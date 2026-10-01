using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Fulfillment;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class AfterSalesDecisionSchemas
{
    internal static BsonDocument Key() => new() { { "actorId", 1 }, { "requestKey", 1 } };
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    internal static BsonDocument Validator() {
        var line = S.Object(new() { { "receiptId", S.Number(1) }, { "skuId", S.Number(1) }, { "reportedBaseUnits", S.Number(1, DomainValues.MaximumLineQuantity) } });
        BsonDocument Lines(int minimum) => new() { { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", 100 }, { "items", line } };
        var date = new BsonDocument("bsonType", "date");
        var input = S.Object(new() { { "action", S.Choice("authorize_return", "reject") }, { "publicExplanation", S.Text(1000) }, { "lines", Lines(0) } });
        var source = S.Object(new() { { "caseId", S.Number(1) }, { "caseHash", Hash }, { "invoiceId", S.Number(1) }, { "invoiceBinding", Hash },
            { "requestedBy", S.Number(1) }, { "requestedAt", date }, { "lines", Lines(1) } });
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "revision", S.Number(2, 2) }, { "source", source }, { "binding", Hash }, { "input", input }, { "state", S.Choice("return_authorized", "rejected") },
            { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "decidedAt", date }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash },
            { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 2 }, { "maxItems", 2 }, { "items", ReceivingAccountSchemas.Person() } } } }; S.Metadata(fields);
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("decidedAt"), S.DateRange("source.requestedAt"),
            new BsonDocument("$expr", new BsonDocument("$and", new BsonArray { new BsonDocument("$eq", new BsonArray { "$_id", "$source.caseId" }),
                new BsonDocument("$gte", new BsonArray { "$decidedAt", "$source.requestedAt" }) })) });
    }
}
