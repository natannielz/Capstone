using MongoDB.Bson;
using UnitToko.Platform.Fulfillment;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

// Version-23 storage contract. Future format changes require a new forward migration.
internal static class ShipmentPreparationSchemas
{
    internal static readonly string[] Collections = [ShipmentPreparationBson.Heads, ShipmentPreparationBson.Versions];
    internal static BsonDocument Key(string name) => name == ShipmentPreparationBson.Heads ? new("suborderId", 1) :
        name == ShipmentPreparationBson.Versions ? new() { { "preparationId", 1 }, { "revision", 1 } } : throw new ArgumentException("Unknown preparation collection", nameof(name));
    private static BsonDocument Nullable(BsonDocument value, string kind) { value["bsonType"] = new BsonArray { kind, "null" }; return value; }
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Quantity(int min = 1) => new() { { "bsonType", "int" }, { "minimum", min }, { "maximum", 1000000 } };
    private static BsonDocument Array(BsonDocument item) => new() { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", item } };
    private static BsonDocument Scope() => S.Object(new() {
        { "buyingOrganizationId", S.Number(1) }, { "branchId", S.Number(1) }, { "divisionId", Nullable(S.Number(1), "long") },
        { "fulfillmentOrganizationId", S.Number(1) }, { "shopId", S.Number(1) }, { "warehouseId", Nullable(S.Number(1), "long") },
        { "channelId", S.Number(1) }, { "createdBy", S.Number(1) } });
    private static BsonDocument Source() {
        var delivery = OrderDraftSchemas.Input()["properties"]["delivery"].DeepClone().AsBsonDocument; delivery["bsonType"] = "object";
        return S.Object(new() {
            { "orderId", S.Number(1) }, { "suborderId", S.Number(1) }, { "revision", S.Number(1) }, { "review", Hash() }, { "buyerName", S.Text(200) },
            { "delivery", delivery }, { "orderedAt", new BsonDocument("bsonType", "date") },
            { "canPrepare", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } },
            { "lines", Array(S.Object(new() { { "skuId", S.Number(1) }, { "publicId", new BsonDocument { { "bsonType", "string" },
                { "pattern", @"\A[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\z" } } },
                { "skuCode", S.Text(64) }, { "productName", S.Text(160) },
                { "definition", CatalogSchemas.Validator("variantDefinitions")["$and"][0]["$jsonSchema"].DeepClone() },
                { "orderedPackages", Quantity() }, { "remainingPackages", Quantity(0) } })) }
        });
    }
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(name == ShipmentPreparationBson.Heads ? "fulfillment.preparation" : "fulfillment.preparation.version") },
            { "revision", S.Number(1) }, { "scope", Scope() } };
        var checks = new BsonArray();
        if (name == ShipmentPreparationBson.Heads) {
            fields.Add("suborderId", S.Number(1)); fields.Add("orderId", S.Number(1)); fields.Add("versionId", S.Number(1));
            fields.Add("fence", S.Number(0, long.MaxValue - 1)); fields.Add("createdAt", new BsonDocument("bsonType", "date")); fields.Add("updatedAt", new BsonDocument("bsonType", "date"));
            checks.Add(S.DateRange("createdAt")); checks.Add(S.DateRange("updatedAt"));
            checks.Add(new BsonDocument("$expr", new BsonDocument("$gte", new BsonArray { "$updatedAt", "$createdAt" })));
        } else if (name == ShipmentPreparationBson.Versions) {
            fields.Add("preparationId", S.Number(1)); fields.Add("preparedBy", S.Number(1)); fields.Add("source", Source());
            fields.Add("lines", Array(S.Object(new() { { "skuId", S.Number(1) }, { "packages", Quantity() } })));
            fields.Add("recordedAt", new BsonDocument("bsonType", "date")); fields.Add("actorGrantVersion", S.Number(1));
            fields.Add("requestKey", S.Text(128)); fields.Add("bodyHash", Hash()); S.Metadata(fields);
            checks.Add(S.DateRange("recordedAt")); checks.Add(S.DateRange("source.orderedAt"));
            checks.Add(new BsonDocument("$expr", new BsonDocument("$gte", new BsonArray { "$recordedAt", "$source.orderedAt" })));
        } else throw new ArgumentException("Unknown preparation collection", nameof(name));
        checks.Insert(0, new BsonDocument("$jsonSchema", S.Object(fields))); return new("$and", checks);
    }
}
