using MongoDB.Bson;
using UnitToko.Platform.Fulfillment;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class ReturnedLotVerificationSchemas
{
    internal static BsonDocument Key() => new() { { "actorId", 1 }, { "requestKey", 1 } };
    internal static BsonDocument Validator()
    {
        var hash = PaymentSubmissionSchemas.Hash(); var date = new BsonDocument("bsonType", "date");
        var line = S.Object(new() { { "receiptId", S.Number(1) }, { "skuId", S.Number(1) }, { "originalLotId", S.Number(1) }, { "verifiedBaseUnits", S.Number(1, 1000000) } });
        var source = S.Object(new() { { "returnId", S.Number(1) }, { "returnHash", hash }, { "inspectionHash", hash },
            { "caseId", S.Number(1) }, { "invoiceId", S.Number(1) }, { "requestedBy", S.Number(1) }, { "inspectedAt", date } });
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "revision", S.Number(1, 1) }, { "source", source }, { "binding", hash }, { "input", S.Object(new() {
                { "verificationReference", S.Text(160) }, { "confirmPhysicalOriginalLotMatch", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } },
                { "lines", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", line } } } }) },
            { "state", S.Choice("verified_original_lot_quarantined") }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "verifiedAt", date },
            { "requestKey", S.Text(128) }, { "bodyHash", hash }, { "snapshotHash", hash },
            { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 2 }, { "maxItems", 2 }, { "items", ReceivingAccountSchemas.Person() } } } };
        S.Metadata(fields);
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("verifiedAt"), S.DateRange("source.inspectedAt"),
            new BsonDocument("$expr", new BsonDocument("$and", new BsonArray { new BsonDocument("$eq", new BsonArray { "$_id", "$source.returnId" }),
                new BsonDocument("$gte", new BsonArray { "$verifiedAt", "$source.inspectedAt" }) })) });
    }
}
