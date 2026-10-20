using System.Globalization;
using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;

namespace UnitToko.Api.Catalog;

// Project only reviewed display facts. Stored audit, actor/person, receipt and request-key data stay private.
internal static class CatalogPriceResponses
{
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(BsonValue value) => Number(value.AsInt64);
    private static DateTimeOffset Time(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    internal static CatalogPriceDto? Price(PriceVersion? value) => value is null ? null :
        new(value.Id, value.SkuId, Number(value.Version), value.Currency, value.UnitPrice, value.EffectiveAt);
    internal static CatalogPriceChoiceDto Choice(CatalogPriceChoice value) => new(
        new(value.Sku.Id, value.Sku.FamilyId, value.Sku.Code, value.Sku.PublicId.ToString("D"), Number(value.Sku.Revision), value.Sku.Active),
        value.FamilyName, CatalogResponses.From(value.Definition));
    internal static CatalogPriceDto Price(BsonDocument value) => new(new(value["_id"].AsInt64), new(value["skuId"].AsInt64),
        Number(value["version"]), value["currency"].AsString, new Money(Decimal128.ToDecimal(value["unitPrice"].AsDecimal128)), Time(value["effectiveAt"]));
    private static CatalogPriceExpectationDto Expected(BsonDocument row) => new(Number(row["skuRevision"]),
        new(row["definitionId"].AsInt64), Number(row["definitionVersion"]), Number(row["priceVersion"]));
    internal static CatalogPricePreviewDto Preview(CatalogPricePreview value) => new(
        new(value.Sku.Id, value.Sku.FamilyId, value.Sku.Code, value.Sku.PublicId.ToString("D"), Number(value.Sku.Revision), value.Sku.Active),
        CatalogResponses.From(value.Definition), new(Number(value.Expected.SkuRevision), value.Expected.DefinitionId,
            Number(value.Expected.DefinitionVersion), Number(value.Expected.PriceVersion)), Price(value.Current));
    internal static CatalogPriceCommandDto Command(BsonDocument row) => new(new(row["proposalId"].AsInt64), new(row["skuId"].AsInt64), "1",
        row.TryGetValue("status", out var status) ? status.AsString : "proposed",
        row.TryGetValue("decisionId", out var decision) && !decision.IsBsonNull ? new LongId(decision.AsInt64) : null,
        row.TryGetValue("priceId", out var price) && !price.IsBsonNull ? new LongId(price.AsInt64) : null,
        row.TryGetValue("priceVersion", out var version) && !version.IsBsonNull ? Number(version) : null);
    internal static CatalogPriceProposalDto Proposal(BsonDocument view)
    {
        var row = view["proposal"].AsBsonDocument; var sku = row["skuSnapshot"].AsBsonDocument;
        var definition = row["definition"].AsBsonDocument;
        var decision = view["decision"].IsBsonNull ? null : view["decision"].AsBsonDocument;
        return new(new(row["_id"].AsInt64), new(row["skuId"].AsInt64), Number(row["revision"]), decision?["decision"].AsString ?? "proposed",
            new(new(sku["_id"].AsInt64), new(sku["familyId"].AsInt64), sku["sku"].AsString, sku["publicId"].AsString, Number(sku["revision"]), sku["active"].AsBoolean),
            new(new(definition["_id"].AsInt64), new(definition["skuId"].AsInt64), Number(definition["version"]),
                definition["packageLabel"].AsString, definition["packageKind"].AsString, definition["baseUnitCode"].AsString,
                definition["baseUnitsPerPackage"].AsInt32,
                [.. definition["attributes"].AsBsonArray.Select(attribute => new CatalogAttributeDto(attribute["key"].AsString, attribute["value"].AsString))],
                definition["minimumOrderPackages"].AsInt32, definition["orderIncrementPackages"].AsInt32, Time(definition["effectiveAt"])),
            Expected(row["expected"].AsBsonDocument), row["basePrice"].IsBsonNull ? null : Price(row["basePrice"].AsBsonDocument),
            new(row["input"]["currency"].AsString, new Money(Decimal128.ToDecimal(row["input"]["unitPrice"].AsDecimal128))),
            Time(row["proposedAt"]), row["reason"].AsString,
            decision is null ? null : new(new(decision["_id"].AsInt64), decision["decision"].AsString, Time(decision["decidedAt"]),
                decision["reviewReference"].AsString, decision["reason"].AsString),
            view["price"].IsBsonNull ? null : Price(view["price"].AsBsonDocument), new(row["proposedBy"].AsInt64));
    }
}
