using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using UnitToko.Api.Identity;
using UnitToko.Platform.Integrity;

namespace UnitToko.Tests.Identity;

// The process-wide seal ring is shared with the seal tests, so these run in the same serial collection and restore it.
[Collection("Mongo")]
public sealed class SealKeyProvisioningTests
{
    private static IConfiguration Configuration(IdentityRuntimeTests.RuntimeKeys keys, bool required = false)
    {
        var settings = IdentityRuntimeTests.Settings(keys);
        if (required) settings["Integrity:SealsRequired"] = "true";
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public void Provisioning_creates_one_protected_current_key_once_and_never_rotates_it()
    {
        var previous = ProvenanceSealRing.Current;
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        try
        {
            var id = IdentityKeyMaterial.ProvisionSealKeys(Configuration(keys));
            // Only the key id comes back; the key itself stays encrypted in the protected directory.
            Assert.Matches(new Regex(@"\A[0-9]{14}-[0-9a-f]{8}\z"), id);
            Assert.Equal(id, File.ReadAllText(Path.Combine(keys.Directory, "seal-current.txt")).Trim());
            var stored = Assert.Single(Directory.GetFiles(keys.Directory, "seal-key-*.protected"));
            Assert.Equal("seal-key-" + id + ".protected", Path.GetFileName(stored));
            // A second provisioning keeps the existing ring: same id, no new key (rotation is a separate operator decision).
            Assert.Equal(id, IdentityKeyMaterial.ProvisionSealKeys(Configuration(keys, required: true)));
            Assert.Single(Directory.GetFiles(keys.Directory, "seal-key-*.protected"));
        }
        finally { ProvenanceSealRing.Configure(previous); }
    }

    [Fact]
    public void Required_seals_refuse_to_start_without_keys_and_load_the_provisioned_ring()
    {
        var previous = ProvenanceSealRing.Current;
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        try
        {
            ProvenanceSealRing.Configure(null);
            Assert.Throws<InvalidOperationException>(() => new IdentityKeyMaterial(Configuration(keys, required: true)));
            Assert.Null(ProvenanceSealRing.Current);
            var id = IdentityKeyMaterial.ProvisionSealKeys(Configuration(keys));
            ProvenanceSealRing.Configure(null);
            using (new IdentityKeyMaterial(Configuration(keys, required: true)))
            {
                var ring = Assert.IsType<SealKeyRing>(ProvenanceSealRing.Current);
                Assert.Equal(id, ring.CurrentKeyId);
                Assert.Equal(32, ring.Key(id).Length);
            }
        }
        finally { ProvenanceSealRing.Configure(previous); }
    }
}
