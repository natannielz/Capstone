using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class ReturnQuarantineSchemas
{
    internal static BsonDocument Key() => new() { { "caseId", 1 }, { "revision", 1 } };
    internal static BsonDocument RequestKey() => new() { { "actorId", 1 }, { "requestKey", 1 } };
    private static BsonDocument Array(BsonDocument item) => new() { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", item } };
    internal static BsonDocument Validator() {
        var hash = PaymentSubmissionSchemas.Hash(); var date = new BsonDocument("bsonType", "date");
        var authorized = S.Object(new() { { "receiptId", S.Number(1) }, { "skuId", S.Number(1) }, { "reportedBaseUnits", S.Number(1, DomainValues.MaximumLineQuantity) } });
        var stock = S.Object(new() { { "receiptId", S.Number(1) }, { "skuId", S.Number(1) }, { "definitionId", S.Number(1) }, { "packageLabel", S.Text(160) },
            { "baseUnitsPerPackage", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", DomainValues.MaximumLineQuantity } } }, { "baseUnitCode", S.Text(160) },
            { "location", S.Location() }, { "dispatchId", S.Number(1) }, { "dispatchReview", hash },
            { "originalLots", Array(S.Object(new() { { "lotId", S.Number(1) }, { "poolId", S.Number(1) }, { "dispatchedPackages", S.Number(1) } })) } });
        var source = S.Object(new() { { "caseId", S.Number(1) }, { "caseHash", hash }, { "decisionHash", hash }, { "requestedBy", S.Number(1) }, { "invoiceId", S.Number(1) },
            { "decidedAt", date }, { "authorizedLines", Array(authorized) }, { "stock", Array(stock) } });
        var line = S.Object(new() { { "receiptId", S.Number(1) }, { "skuId", S.Number(1) }, { "receivedBaseUnits", S.Number(1, DomainValues.MaximumLineQuantity) },
            { "observedCondition", S.Choice("uninspected", "sealed", "opened", "damaged") } });
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "caseId", S.Number(1) }, { "revision", S.Number(1, long.MaxValue - 1) }, { "source", source }, { "binding", hash },
            { "previousHash", new BsonDocument { { "bsonType", new BsonArray { "string", "null" } }, { "pattern", "^[0-9a-f]{64}$" } } },
            { "input", S.Object(new() { { "publicNote", S.Text(1000) }, { "lines", Array(line) } }) }, { "state", S.Choice("quarantined") }, { "lotIdentity", S.Choice("unverified") },
            { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "receivedAt", date }, { "requestKey", S.Text(128) }, { "bodyHash", hash }, { "snapshotHash", hash },
            { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 2 }, { "maxItems", 2 }, { "items", ReceivingAccountSchemas.Person() } } } }; S.Metadata(fields);
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("receivedAt"), S.DateRange("source.decidedAt"),
            new BsonDocument("$expr", new BsonDocument("$and", new BsonArray { new BsonDocument("$eq", new BsonArray { "$caseId", "$source.caseId" }),
                new BsonDocument("$gte", new BsonArray { "$receivedAt", "$source.decidedAt" }) })) });
    }
}
