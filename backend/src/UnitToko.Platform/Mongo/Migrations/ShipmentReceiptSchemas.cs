using MongoDB.Bson;
using UnitToko.Modules.Fulfillment;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ShipmentReceiptSchemas
{
    internal static readonly string[] Collections = ["shipmentReceiptChallenges", "shipmentReceipts"];
    internal static BsonDocument ChallengeTimeKey() => new() { { "shipmentId", 1 }, { "recordedAt", -1 } };
    internal static BsonDocument Key(string collection) => collection == Collections[0]
        ? new() { { "actorId", 1 }, { "requestKey", 1 } } : new("shipmentId", 1);
    internal static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument NullableLong() => new() { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } };
    internal static BsonDocument Validator(string collection) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "shipmentId", S.Number(1) }, { "documentId", S.Number(1) }, { "documentVersion", S.Number(1, 1) }, { "documentHash", Hash() },
            { "courierId", S.Number(1) }, { "recipientId", S.Number(1) }, { "recipientName", S.Text(256) }, { "recipientProfileVersion", S.Number(1) },
            { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "requestKey", S.Text(128) }, { "bodyHash", Hash() }, { "recordedAt", Date() } };
        S.Metadata(fields);
        if (collection == Collections[0]) {
            fields.Add("tokenHash", Hash()); fields.Add("expiresAt", Date());
            fields.Add("failedAttempts", new BsonDocument { { "bsonType", "int" }, { "minimum", 0 }, { "maximum", DeliveryReceiptPolicy.MaximumFailedAttempts } });
            fields.Add("consumedReceiptId", NullableLong()); fields.Add("consumedAt", new BsonDocument("bsonType", new BsonArray { "date", "null" }));
        } else if (collection == Collections[1]) {
            fields.Add("challengeId", S.Number(1)); fields.Add("tokenHash", Hash());
            fields.Add("courierPersonId", S.Number(1)); fields.Add("recipientPersonId", S.Number(1));
            fields.Add("consentVersion", S.Choice(DeliveryReceiptPolicy.ConsentVersion)); fields.Add("consentText", S.Choice(DeliveryReceiptPolicy.ConsentText));
            fields.Add("hasException", new BsonDocument("bsonType", "bool"));
            var quantity = S.Object(new() { { "packages", new BsonDocument { { "bsonType", "int" }, { "minimum", 0 }, { "maximum", 1000000 } } },
                { "looseUnits", new BsonDocument { { "bsonType", "int" }, { "minimum", 0 }, { "maximum", 999999 } } } });
            fields.Add("lines", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", S.Object(new() {
                { "skuId", S.Number(1) }, { "definitionId", S.Number(1) }, { "dispatchedBaseUnits", S.Number(1, 1000000000000) },
                { "accepted", quantity }, { "rejected", quantity.DeepClone() }, { "missing", quantity.DeepClone() },
                { "acceptedBaseUnits", S.Number(0, 1000000000000) }, { "rejectedBaseUnits", S.Number(0, 1000000000000) }, { "missingBaseUnits", S.Number(0, 1000000000000) }, { "exceptionReason", new BsonDocument("anyOf", new BsonArray { S.Text(1000), new BsonDocument("bsonType", "null") }) }
            }) } });
        } else throw new ArgumentException("Unknown receipt collection", nameof(collection));
        var expressions = new BsonArray { new BsonDocument("$ne", new BsonArray { "$courierId", "$recipientId" }),
            new BsonDocument("$eq", new BsonArray { "$actorId", collection == Collections[0] ? "$courierId" : "$recipientId" }) };
        if (collection == Collections[0]) {
            expressions.Add(new BsonDocument("$eq", new BsonArray { "$expiresAt", new BsonDocument("$add", new BsonArray { "$recordedAt", 900000L }) }));
            expressions.Add(new BsonDocument("$eq", new BsonArray { new BsonDocument("$eq", new BsonArray { "$consumedReceiptId", BsonNull.Value }), new BsonDocument("$eq", new BsonArray { "$consumedAt", BsonNull.Value }) }));
        } else expressions.Add(new BsonDocument("$ne", new BsonArray { "$courierPersonId", "$recipientPersonId" }));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("recordedAt"), new BsonDocument("$expr", new BsonDocument("$and", expressions)) });
    }
}
