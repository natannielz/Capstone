using MongoDB.Bson;
using UnitToko.Platform.Ordering;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class OrderViewSchemas
{
    internal static readonly string[] Collections = [OrderViewBson.Heads, OrderViewBson.Versions];
    internal static BsonDocument Key(string name) => name == OrderViewBson.Heads ? new() { { "createdBy", 1 }, { "_id", 1 } } : new() { { "viewId", 1 }, { "revision", 1 } };
    internal static BsonDocument RequestKey() => new() { { "createdBy", 1 }, { "requestKey", 1 } };
    private static BsonDocument Nullable(BsonDocument value, string kind) { value["bsonType"] = new BsonArray { kind, "null" }; return value; }
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "createdBy", S.Number(1) }, { "revision", S.Number(1) }, { "name", S.Text(80) },
            { "filter", S.Object(new() { { "context", Nullable(OrderDraftSchemas.Context(), "object") }, { "sort", S.Choice("newest", "oldest") } }) } };
        if (name == OrderViewBson.Heads) {
            fields.Add("versionId", S.Number(1)); fields.Add("removed", new BsonDocument("bsonType", "bool")); fields.Add("fence", S.Number(0, long.MaxValue - 1));
            fields.Add("createdAt", new BsonDocument("bsonType", "date")); fields.Add("updatedAt", new BsonDocument("bsonType", "date"));
        } else if (name == OrderViewBson.Versions) {
            fields.Add("viewId", S.Number(1)); fields.Add("action", S.Choice("create", "rename", "remove"));
            fields.Add("recordedAt", new BsonDocument("bsonType", "date")); fields.Add("actorGrantVersion", S.Number(1)); fields.Add("requestKey", S.Text(128));
            fields.Add("bodyHash", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } }); S.Metadata(fields);
        } else throw OrderViewBson.Invalid();
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)),
            S.DateRange(name == OrderViewBson.Heads ? "createdAt" : "recordedAt"),
            name == OrderViewBson.Heads ? S.DateRange("updatedAt") : new BsonDocument("revision", new BsonDocument("$gte", 1L)) });
    }
}
