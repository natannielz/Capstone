using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Api.Catalog;

namespace UnitToko.Tests.Catalog;

public sealed class ScanResolverTests
{
    [Fact]
    public void Transport_rejects_null_or_unbounded_scan_entries_and_preserves_manual_absence()
    {
        Assert.Null(OperationalScanSelectionDto.DomainAll(null));
        Assert.Null(OperationalScanSelectionDto.DomainAll([]));
        Assert.Throws<DomainValidationException>(() => OperationalScanSelectionDto.DomainAll([null!]));
        Assert.Throws<DomainValidationException>(() => OperationalScanSelectionDto.DomainAll([new(null, null)]));
        var dto = OperationalScanSelectionDto.From(Box.Selection!);
        Assert.Equal(Box.Selection, Assert.Single(OperationalScanSelectionDto.DomainAll([dto])!));
        Assert.Throws<DomainValidationException>(() => OperationalScanSelectionDto.DomainAll(Enumerable.Repeat(dto, 101).ToArray()));
    }
    private static readonly GrantScope Location = new(GrantScopeKind.FulfillmentWarehouse, OrganizationId: 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
    private static readonly ScanCandidate Box = new("20:1", new(31), new(32), 1, "PENCIL-12", "Dus isi 12", "sealed", "piece", 12,
        new(new(new(101), 1, new(31), new(32), 1, "skuCode", "internal", "PENCIL-12"), null));
    private static readonly ScanCandidate Loose = new("20:2", new(41), new(42), 1, "PENCIL-1", "Satuan", "loose", "piece", 1,
        new(new(new(102), 1, new(41), new(42), 1, "skuCode", "internal", "PENCIL-1"), null));
    private static ScanRequest Receiving(string code, string kind = "sku", string? source = null, string? account = null) =>
        new("receiving", kind, code, source, account, Location, 10, 1, 20, 1, null);

    [Fact]
    public void Exact_code_preserves_package_identity_and_never_changes_quantity()
    {
        var match = ScanRules.Resolve(Receiving("PENCIL-12"), [new(Box), new(Loose)]);
        Assert.Equal("match", match.Kind); Assert.Equal(Box, Assert.Single(match.Candidates)); Assert.Equal(12, match.Candidates[0].BaseUnitsPerPackage);
        var loose = ScanRules.Resolve(Receiving("PENCIL-1"), [new(Box), new(Loose)]); Assert.Equal(Loose, Assert.Single(loose.Candidates));
        Assert.Equal("unknown", ScanRules.Resolve(Receiving("PENC1L-12"), [new(Box), new(Loose)]).Kind);
        Assert.Equal("unknown", ScanRules.Resolve(Receiving("12"), [new(Box), new(Loose)]).Kind);
    }
    [Fact]
    public void Duplicate_physical_document_targets_are_ambiguous_even_for_same_SKU()
    {
        var result = ScanRules.Resolve(Receiving("PENCIL-12"), [new(Box), new(Box with {TargetReference = "20:3"})]);
        Assert.Equal("ambiguous", result.Kind); Assert.Equal(2, result.Candidates.Length);
    }
    [Fact]
    public void Supplier_code_requires_exact_source_namespace_and_account()
    {
        var source = new ScanSourceCandidate(Box with {Selection = new(null, new(new(10), 1, new(20), 1, 1, Box.SkuId,
            Box.DefinitionId, 1, "omi", "STORE-A", "00123"))}, "omi", "STORE-A", "00123");
        Assert.Equal("match", ScanRules.Resolve(Receiving("00123", "supplier", "omi", "STORE-A"), [source]).Kind);
        foreach (var request in new[] {Receiving("123", "supplier", "omi", "STORE-A"), Receiving("00123", "supplier", "supplier", "STORE-A"), Receiving("00123", "supplier", "omi", "STORE-B")})
            Assert.Equal("unknown", ScanRules.Resolve(request, [source]).Kind);
        Assert.Throws<DomainValidationException>(() => ScanRules.Resolve(Receiving("00123", "supplier"), [source]));
    }
    [Theory]
    [InlineData("4006381333931")]
    [InlineData("04006381333931")]
    public void Valid_GTIN_does_not_invent_an_approved_mapping(string raw)
    {
        var result = ScanRules.Resolve(Receiving(raw, "gtin"), [new(Box)]);
        Assert.Equal("unknown", result.Kind); Assert.Equal("code_not_in_context", result.Reason); Assert.Empty(result.Candidates);
    }
    [Theory]
    [InlineData("4006381333932")]
    [InlineData("４006381333931")]
    [InlineData(" 4006381333931")]
    public void Invalid_GTIN_or_scanner_text_stops_without_candidates(string raw)
    {
        var result = ScanRules.Resolve(Receiving(raw, "gtin"), [new(Box)]); Assert.Equal("invalid", result.Kind); Assert.Empty(result.Candidates);
    }
    [Fact]
    public void Picking_dispatch_and_stocktake_require_their_exact_context_shapes()
    {
        var pick = new ScanRequest("picking", "sku", "PENCIL-12", null, null, null, 10, 1, null, null, new('a', 64));
        Assert.Equal("match", ScanRules.Resolve(pick, [new(Box)]).Kind);
        var dispatch = pick with {Purpose = "dispatch", AuxiliaryId = 40, AuxiliaryRevision = 2};
        Assert.Equal("match", ScanRules.Resolve(dispatch, [new(Box)]).Kind);
        var count = new ScanRequest("stocktake", "sku", "PENCIL-12", null, null, Location, null, null, null, null, null);
        Assert.Equal("match", ScanRules.Resolve(count, [new(Box)]).Kind);
        foreach (var malformed in new[] {pick with {Location = Location}, pick with {DocumentRevision = null}, dispatch with {AuxiliaryRevision = null}, count with {DocumentId = 10}, count with {Location = null}})
            Assert.Throws<DomainValidationException>(() => ScanRules.Resolve(malformed, [new(Box)]));
    }
}
