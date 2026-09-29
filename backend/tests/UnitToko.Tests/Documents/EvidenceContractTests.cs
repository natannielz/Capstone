using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;

namespace UnitToko.Tests.Documents;

public sealed class EvidenceContractTests
{
    private static readonly string Hash = new('a', 64);

    [Theory]
    [InlineData("validated", "clean", "image/jpeg", true, true)]
    [InlineData("validated", "clean", "image/png", true, true)]
    [InlineData("quarantined", "pending", "image/jpeg", true, false)]
    [InlineData("validated", "clean", "application/pdf", true, false)]
    [InlineData("validated", "clean", "image/jpeg", false, false)]
    [InlineData("failed", "infected", "image/png", true, false)]
    [InlineData("deleting", "clean", "image/png", true, false)]
    [InlineData("deleted", "clean", "image/png", true, false)]
    [InlineData("validated", "unavailable", "image/png", true, false)]
    [InlineData("validated", "clean", "image/png\n", true, false)]
    [InlineData(null, "clean", "image/png", true, false)]
    public void Payment_requires_present_validated_clean_image(
        string? state, string? scan, string? mime, bool bytesPresent, bool expected) =>
        Assert.Equal(expected, EvidencePolicy.CanSubmitPayment(state, scan, mime, bytesPresent));

    [Fact]
    public void Immutable_reference_preserves_lineage_version_and_exact_hash()
    {
        var reference = new EvidenceRef(long.MaxValue, 2, Hash);
        Assert.Equal(long.MaxValue, reference.Id);
        Assert.Equal(2, reference.Version);
        Assert.Equal(Hash, reference.Sha256);
        Assert.Equal(reference, new EvidenceRef(long.MaxValue, 2, Hash));
        Assert.NotEqual(reference, new EvidenceRef(long.MaxValue, 3, Hash));
        Assert.NotEqual(reference, new EvidenceRef(long.MaxValue, 2, new string('b', 64)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void Reference_rejects_nonpositive_identity_or_version(long id, long version) =>
        Assert.Throws<DomainValidationException>(() => new EvidenceRef(id, version, Hash));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaag")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    public void Reference_rejects_noncanonical_digest(string? hash) =>
        Assert.Throws<DomainValidationException>(() => new EvidenceRef(1, 1, hash!));

    [Theory]
    [InlineData(EvidenceTargetKind.CatalogFamily)]
    [InlineData(EvidenceTargetKind.CatalogSku)]
    [InlineData(EvidenceTargetKind.PaymentDeclaration)]
    public void Supported_target_preserves_exact_root_identity(EvidenceTargetKind kind)
    {
        var target = new EvidenceTargetRef(kind, long.MaxValue);
        Assert.Equal(kind, target.Kind);
        Assert.Equal(long.MaxValue, target.Id);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(999, 1)]
    [InlineData(1, 0)]
    [InlineData(2, -1)]
    public void Target_rejects_unknown_kind_or_invalid_identity(int kind, long id) =>
        Assert.Throws<DomainValidationException>(() => new EvidenceTargetRef((EvidenceTargetKind)kind, id));

    [Fact]
    public void Image_limits_enforce_actual_dimensions_and_single_complete_image()
    {
        var limits = new EvidenceLimits(100, 1000, 1000, 500_000);
        Assert.True(limits.AcceptsImage(100, 1000, 500, 1, 1));
        Assert.False(limits.AcceptsImage(101, 1000, 500, 1, 1));
        Assert.False(limits.AcceptsImage(100, 1001, 1, 1, 1));
        Assert.False(limits.AcceptsImage(100, 1, 1001, 1, 1));
        Assert.False(limits.AcceptsImage(100, 1000, 501, 1, 1));
        Assert.False(limits.AcceptsImage(0, 1, 1, 1, 1));
        Assert.False(limits.AcceptsImage(1, 0, 1, 1, 1));
        Assert.False(limits.AcceptsImage(1, 1, -1, 1, 1));
        Assert.False(limits.AcceptsImage(1, 1, 1, 0, 1));
        Assert.False(limits.AcceptsImage(1, 1, 1, 2, 1));
        Assert.False(limits.AcceptsImage(1, 1, 1, 1, 0));
        Assert.False(limits.AcceptsImage(1, 1, 1, 1, 2));
    }

    [Fact]
    public void Pixel_product_cannot_wrap_Int32_to_bypass_limit()
    {
        var limits = new EvidenceLimits(100, int.MaxValue, int.MaxValue, 1_000_000);
        Assert.False(limits.AcceptsImage(10, 50_000, 50_000, 1, 1));
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(-1, 1, 1, 1)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, 1, -1, 1)]
    [InlineData(1, 1, 1, 0)]
    public void Unconfigured_or_invalid_limits_cannot_enable_upload(
        long bytes, int width, int height, long pixels) =>
        Assert.Throws<DomainValidationException>(() => new EvidenceLimits(bytes, width, height, pixels));
}
