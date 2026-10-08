using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Catalog;

public sealed class CatalogIdentifierPolicyTests
{
    private static readonly GrantScope Owner = new(GrantScopeKind.FulfillmentWarehouse, 1, ShopId: 2, WarehouseId: 3, ChannelId: 4);
    [Fact]
    public void Legitimate_supplied_gtin_keeps_raw_digits_and_canonical_equivalent_forms_share_one_reservation()
    {
        var plain = CatalogIdentifierPolicy.Canonical("gtin", "4006381333931");
        var padded = CatalogIdentifierPolicy.Canonical("gtin", "04006381333931");
        Assert.Equal("04006381333931", plain); Assert.Equal(plain, padded);
        var identifier = new CatalogIdentifier(new(1), new(1), new(2), "gtin", CatalogIdentifierPolicy.Namespace("gtin", Owner, null),
            "4006381333931", plain, false, DateTimeOffset.Parse("2026-10-08T00:00:00Z"), null, 1, "Explicitly supplied manufacturer package reference", new(3));
        CatalogIdentifierPolicy.Validate(identifier); Assert.Equal("4006381333931", identifier.RawValue);
        Assert.Throws<DomainValidationException>(() => CatalogIdentifierPolicy.Validate(identifier with { IsPrimary = true }));
        Assert.Throws<DomainValidationException>(() => CatalogIdentifierPolicy.Validate(identifier with { DefinitionId = null }));
    }
    [Theory]
    [InlineData("4006381333932")]
    [InlineData(" 4006381333931")]
    [InlineData("４006381333931")]
    [InlineData("")]
    public void Checksum_and_ascii_validation_never_fabricate_an_identifier(string supplied) =>
        Assert.Throws<DomainValidationException>(() => CatalogIdentifierPolicy.Canonical("gtin", supplied));
    [Fact]
    public void Primary_and_alias_sku_codes_share_original_namespace_while_external_codes_keep_catalog_and_account_scope()
    {
        Assert.Equal("internal", CatalogIdentifierPolicy.Namespace("skuCode", Owner, null));
        Assert.Equal("CODE-1", CatalogIdentifierPolicy.Canonical("skuCode", "code-1"));
        Assert.Equal("abc", CatalogIdentifierPolicy.Canonical("supplier", "abc"));
        Assert.NotEqual(CatalogIdentifierPolicy.Namespace("supplier", Owner, "account-a"), CatalogIdentifierPolicy.Namespace("supplier", Owner, "account-b"));
        Assert.NotEqual(CatalogIdentifierPolicy.Namespace("supplier", Owner, "account-a"), CatalogIdentifierPolicy.Namespace("supplier", Owner with { ChannelId = 5 }, "account-a"));
        Assert.NotEqual(CatalogIdentifierPolicy.Namespace("gtin", Owner, null), CatalogIdentifierPolicy.Namespace("gtin", Owner with { ChannelId = 5 }, null));
        Assert.Throws<DomainValidationException>(() => CatalogIdentifierPolicy.Namespace("gtin", Owner, "invented-owner"));
        Assert.Throws<DomainValidationException>(() => CatalogIdentifierPolicy.Namespace("supplier", Owner, null));
    }
    [Fact]
    public void Retired_reservation_retains_its_identity_and_cannot_remain_primary()
    {
        var at = DateTimeOffset.Parse("2026-10-08T00:00:00Z");
        var identifier = new CatalogIdentifier(new(10), new(1), new(20), "skuCode", "internal", "OLD-1", "OLD-1", false,
            at, at.AddDays(1), 2, "Reviewed obsolete package label", new(30));
        CatalogIdentifierPolicy.Validate(identifier);
        Assert.Throws<DomainValidationException>(() => CatalogIdentifierPolicy.Validate(identifier with { IsPrimary = true }));
        Assert.Throws<DomainValidationException>(() => CatalogIdentifierPolicy.Validate(identifier with { RetiredAt = at.AddSeconds(-1) }));
        Assert.Throws<DomainValidationException>(() => CatalogIdentifierPolicy.Validate(identifier with { Namespace = "alternate" }));
        Assert.Throws<DomainValidationException>(() => CatalogIdentifierPolicy.Validate(identifier with { ProvenanceReference = " " }));
    }
}
