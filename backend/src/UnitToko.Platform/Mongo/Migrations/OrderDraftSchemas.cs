using MongoDB.Bson;
using UnitToko.Platform.Ordering;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class OrderDraftSchemas
{
    internal static readonly string[] Collections = [OrderDraftBson.Heads, OrderDraftBson.Versions];
    internal static BsonDocument Key(string name) => name == OrderDraftBson.Heads ? new("_id", 1) : new() { { "draftId", 1 }, { "revision", 1 } };
    private static BsonDocument Nullable(BsonDocument schema, string type) { schema["bsonType"] = new BsonArray { type, "null" }; return schema; }
    internal static BsonDocument Context() => S.Object(new() { { "organizationId", S.Number(1) }, { "branchId", S.Number(1) }, { "divisionId", Nullable(S.Number(1), "long") } });
    private static BsonDocument Multiline(int limit) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", limit },
        { "pattern", @"\A[^\p{Z}\s\p{Cc}](?:(?:[^\p{Cc}]|\n)*[^\p{Z}\s\p{Cc}])?\z" } };
    internal static BsonDocument Input() => S.Object(new() { { "context", Context() },
        { "selections", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", S.Object(new() {
            { "skuId", S.Number(1) }, { "definitionId", S.Number(1) }, { "definitionVersion", S.Number(1) }, { "priceId", S.Number(1) }, { "priceVersion", S.Number(1) },
            { "packages", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } } } }) } } },
        { "delivery", Nullable(S.Object(new() {
            { "recipientName", S.Text(160) }, { "phone", S.Text(32) }, { "address", Multiline(1000) }, { "city", S.Text(120) },
            { "postalCode", Nullable(new() { { "bsonType", "string" }, { "pattern", @"\A[0-9]{5}\z" } }, "string") },
            { "instructions", Nullable(Multiline(1000), "string") } }), "object") },
        { "purchaseReference", Nullable(S.Text(160), "string") } });
    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(name == OrderDraftBson.Heads ? "ordering.draft" : "ordering.draft.version") }, { "revision", S.Number(1) }, { "createdBy", S.Number(1) } };
        if (name == OrderDraftBson.Heads) {
            fields.Add("versionId", S.Number(1)); fields.Add("context", Context()); fields.Add("fence", S.Number(0, long.MaxValue - 1));
            fields.Add("createdAt", new BsonDocument("bsonType", "date")); fields.Add("updatedAt", new BsonDocument("bsonType", "date"));
        } else if (name == OrderDraftBson.Versions) {
            fields.Add("draftId", S.Number(1)); fields.Add("input", Input()); fields.Add("recordedAt", new BsonDocument("bsonType", "date"));
            fields.Add("actorGrantVersion", S.Number(1)); fields.Add("requestKey", S.Text(128));
            fields.Add("bodyHash", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } }); S.Metadata(fields);
        } else throw OrderDraftBson.Invalid();
        var checks = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) };
        if (name == OrderDraftBson.Heads) {
            checks.Add(S.DateRange("createdAt")); checks.Add(S.DateRange("updatedAt"));
            checks.Add(new BsonDocument("$expr", new BsonDocument("$gte", new BsonArray { "$updatedAt", "$createdAt" })));
        } else checks.Add(S.DateRange("recordedAt"));
        return new("$and", checks);
    }
}
