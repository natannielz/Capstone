using System.Collections.Immutable;
using System.Globalization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Catalog;

/// <summary>A supplied identifier is evidence of an assignment, never allocation or authenticity.
/// Reserved identity fields survive retirement. DefinitionId records the reviewed package.</summary>
public sealed record CatalogIdentifier(LongId Id, LongId OrganizationId, LongId SkuId, string Kind,
    string Namespace, string RawValue, string CanonicalValue, bool IsPrimary, DateTimeOffset EffectiveAt,
    DateTimeOffset? RetiredAt, long Revision, string ProvenanceReference, LongId? DefinitionId);
public sealed record CatalogIdentifierView(CatalogSkuView Catalog, ImmutableArray<CatalogIdentifier> Identifiers);
public sealed record CatalogAssignIdentifier(LongId SkuId, long ExpectedSkuRevision, LongId DefinitionId,
    string Kind, string? SourceAccount, string RawValue, string ProvenanceReference);
public sealed record CatalogRetireIdentifier(LongId SkuId, long ExpectedSkuRevision, LongId IdentifierId,
    long ExpectedIdentifierRevision, string ProvenanceReference);
public sealed record CatalogCorrectSkuCode(LongId SkuId, long ExpectedSkuRevision, LongId PrimaryIdentifierId,
    long ExpectedIdentifierRevision, string NewCode, string ProvenanceReference);
public sealed record CatalogIdentifierResult(LongId SkuId, long SkuRevision, LongId IdentifierId,
    long IdentifierRevision, DateTimeOffset RecordedAt);
public sealed record CatalogIdentifierSelection(LongId IdentifierId, long IdentifierRevision, LongId SkuId,
    LongId DefinitionId, long DefinitionVersion, string Kind, string Namespace, string CanonicalValue);

public static class CatalogIdentifierPolicy
{
    public static string Canonical(string kind, string raw) => kind switch
    {
        "skuCode" => CatalogPolicy.NormalizeSkuCode(raw),
        "gtin" => Gtin.Parse(raw).Canonical14,
        "supplier" or "omi" or "smart" => ExactText(raw, 128),
        _ => throw Invalid()
    };
    public static string Namespace(string kind, GrantScope owner, string? sourceAccount)
    {
        CatalogAuthoringPolicy.Owner(owner);
        if (kind is "skuCode" or "gtin")
        {
            if (sourceAccount is not null) throw Invalid();
            // GTIN uniqueness follows the precise independent authoring catalog; SKU aliases
            // share the original organization-wide namespace with primary reservations.
            return kind == "skuCode" ? "internal" : CatalogNamespace(owner);
        }
        if (kind is not ("supplier" or "omi" or "smart") || sourceAccount is null) throw Invalid();
        return CatalogNamespace(owner) + ":account:" + ExactText(sourceAccount, 128);
    }
    public static string ExactText(string value, int maximum)
    {
        if (CatalogAuthoringPolicy.Text(value, maximum) != value) throw Invalid();
        return value;
    }
    public static void Validate(CatalogIdentifier value)
    {
        if (value is null || value.Id.Value <= 0 || value.OrganizationId.Value <= 0 || value.SkuId.Value <= 0 ||
            value.Revision <= 0 || value.DefinitionId is { Value: <= 0 } || value.EffectiveAt <= DateTimeOffset.MinValue ||
            value.RetiredAt is { } retired && retired < value.EffectiveAt ||
            value.CanonicalValue != Canonical(value.Kind, value.RawValue) ||
            ExactText(value.Namespace, 256) != value.Namespace || ExactText(value.ProvenanceReference, 500) != value.ProvenanceReference ||
            value.Kind == "skuCode" && value.Namespace != "internal" ||
            value.IsPrimary && (value.Kind != "skuCode" || value.RetiredAt is not null) ||
            value.Kind != "skuCode" && value.DefinitionId is null) throw Invalid();
    }
    private static string CatalogNamespace(GrantScope owner) => "catalog:" + Number(owner.ShopId!.Value) + ":" +
        Number(owner.ChannelId!.Value) + ":" + (owner.WarehouseId is { } warehouse ? Number(warehouse) : "shop");
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    public static DomainValidationException Invalid() => new("invalid_catalog_identifier", "Supply the exact identifier, package and provenance; an identifier never establishes ownership or authenticity");
}
