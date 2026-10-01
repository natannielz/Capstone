using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class ReturnResolutionSchemas
{
    internal static BsonDocument Key() => new() { { "actorId", 1 }, { "requestKey", 1 } };
    internal static BsonDocument Validator() {
        var hash = PaymentSubmissionSchemas.Hash(); var date = new BsonDocument("bsonType", "date");
        var line = S.Object(new() { { "receiptId", S.Number(1) }, { "skuId", S.Number(1) }, { "inspectedBaseUnits", S.Number(1, DomainValues.MaximumLineQuantity) },
            { "identity", S.Choice("matched", "different", "unverified") }, { "condition", S.Choice("sealed", "opened", "damaged", "mixed") } });
        var source = S.Object(new() { { "returnId", S.Number(1) }, { "inspectionHash", hash }, { "caseId", S.Number(1) }, { "invoiceId", S.Number(1) }, { "requestedBy", S.Number(1) },
            { "inspectedBy", S.Number(1) }, { "inspectedAt", date }, { "lines", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", line } } } });
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } }, { "revision", S.Number(1, 1) },
            { "source", source }, { "binding", hash }, { "input", S.Object(new() { { "action", S.Choice("replacement", "credit", "refund") }, { "publicNote", S.Text(1000) } }) }, { "state", S.Choice("resolution_requested") },
            { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "requestedAt", date }, { "requestKey", S.Text(128) }, { "bodyHash", hash }, { "snapshotHash", hash },
            { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 2 }, { "maxItems", 2 }, { "items", ReceivingAccountSchemas.Person() } } } }; S.Metadata(fields);
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("requestedAt"), S.DateRange("source.inspectedAt"),
            new BsonDocument("$expr", new BsonDocument("$and", new BsonArray { new BsonDocument("$eq", new BsonArray { "$_id", "$source.returnId" }), new BsonDocument("$gte", new BsonArray { "$requestedAt", "$source.inspectedAt" }) })) });
    }
}
