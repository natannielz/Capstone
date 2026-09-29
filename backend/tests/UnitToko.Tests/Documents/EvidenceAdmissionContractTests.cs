using System.Text.Json;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;

namespace UnitToko.Tests.Documents;

public sealed class EvidenceAdmissionContractTests
{
    [Fact]
    public void Payment_purpose_is_bound_to_payment_owner_only() {
        var payment = new EvidenceTargetRef(EvidenceTargetKind.PaymentDeclaration, 9);
        Assert.Equal(payment, new ReserveEvidenceImage(payment, EvidencePurpose.PaymentImage, 1).Target);
        Assert.Throws<DomainValidationException>(() => new ReserveCatalogImage(payment));
        Assert.Throws<DomainValidationException>(() => new ReserveEvidenceImage(payment, EvidencePurpose.CatalogImage, 1));
        Assert.Throws<DomainValidationException>(() => new ReserveEvidenceImage(new(EvidenceTargetKind.CatalogSku, 9), EvidencePurpose.PaymentImage, 1));
    }
    [Fact]
    public void Request_accepts_only_catalog_image_and_positive_optional_revision()
    {
        var target = new EvidenceTargetRef(EvidenceTargetKind.CatalogSku, 9);
        var request = new ReserveCatalogImage(target, expectedTargetRevision: 2, filename: "cosmetic.jpg");
        Assert.Equal(target, request.Target);
        Assert.Equal(EvidencePurpose.CatalogImage, request.Purpose);
        Assert.Equal(2, request.ExpectedTargetRevision);
        Assert.Null(new ReserveCatalogImage(target).ExpectedTargetRevision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Request_rejects_invalid_revision(long revision) =>
        Assert.Throws<DomainValidationException>(() => new ReserveCatalogImage(
            new(EvidenceTargetKind.CatalogFamily, 1), expectedTargetRevision: revision));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Request_rejects_unsupported_purpose(int purpose) =>
        Assert.Throws<DomainValidationException>(() => new ReserveCatalogImage(
            new(EvidenceTargetKind.CatalogFamily, 1), (EvidencePurpose)purpose));

    [Fact]
    public void Missing_target_is_denied() =>
        Assert.Throws<DomainValidationException>(() => new ReserveCatalogImage(null!));

    [Fact]
    public void Store_is_unavailable_until_real_local_activation()
    {
        var registration = EvidenceStoreRegistration.Unavailable();
        Assert.Null(registration.Store);
        Assert.Equal("storage_unavailable", registration.Reason);
        Assert.Null(EvidenceStoreRegistration.OpenLocal("Z:/missing-private-store", new string('a', 64), []).Store);
    }

    [Fact]
    public void Safe_status_contains_no_internal_claim_or_storage_address()
    {
        var result = new SafeStatus(1, "reserved", 1, DateTimeOffset.UtcNow, null,
            "unavailable", "unavailable", null);
        var json = JsonSerializer.Serialize(result).ToLowerInvariant();
        foreach (var forbidden in new[] { "token", "generation", "storeid", "issuer", "subject", "owner", "path" })
            Assert.DoesNotContain(forbidden, json);
    }
}
