using System.Text.Json.Serialization;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;

namespace UnitToko.Api.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationalScanSelectionDto([property: JsonRequired] CatalogScanIdentifierDto? Identifier,
    [property: JsonRequired] ApprovedSourceScanDto? Source)
{
    internal static OperationalScanSelection[]? DomainAll(IReadOnlyList<OperationalScanSelectionDto>? values)
    {
        if (values is null || values.Count == 0) return null;
        if (values.Count > 100 || values.Any(value => value is null)) throw ScanRules.Invalid();
        return values.Select(value => value.Domain()).ToArray();
    }
    internal OperationalScanSelection Domain()
    {
        if ((Identifier is null) == (Source is null)) throw ScanRules.Invalid();
        return new(Identifier?.Domain(), Source?.Domain());
    }
    internal static OperationalScanSelectionDto From(OperationalScanSelection value) => new(
        value.Identifier is { } id ? new(id.IdentifierId, new(id.IdentifierRevision), id.SkuId, id.DefinitionId, new(id.DefinitionVersion), id.Kind, id.Namespace, id.CanonicalValue) : null,
        value.Source is { } source ? new(source.OrderId, new(source.OrderVersion), source.InspectionId, new(source.InspectionRevision), source.InspectionLineNumber,
            source.SkuId, source.DefinitionId, new(source.DefinitionVersion), source.SourceSystem, source.SourceAccount, source.SourceCode) : null);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogScanIdentifierDto([property: JsonRequired] LongId IdentifierId, [property: JsonRequired] LongId IdentifierRevision,
    [property: JsonRequired] LongId SkuId, [property: JsonRequired] LongId DefinitionId, [property: JsonRequired] LongId DefinitionVersion,
    [property: JsonRequired] string Kind, [property: JsonRequired] string Namespace, [property: JsonRequired] string CanonicalValue)
{
    internal CatalogIdentifierSelection Domain() => new(IdentifierId, IdentifierRevision.Value, SkuId, DefinitionId, DefinitionVersion.Value, Kind, Namespace, CanonicalValue);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApprovedSourceScanDto([property: JsonRequired] LongId OrderId, [property: JsonRequired] LongId OrderVersion,
    [property: JsonRequired] LongId InspectionId, [property: JsonRequired] LongId InspectionRevision, [property: JsonRequired] int InspectionLineNumber,
    [property: JsonRequired] LongId SkuId, [property: JsonRequired] LongId DefinitionId, [property: JsonRequired] LongId DefinitionVersion,
    [property: JsonRequired] string SourceSystem, [property: JsonRequired] string SourceAccount, [property: JsonRequired] string SourceCode)
{
    internal ApprovedSourceScanSelection Domain() => new(OrderId, OrderVersion.Value, InspectionId, InspectionRevision.Value, InspectionLineNumber,
        SkuId, DefinitionId, DefinitionVersion.Value, SourceSystem, SourceAccount, SourceCode);
}
