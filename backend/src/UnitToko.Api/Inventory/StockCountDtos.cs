using System.Globalization;
using System.Text.Json.Serialization;
using UnitToko.Api.Authorization;
using UnitToko.Api.Catalog;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;

namespace UnitToko.Api.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockCountReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockCountPageQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccessScopeDto Location, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockCountPackageQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccessScopeDto Location, string? Search = null, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockCountSourceQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccessScopeDto Location, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockCountPoolSelectionDto([property: JsonRequired] LongId PoolId,
    [property: JsonRequired] LongId ExpectedPoolRevision, [property: JsonRequired] string ConfigurationHash);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockCountRecordDto([property: JsonRequired] AccessScopeDto Location,
    [property: JsonRequired] LongId SkuId, [property: JsonRequired] LongId ExpectedSkuRevision,
    [property: JsonRequired] LongId ExpectedDefinitionId, [property: JsonRequired] int Packages,
    [property: JsonRequired] string Reason, string? ObservedLotLabel = null, StockCountPoolSelectionDto? PoolSelection = null,
    OperationalScanSelectionDto[]? ScanSelections = null)
{
    public StockCountInput ToDomain()
    {
        var input = new StockCountInput(StockCountInputs.Location(Location), SkuId.Value, ExpectedSkuRevision.Value,
            ExpectedDefinitionId.Value, Packages, ObservedLotLabel, PoolSelection is { } selected
                ? new StockCountPoolSelection(selected.PoolId.Value, selected.ExpectedPoolRevision.Value, selected.ConfigurationHash) : null);
        StockCountPolicy.Validate(input);
        return input;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockCountReceiptQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record StockCountWarehouseOptionDto(AccessScopeDto Location, string OrganizationName, string ShopName,
    string WarehouseName, string ChannelName);
public sealed record StockCountWarehouseOptionsDto(StockCountWarehouseOptionDto[] Items);
public sealed record StockCountPackageOptionDto(LongId SkuId, string SkuRevision, LongId DefinitionId, string DefinitionVersion,
    string SkuCode, string FamilyName, string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage);
public sealed record StockCountPackagePageDto(string Total, StockCountPackageOptionDto[] Items);
public sealed record StockCountPoolBindingDto(LongId PoolId, string PoolRevision, LongId SetupDecisionId,
    string ConfigurationHash, string PhysicalKey, string Label, bool Active);
public sealed record StockCountSourceChoiceDto(StockCountPoolBindingDto Pool, AccessScopeDto RequestedLocation);
public sealed record StockCountSourcePageDto(string Total, StockCountSourceChoiceDto[] Items);
public sealed record StockCountObservationDto(LongId Id, string Version, AccessScopeDto Location, DateTimeOffset RecordedAt,
    LongId SkuId, string SkuCode, LongId DefinitionId, string DefinitionVersion,
    string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage,
    int Packages, string BaseUnits, string? ObservedLotLabel, string Reason, string Baseline, string Coverage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StockCountPoolBindingDto? PoolBinding = null);
public sealed record StockCountObservationPageDto(string Total, StockCountObservationDto[] Items);
public sealed record StockCountReceiptResultDto(LongId Id, string Version);
public sealed record StockCountReceiptDto(string State, StockCountReceiptResultDto? Result, DateTimeOffset? RecordedAt);

internal static class StockCountInputs
{
    internal static GrantScope Location(AccessScopeDto? input)
    {
        if (input is null) throw new DomainValidationException("invalid_stock_count", "Choose an exact warehouse");
        var location = input.ToDomain(); StockCountPolicy.Location(location); return location;
    }
}
internal static class StockCountResponses
{
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static StockCountWarehouseOptionsDto Warehouses(IEnumerable<StockWarehouseOption> items) =>
        new([.. items.Select(item => new StockCountWarehouseOptionDto(AccessScopeDto.From(item.Location),
            item.OrganizationName, item.ShopName, item.WarehouseName, item.ChannelName))]);
    internal static StockCountPackagePageDto Packages(CatalogPage<StockCountPackageOption> page) =>
        new(Number(page.Total), [.. page.Items.Select(item => new StockCountPackageOptionDto(item.SkuId,
            Number(item.SkuRevision), item.DefinitionId, Number(item.DefinitionVersion), item.SkuCode, item.FamilyName,
            item.PackageLabel, item.PackageKind, item.BaseUnitCode, item.BaseUnitsPerPackage))]);
    internal static StockCountPoolBindingDto Pool(StockCountPoolBinding item) =>
        new(new(item.PoolId), Number(item.PoolRevision), new(item.SetupDecisionId), item.ConfigurationHash,
            item.PhysicalKey, item.Label, item.Active);
    internal static StockCountSourcePageDto Sources(StockCountSourcePage page) =>
        new(Number(page.Total), [.. page.Items.Select(item => new StockCountSourceChoiceDto(Pool(item.Pool),
            AccessScopeDto.From(item.RequestedLocation)))]);
    internal static StockCountObservationDto Observation(StockCountObservation item) => new(new(item.Id), "1",
        AccessScopeDto.From(item.Location), item.RecordedAt, new(item.Definition.SkuId), item.Definition.SkuCode,
        new(item.Definition.DefinitionId), Number(item.Definition.DefinitionVersion), item.Definition.PackageLabel,
        item.Definition.PackageKind, item.Definition.BaseUnitCode, item.Definition.BaseUnitsPerPackage,
        item.Packages, Number(item.BaseUnits), item.ObservedLotLabel, item.Reason, item.Baseline, item.Coverage,
        item.PoolBinding is { } binding ? Pool(binding) : null);
    internal static StockCountObservationPageDto Page(StockCountPage page) => new(Number(page.Total), [.. page.Items.Select(Observation)]);
    internal static StockCountReceiptDto Receipt(StockCountObservation? item) => item is null
        ? new("not_confirmed", null, null) : new("committed", new(new(item.Id), "1"), item.RecordedAt);
}
