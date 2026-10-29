using System.Runtime.Versioning;
using UnitToko.Platform.Integrity;

namespace UnitToko.LocalPilot.Tests;

public sealed class PilotSealsTests
{
    [WindowsPilotFact]
    [SupportedOSPlatform("windows")]
    public void Pilot_requires_seals_and_creates_its_key_ring_once_in_its_protected_key_directory()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnitToko-LocalPilot-Tests", Guid.NewGuid().ToString("N")));
        var previous = ProvenanceSealRing.Current;
        try
        {
            using var state = new PilotState(path);
            var settings = PilotApiFactory.HostSettings(state);
            // The pilot runs like a production deployment: the API refuses to start without seal keys.
            Assert.Equal("true", settings["Integrity:SealsRequired"]);
            Assert.False(File.Exists(Path.Combine(state.KeyDirectory, "seal-current.txt")));
            var id = PilotApiFactory.ProvisionSeals(settings);
            Assert.Equal(id, File.ReadAllText(Path.Combine(state.KeyDirectory, "seal-current.txt")).Trim());
            Assert.Single(Directory.GetFiles(state.KeyDirectory, "seal-key-*.protected"));
            Assert.Equal(id, Assert.IsType<SealKeyRing>(ProvenanceSealRing.Current).CurrentKeyId);
            // A restart keeps the same ring: no new key, same current id.
            Assert.Equal(id, PilotApiFactory.ProvisionSeals(PilotApiFactory.HostSettings(state)));
            Assert.Single(Directory.GetFiles(state.KeyDirectory, "seal-key-*.protected"));
        }
        finally
        {
            ProvenanceSealRing.Configure(previous);
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnitToko-LocalPilot-Tests")) + Path.DirectorySeparatorChar;
            if (path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
