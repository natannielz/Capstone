using System.Globalization;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Catalog;

namespace UnitToko.Api.Catalog;

public sealed record CatalogBuyerCodeDto(LongId IdentifierId, string IdentifierRevision, string Code);
public sealed record CatalogBuyerIdentityDto(LongId SkuId, string SkuRevision, LongId DefinitionId,
    string DefinitionVersion, CatalogBuyerCodeDto CurrentPrimary);
public sealed record CatalogBuyerVariantDto(LongId FamilyId, string FamilyPublicId, string FamilyName,
    string Description, string CategoryCode, LongId SkuId, string PublicId, string Code,
    CatalogDefinitionDto Definition, CatalogPriceDto? CurrentPrice, CatalogBuyerIdentityDto BuyerIdentity);
public sealed record CatalogBuyerVariantPageDto(string Total, CatalogBuyerVariantDto[] Items);
public sealed record CatalogBuyerMatchedIdentifierDto(LongId IdentifierId, string IdentifierRevision, string Code,
    LongId SkuId, string SkuRevision, LongId DefinitionId, string DefinitionVersion, string Kind, string Namespace);
public sealed record CatalogBuyerCodeMatchDto(string RequestedCode, CatalogBuyerMatchedIdentifierDto MatchedIdentifier,
    CatalogBuyerVariantDto Variant);

internal static class CatalogBuyerIdentifierResponses
{
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static CatalogBuyerVariantDto From(CatalogBuyerVariant item)
    {
        var original = CatalogResponses.From(item.Variant); var identity = item.BuyerIdentity;
        return new(original.FamilyId, original.FamilyPublicId, original.FamilyName, original.Description,
            original.CategoryCode, original.SkuId, original.PublicId, original.Code, original.Definition, original.CurrentPrice,
            new(identity.SkuId, Number(identity.SkuRevision), identity.DefinitionId, Number(identity.DefinitionVersion),
                new(identity.CurrentPrimary.IdentifierId, Number(identity.CurrentPrimary.IdentifierRevision), identity.CurrentPrimary.Code)));
    }
    internal static CatalogBuyerVariantPageDto From(CatalogPage<CatalogBuyerVariant> page) => new(Number(page.Total), [.. page.Items.Select(From)]);
    internal static CatalogBuyerCodeMatchDto From(CatalogBuyerCodeMatch item)
    {
        var binding = item.MatchedIdentifier;
        return new(item.RequestedCode, new(binding.IdentifierId, Number(binding.IdentifierRevision), binding.Code,
            binding.SkuId, Number(binding.SkuRevision), binding.DefinitionId, Number(binding.DefinitionVersion), "skuCode", "internal"), From(item.Variant));
    }
}
