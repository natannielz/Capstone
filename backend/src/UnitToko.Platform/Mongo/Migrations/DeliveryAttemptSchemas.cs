using MongoDB.Bson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

// Frozen deployment version 28. The historical recipient/QR schema is unchanged.
internal static class DeliveryAttemptSchemas
{
    internal static readonly string[] Collections = ["deliveryAttempts", "receiptChallengeRevocations"];
    internal static BsonDocument Key(string name) => name == Collections[0] ? new() { { "shipmentId", 1 }, { "sequence", 1 } } : new("_id", 1);
    internal static BsonDocument RevocationLookupKey() => new("shipmentId", 1);
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Date() => new("bsonType", "date");
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "shipmentId", S.Number(1) }, { "sequence", S.Number(1, 1000) }, { "recordedAt", Date() } };
        var expressions = new BsonArray();
        if (name == Collections[0]) {
            fields.Add("shipmentRevision", S.Number(1, 1)); fields.Add("dispatchHash", Hash());
            fields.Add("previousAttemptId", new BsonDocument("anyOf", new BsonArray { S.Number(1), new BsonDocument("bsonType", "null") }));
            fields.Add("previousHash", new BsonDocument("anyOf", new BsonArray { Hash(), new BsonDocument("bsonType", "null") }));
            fields.Add("courierId", S.Number(1)); fields.Add("actorId", S.Number(1)); fields.Add("actorGrantVersion", S.Number(1));
            fields.Add("occurredAt", Date()); fields.Add("outcome", S.Choice("recipient_unavailable", "address_unreachable", "delivery_refused", "transport_issue", "other"));
            fields.Add("revokedChallengeIds", new BsonDocument { { "bsonType", "array" }, { "maxItems", 20 }, { "uniqueItems", true }, { "items", S.Number(1) } });
            fields.Add("requestKey", S.Text(128)); fields.Add("bodyHash", Hash()); S.Metadata(fields);
            expressions.Add(new BsonDocument("$eq", new BsonArray { "$actorId", "$courierId" }));
            expressions.Add(new BsonDocument("$lte", new BsonArray { "$occurredAt", "$recordedAt" }));
            foreach (var field in new[] { "previousAttemptId", "previousHash" }) expressions.Add(new BsonDocument("$eq", new BsonArray {
                new BsonDocument("$eq", new BsonArray { "$sequence", 1L }), new BsonDocument("$eq", new BsonArray { "$" + field, BsonNull.Value }) }));
        } else if (name == Collections[1]) fields.Add("attemptId", S.Number(1));
        else throw new ArgumentException("Unknown delivery attempt collection");
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("recordedAt") };
        if (name == Collections[0]) terms.Add(S.DateRange("occurredAt"));
        if (expressions.Count > 0) terms.Add(new BsonDocument("$expr", new BsonDocument("$and", expressions)));
        return new("$and", terms);
    }
}
