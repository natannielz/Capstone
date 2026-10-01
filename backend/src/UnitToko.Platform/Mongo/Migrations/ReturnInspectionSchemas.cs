using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class ReturnInspectionSchemas
{
    internal static BsonDocument Key() => new() { { "actorId", 1 }, { "requestKey", 1 } };
    private static BsonDocument Array(BsonDocument item) => new() { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", item } };
    internal static BsonDocument Validator() {
        var hash = PaymentSubmissionSchemas.Hash(); var date = new BsonDocument("bsonType", "date");
        var received = S.Object(new() { { "receiptId", S.Number(1) }, { "skuId", S.Number(1) }, { "receivedBaseUnits", S.Number(1, DomainValues.MaximumLineQuantity) }, { "observedCondition", S.Choice("uninspected", "sealed", "opened", "damaged") } });
        var source = S.Object(new() { { "returnId", S.Number(1) }, { "returnHash", hash }, { "caseId", S.Number(1) }, { "invoiceId", S.Number(1) }, { "requestedBy", S.Number(1) },
            { "receivedBy", S.Number(1) }, { "receivedAt", date }, { "lines", Array(received) } });
        var lot = S.Text(160); lot["bsonType"] = new BsonArray { "string", "null" };
        var line = S.Object(new() { { "receiptId", S.Number(1) }, { "skuId", S.Number(1) }, { "inspectedBaseUnits", S.Number(1, DomainValues.MaximumLineQuantity) },
            { "identity", S.Choice("matched", "different", "unverified") }, { "condition", S.Choice("sealed", "opened", "damaged", "mixed") }, { "observedIdentity", S.Text(160) }, { "observedLotLabel", lot } });
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "revision", S.Number(1, 1) }, { "source", source }, { "binding", hash }, { "input", S.Object(new() { { "publicNote", S.Text(1000) }, { "lines", Array(line) } }) },
            { "state", S.Choice("inspected_quarantine") }, { "lotIdentity", S.Choice("unverified") }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
            { "inspectedAt", date }, { "requestKey", S.Text(128) }, { "bodyHash", hash }, { "snapshotHash", hash },
            { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 2 }, { "maxItems", 2 }, { "items", ReceivingAccountSchemas.Person() } } } }; S.Metadata(fields);
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("inspectedAt"), S.DateRange("source.receivedAt"),
            new BsonDocument("$expr", new BsonDocument("$and", new BsonArray { new BsonDocument("$eq", new BsonArray { "$_id", "$source.returnId" }), new BsonDocument("$gte", new BsonArray { "$inspectedAt", "$source.receivedAt" }) })) });
    }
}
