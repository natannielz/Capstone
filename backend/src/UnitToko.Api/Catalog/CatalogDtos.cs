using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Api.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CatalogOwnerPageQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccessScopeDto Owner, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CatalogFamilyQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccessScopeDto Owner, string? Search = null, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CatalogSkuQueryDto([property: JsonRequired] string Purpose, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogReceiptQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogCategorySaveDto([property: JsonRequired] AccessScopeDto Owner,
    [property: JsonRequired] LongId? CategoryId, [property: JsonRequired] string Code,
    [property: JsonRequired] string Name, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogFamilyCreateDto([property: JsonRequired] AccessScopeDto Owner,
    [property: JsonRequired] LongId CategoryId, [property: JsonRequired] string Name,
    [property: JsonRequired] string Description, [property: JsonRequired] LongId[] ImageAssetIds,
    [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogFamilyEditDto([property: JsonRequired] LongId CategoryId,
    [property: JsonRequired] string Name, [property: JsonRequired] string Description,
    [property: JsonRequired] LongId[] ImageAssetIds, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogAttributeDto([property: JsonRequired] string Key, [property: JsonRequired] string Value);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CatalogDefinitionInputDto([property: JsonRequired] string PackageLabel,
    [property: JsonRequired] string PackageKind, [property: JsonRequired] string BaseUnitCode,
    [property: JsonRequired] int BaseUnitsPerPackage, [property: JsonRequired] CatalogAttributeDto[] Attributes,
    [property: JsonRequired] LongId[] ImageAssetIds, [property: JsonRequired] int MinimumOrderPackages,
    [property: JsonRequired] int OrderIncrementPackages)
{
    public DefinitionInput ToDomain()
    {
        if (Attributes is null || Attributes.Length > 32) throw CatalogInputs.Invalid();
        var attributes = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var attribute in Attributes)
        {
            if (attribute is null || attribute.Key is null || attribute.Value is null || attributes.ContainsKey(attribute.Key))
                throw CatalogInputs.Invalid();
            attributes.Add(attribute.Key, attribute.Value);
        }
        return new(CatalogInputs.Text(PackageLabel), CatalogInputs.Text(PackageKind), CatalogInputs.Text(BaseUnitCode),
            BaseUnitsPerPackage, attributes.ToImmutable(), CatalogInputs.Images(ImageAssetIds),
            MinimumOrderPackages, OrderIncrementPackages);
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogSkuCreateDto([property: JsonRequired] string Code,
    [property: JsonRequired] string PhysicalItemKey, [property: JsonRequired] CatalogDefinitionInputDto Definition,
    [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogDefinitionSaveDto([property: JsonRequired] CatalogDefinitionInputDto Definition,
    [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogReasonDto([property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogAssortmentVersionDto([property: JsonRequired] LongId Id, [property: JsonRequired] string Version);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPublicationSaveDto([property: JsonRequired] LongId[] AssortmentIds,
    [property: JsonRequired] CatalogAssortmentVersionDto[] ExpectedAssortments, [property: JsonRequired] string Reason)
{
    public CatalogSetPublications ToDomain(LongId skuId, long version)
    {
        if (AssortmentIds is null || AssortmentIds.Length > 128 || ExpectedAssortments is null ||
            ExpectedAssortments.Length > 256 || AssortmentIds.Distinct().Count() != AssortmentIds.Length ||
            ExpectedAssortments.Any(item => item is null) ||
            ExpectedAssortments.Select(item => item.Id).Distinct().Count() != ExpectedAssortments.Length)
            throw CatalogInputs.Invalid();
        return new(skuId, version, [.. AssortmentIds],
            [.. ExpectedAssortments.Select(item => new CatalogAssortmentVersion(item.Id, CatalogInputs.PositiveVersion(item.Version)))]);
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CatalogVariantQueryDto(int Offset = 0, int Limit = 25, string? Search = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogVariantReadDto;

public sealed record CatalogOwnerOptionDto(AccessScopeDto Owner, string Label);
public sealed record CatalogOwnerOptionsDto(CatalogOwnerOptionDto[] Items);
public sealed record CatalogCategoryDto(LongId Id, LongId OrganizationId, AccessScopeDto Owner, string Code,
    string Name, bool Active, string Version);
public sealed record CatalogCategoryPageDto(string Total, CatalogCategoryDto[] Items);
public sealed record CatalogFamilyDto(LongId Id, LongId OrganizationId, AccessScopeDto Owner, string PublicId,
    LongId CategoryId, string CategoryCode, string Name, string Description, bool Active, string Version);
public sealed record CatalogFamilyPageDto(string Total, CatalogFamilyDto[] Items);
public sealed record CatalogDefinitionDto(LongId Id, LongId SkuId, string Version, string PackageLabel,
    string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage, CatalogAttributeDto[] Attributes,
    int MinimumOrderPackages, int OrderIncrementPackages, DateTimeOffset EffectiveAt);
public sealed record CatalogPublicationDto(LongId AssortmentId, AccessScopeDto Owner, string Version, string Status);
public sealed record CatalogSkuDto(LongId Id, LongId OrganizationId, LongId FamilyId, string PublicId, string Code,
    string PhysicalItemKey, bool Active, LongId CurrentDefinitionId, string Version, LongId? PredecessorSkuId,
    AccessScopeDto Owner, CatalogDefinitionDto Definition, CatalogPublicationDto[] Publications);
public sealed record CatalogSkuPageDto(string Total, CatalogSkuDto[] Items);
public sealed record CatalogAssortmentDto(LongId Id, AccessScopeDto Owner, string Version, string Status, string Label);
public sealed record CatalogAssortmentPageDto(string Total, CatalogAssortmentDto[] Items);
public sealed record CatalogMutationResultDto(string Kind, LongId Id, string Version, LongId? FamilyId,
    LongId? CurrentDefinitionId, string? DefinitionVersion, LongId? PredecessorSkuId, DateTimeOffset RecordedAt);
public sealed record CatalogReceiptDto(string State, CatalogMutationResultDto? Result, DateTimeOffset? RecordedAt);
public sealed record CatalogPublishedVariantDto(LongId FamilyId, string FamilyPublicId, string FamilyName,
    string Description, string CategoryCode, LongId SkuId, string PublicId, string Code, CatalogDefinitionDto Definition);
public sealed record CatalogPublishedVariantPageDto(string Total, CatalogPublishedVariantDto[] Items);

internal static class CatalogInputs
{
    internal static DomainValidationException Invalid() => new("invalid_catalog_input", "Complete the bounded catalog fields");
    internal static string Text(string value) => value ?? throw Invalid();
    internal static GrantScope Owner(AccessScopeDto value)
    {
        if (value is null || value.Kind is not (GrantScopeKind.FulfillmentShop or GrantScopeKind.FulfillmentWarehouse) ||
            value.BranchId is not null || value.DivisionId is not null) throw Invalid();
        return value.ToDomain();
    }
    internal static ImmutableArray<LongId> Images(LongId[] values)
    {
        if (values is null) throw Invalid();
        if (values.Length != 0) throw new DomainValidationException("catalog_image_not_eligible", "Catalog images require an eligible asset");
        return [];
    }
    internal static DefinitionInput Definition(CatalogDefinitionInputDto value) => value?.ToDomain() ?? throw Invalid();
    internal static long PositiveVersion(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 19 || value[0] is < '1' or > '9' ||
            value.Any(character => character is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw new DomainValidationException("invalid_version", "Choose a positive Int64 version");
        return version;
    }
}
internal static class CatalogResponses
{
    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static CatalogOwnerOptionsDto From(ImmutableArray<CatalogOwnerOption> items) =>
        new([.. items.Select(item => new CatalogOwnerOptionDto(AccessScopeDto.From(item.Owner), item.Label))]);
    internal static CatalogCategoryDto From(CatalogCategory item) => new(item.Id, item.OrganizationId,
        AccessScopeDto.From(item.AuthoringOwner), item.Code, item.Name, item.Active, Number(item.Revision));
    internal static CatalogCategoryPageDto From(CatalogPage<CatalogCategory> page) => new(Number(page.Total), [.. page.Items.Select(From)]);
    internal static CatalogFamilyDto From(ProductFamily item) => new(item.Id, item.OrganizationId,
        AccessScopeDto.From(item.AuthoringOwner), item.PublicId.ToString("D"), item.CategoryId, item.CategoryCode,
        item.Name, item.Description, item.Active, Number(item.Revision));
    internal static CatalogFamilyPageDto From(CatalogPage<ProductFamily> page) => new(Number(page.Total), [.. page.Items.Select(From)]);
    internal static CatalogDefinitionDto From(VariantDefinitionVersion item) => new(item.Id, item.SkuId, Number(item.Version),
        item.PackageLabel, item.PackageKind, item.BaseUnitCode, item.BaseUnitsPerPackage,
        [.. item.Attributes.Select(attribute => new CatalogAttributeDto(attribute.Key, attribute.Value))],
        item.MinimumOrderPackages, item.OrderIncrementPackages, item.EffectiveAt);
    internal static CatalogSkuDto From(CatalogSkuView item) => new(item.Sku.Id, item.Sku.OrganizationId, item.Sku.FamilyId,
        item.Sku.PublicId.ToString("D"), item.Sku.Code, item.Sku.PhysicalItemKey, item.Sku.Active, item.Sku.CurrentDefinitionId,
        Number(item.Sku.Revision), item.Sku.PredecessorSkuId, AccessScopeDto.From(item.AuthoringOwner), From(item.Definition),
        [.. item.Publications.Select(publication => new CatalogPublicationDto(publication.AssortmentId,
            AccessScopeDto.From(publication.Owner), Number(publication.Version), publication.Status))]);
    internal static CatalogSkuPageDto From(CatalogPage<CatalogSkuView> page) => new(Number(page.Total), [.. page.Items.Select(From)]);
    internal static CatalogAssortmentPageDto From(CatalogPage<CatalogAssortmentOption> page) => new(Number(page.Total),
        [.. page.Items.Select(item => new CatalogAssortmentDto(item.Id, AccessScopeDto.From(item.Owner), Number(item.Version), item.Status, item.Label))]);
    internal static CatalogMutationResultDto From(CatalogMutationResult item) => new(item.Kind, item.Id, Number(item.Version),
        item.FamilyId, item.CurrentDefinitionId, item.DefinitionVersion is { } definitionVersion ? Number(definitionVersion) : null,
        item.PredecessorSkuId, item.RecordedAt);
    internal static CatalogReceiptDto Receipt(CatalogMutationResult? item) => item is null
        ? new("not_confirmed", null, null) : new("committed", From(item), item.RecordedAt);
    internal static CatalogPublishedVariantDto From(CatalogPublishedVariant item) => new(item.Family.Id,
        item.Family.PublicId.ToString("D"), item.Family.Name, item.Family.Description, item.Family.CategoryCode,
        item.Sku.Id, item.Sku.PublicId.ToString("D"), item.Sku.Code, From(item.Definition));
    internal static CatalogPublishedVariantPageDto From(CatalogPage<CatalogPublishedVariant> page) => new(Number(page.Total), [.. page.Items.Select(From)]);
}
