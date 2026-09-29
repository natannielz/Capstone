using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Catalog;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Closed price shapes installed by the forward migration after complete preflight.</summary>
internal static class CatalogPriceSchemas
{
    internal static readonly string[] Collections = [CatalogPriceBson.Proposals, CatalogPriceBson.Decisions, CatalogPriceBson.Versions, CatalogPriceBson.Heads];
    internal static BsonDocument Key(string collection) => collection switch {
        CatalogPriceBson.Decisions => new("proposalId", 1),
        CatalogPriceBson.Versions => new() { { "skuId", 1 }, { "version", 1 } },
        CatalogPriceBson.Proposals or CatalogPriceBson.Heads => new("_id", 1), _ => throw CatalogPriceBson.Invalid() };
    private static BsonDocument NullableId() => new() { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } };
    private static BsonDocument Currency() => new() { { "bsonType", "string" }, { "pattern", @"\A[A-Z]{3}\z" } };
    private static BsonDocument Money() => new() { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(0m) }, { "maximum", new BsonDecimal128(DomainValues.MaximumMoney) }, { "multipleOf", new BsonDecimal128(0.01m) } };
    private static BsonDocument PriceFields() => new() {
        { "_id", S.Number(1) }, { "skuId", S.Number(1) }, { "version", S.Number(1) }, { "currency", Currency() },
        { "unitPrice", Money() }, { "effectiveAt", new BsonDocument("bsonType", "date") } };
    internal static BsonDocument Validator(string collection)
    {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(CatalogPriceBson.Kind(collection)) } };
        var terms = new BsonArray();
        if (collection == CatalogPriceBson.Heads) {
            fields.Add("priceId", S.Number(1)); fields.Add("version", S.Number(1)); fields.Add("fence", S.Number(0));
        }
        else if (collection == CatalogPriceBson.Versions) {
            foreach (var field in PriceFields().Where(field => field.Name != "_id")) fields.Add(field);
            fields.Add("proposalId", S.Number(1)); fields.Add("decisionId", S.Number(1)); terms.Add(S.DateRange("effectiveAt"));
        }
        else {
            fields.Add("skuId", S.Number(1));
            fields.Add("revision", S.Number(1, 1));
            if (collection == CatalogPriceBson.Proposals) {
                fields.Add("skuSnapshot", CatalogSchemas.Validator("skus")["$and"][0]["$jsonSchema"].DeepClone());
                fields.Add("definition", CatalogSchemas.Validator("variantDefinitions")["$and"][0]["$jsonSchema"].DeepClone());
                fields.Add("expected", S.Object(new() { { "skuRevision", S.Number(1) }, { "definitionId", S.Number(1) },
                    { "definitionVersion", S.Number(1) }, { "priceVersion", S.Number(0, long.MaxValue - 1) } }));
                var previous = S.Object(PriceFields()); previous["bsonType"] = new BsonArray { "object", "null" };
                fields.Add("basePrice", previous);
                fields.Add("input", S.Object(new() { { "currency", Currency() }, { "unitPrice", Money() } }));
                fields.Add("proposedBy", S.Number(1)); fields.Add("proposedAt", new BsonDocument("bsonType", "date"));
                terms.Add(S.DateRange("proposedAt")); terms.Add(S.DateRange("definition.effectiveAt"));
            }
            else if (collection == CatalogPriceBson.Decisions) {
                fields.Add("proposalId", S.Number(1)); fields.Add("decision", S.Choice("approve", "reject", "withdraw"));
                fields.Add("priceId", NullableId()); fields.Add("priceVersion", NullableId());
                fields.Add("decidedBy", S.Number(1)); fields.Add("decidedAt", new BsonDocument("bsonType", "date"));
                fields.Add("reviewReference", S.Text(160)); terms.Add(S.DateRange("decidedAt"));
                terms.Add(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$eq", new BsonArray { "$decision", "approve" }), new BsonDocument("$ne", new BsonArray { "$priceId", BsonNull.Value }) }),
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$eq", new BsonArray { "$decision", "approve" }), new BsonDocument("$ne", new BsonArray { "$priceVersion", BsonNull.Value }) }) })));
            } else throw CatalogPriceBson.Invalid();
            S.Metadata(fields); fields.Add("actorGrantVersion", S.Number(1)); fields.Add("requestKey", S.Text(200));
            fields.Add("bodyHash", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } });
        }
        terms.Insert(0, new BsonDocument("$jsonSchema", S.Object(fields)));
        return new("$and", terms);
    }
}
