using MongoDB.Bson;
using A = UnitToko.Platform.Procurement.PurchaseArrivalRecords;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PurchaseArrivalSchemas
{
    internal static readonly string[] Collections = [A.Heads, A.Events, A.Audits];
    private static BsonDocument Nullable(BsonDocument value) { value["bsonType"] = new BsonArray { value["bsonType"], "null" }; return value; }
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Hash => new() { { "bsonType", "string" }, { "pattern", "^[0-9a-f]{64}$" } };
    private static BsonDocument Location => S.Object(new() { { "organizationId", S.Number(1) }, { "shopId", S.Number(1) }, { "warehouseId", S.Number(1) }, { "channelId", S.Number(1) } });
    private static BsonDocument Input => S.Object(new() { { "orderId", S.Number(1) }, { "orderVersion", S.Number(1) },
        { "approvalId", S.Number(1) }, { "skuId", S.Number(1) }, { "definitionId", S.Number(1) }, { "expectedRevision", S.Number(1) },
        { "expectedAt", Nullable(Date) }, { "confirmationReference", S.Text(256) } });
    private static BsonDocument Person => S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) },
        { "verification", S.Choice("Verified") }, { "version", S.Number(1) } });
    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument { { "_id", name == A.Heads ? S.Text(64) : S.Number(1) },
            { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } }, { "location", Location } };
        if (name == A.Heads) {
            fields.Add("orderId", S.Number(1)); fields.Add("skuId", S.Number(1)); fields.Add("revision", S.Number(1));
            fields.Add("eventId", Nullable(S.Number(1))); fields.Add("fence", S.Number(0, long.MaxValue - 1));
        } else {
            fields.Add("actorId", S.Number(1)); fields.Add("recordedAt", Date); fields.Add("requestKey", S.Text(128));
            fields.Add("bodyHash", Hash); fields.Add("eventHash", Hash);
            if (name == A.Events) {
                fields.Add("headId", S.Text(64)); fields.Add("previousRevision", S.Number(1)); fields.Add("revision", S.Number(2));
                fields.Add("action", S.Choice("propose", "approve", "reject")); fields.Add("proposalId", S.Number(1));
                fields.Add("input", Input); fields.Add("orderHash", Hash); fields.Add("binding", Hash);
                fields.Add("actorGrantVersion", S.Number(1)); fields.Add("actorPerson", Person);
                fields.Add("reason", S.Text(1000)); fields.Add("correlationId", S.Text(128));
            } else if (name == A.Audits) { fields.Add("headId", S.Text(64)); fields.Add("operation", S.Choice(A.Prefix + "propose", A.Prefix + "approve", A.Prefix + "reject")); }
            else throw new ArgumentOutOfRangeException(nameof(name));
        }
        return new("$jsonSchema", S.Object(fields));
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        A.Heads => [("ux_purchase_arrival_line", new() { { "orderId", 1 }, { "skuId", 1 } }, true)],
        A.Events => [("ux_purchase_arrival_revision", new() { { "headId", 1 }, { "revision", 1 } }, true),
            ("ux_purchase_arrival_request", new() { { "actorId", 1 }, { "action", 1 }, { "requestKey", 1 } }, true)],
        A.Audits => [], _ => throw new ArgumentOutOfRangeException(nameof(name)) };
}
