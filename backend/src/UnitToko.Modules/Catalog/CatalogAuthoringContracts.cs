using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Catalog;

public sealed record CatalogCategory(LongId Id, LongId OrganizationId, GrantScope AuthoringOwner,
    string Code, string Name, bool Active, long Revision);
public sealed record ProductFamily(LongId Id, LongId OrganizationId, GrantScope AuthoringOwner, Guid PublicId,
    LongId CategoryId, string CategoryCode, string Name, string Description,
    ImmutableArray<LongId> ImageAssetIds, bool Active, long Revision);
public sealed record CatalogPage<T>(long Total, ImmutableArray<T> Items);
public sealed record CatalogOwnerOption(GrantScope Owner, string Label);
public sealed record CatalogAssortmentVersion(LongId Id, long Version);
public sealed record CatalogAssortmentOption(LongId Id, GrantScope Owner, long Version, string Status, string Label);
public sealed record CatalogPublication(LongId AssortmentId, GrantScope Owner, long Version, string Status);
public sealed record CatalogSkuView(Sku Sku, GrantScope AuthoringOwner, VariantDefinitionVersion Definition,
    ImmutableArray<CatalogPublication> Publications);
public sealed record CatalogPublishedVariant(ProductFamily Family, Sku Sku, VariantDefinitionVersion Definition, PriceVersion? CurrentPrice = null);
public sealed record CatalogMutationResult(string Kind, LongId Id, long Version, LongId? FamilyId,
    LongId? CurrentDefinitionId, long? DefinitionVersion, LongId? PredecessorSkuId, DateTimeOffset RecordedAt);

public sealed record CatalogSaveCategory(GrantScope Owner, LongId? CategoryId, long ExpectedVersion, string Code, string Name);
public sealed record CatalogCreateFamily(GrantScope Owner, LongId CategoryId, string Name, string Description,
    ImmutableArray<LongId> ImageAssetIds);
public sealed record CatalogEditFamily(LongId FamilyId, long ExpectedVersion, LongId CategoryId,
    string Name, string Description, ImmutableArray<LongId> ImageAssetIds);
public sealed record CatalogCreateSku(LongId FamilyId, long ExpectedFamilyVersion, string Code,
    string PhysicalItemKey, DefinitionInput Definition);
public sealed record CatalogReviseDefinition(LongId SkuId, long ExpectedVersion, DefinitionInput Definition);
public sealed record CatalogCreateReplacement(LongId PredecessorSkuId, long ExpectedVersion, string Code,
    string PhysicalItemKey, DefinitionInput Definition);
public sealed record CatalogRetireSku(LongId SkuId, long ExpectedVersion);
public sealed record CatalogSetPublications(LongId SkuId, long ExpectedVersion,
    ImmutableArray<LongId> AssortmentIds, ImmutableArray<CatalogAssortmentVersion> ExpectedAssortments);
