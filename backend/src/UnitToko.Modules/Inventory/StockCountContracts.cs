using System.Collections.Immutable;
using System.Text.Json.Serialization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Inventory;

public sealed record StockCountInput(GrantScope Location, long SkuId, long ExpectedSkuRevision,
    long ExpectedDefinitionId, int Packages, string? ObservedLotLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StockCountPoolSelection? PoolSelection = null);
public sealed record StockCountDefinitionSnapshot(GrantScope Owner, long SkuId, long SkuRevision,
    long FamilyId, long FamilyRevision, long CategoryId, long CategoryRevision,
    long DefinitionId, long DefinitionVersion, string SkuCode, string PhysicalItemKey,
    string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage);
public sealed record StockCountObservation(long Id, GrantScope Location, long CountedBy,
    string Issuer, string Subject, DateTimeOffset RecordedAt, StockCountDefinitionSnapshot Definition,
    int Packages, long BaseUnits, string? ObservedLotLabel, string Baseline, string Coverage,
    long AuditId, string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StockCountPoolBinding? PoolBinding = null);
public sealed record StockCountPage(long Total, ImmutableArray<StockCountObservation> Items);

/// <summary>Physical counting evidence only. A count establishes no balance, eligibility or value.</summary>
public static class StockCountPolicy
{
    public static void Validate(StockCountInput input)
    {
        if (input is null || input.SkuId <= 0 || input.ExpectedSkuRevision <= 0 || input.ExpectedDefinitionId <= 0)
            throw Invalid();
        Location(input.Location);
        input.PoolSelection?.Validate();
        _ = BaseUnits(input.Packages, 1);
        if (input.ObservedLotLabel is { } label && !CanonicalText(label, 160)) throw Invalid();
    }
    public static void Location(GrantScope location)
    {
        if (location is null || location.Kind != GrantScopeKind.FulfillmentWarehouse ||
            location.OrganizationId is not > 0 || location.ShopId is not > 0 ||
            location.WarehouseId is not > 0 || location.ChannelId is not > 0 ||
            location.BranchId is not null || location.DivisionId is not null) throw Invalid();
    }
    public static long BaseUnits(int packages, int contents)
    {
        if (packages is < 0 or > DomainValues.MaximumLineQuantity || contents <= 0) throw Invalid();
        return checked((long)packages * contents);
    }
    public static bool CanonicalText(string? value, int maximum)
    {
        if (value is null || value.Length == 0 || value.Length > maximum || value != value.Trim() || value.Any(char.IsControl)) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || index + 1 >= value.Length || !char.IsLowSurrogate(value[++index])) return false;
        }
        return true;
    }
    private static DomainValidationException Invalid() => new("invalid_stock_count", "Count requires an exact warehouse, catalog revision and bounded package observation");
}
