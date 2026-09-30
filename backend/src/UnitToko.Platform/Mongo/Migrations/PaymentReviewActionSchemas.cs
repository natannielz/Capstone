using MongoDB.Bson;
using UnitToko.Platform.Billing;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PaymentReviewActionSchemas
{
    internal static BsonDocument Key() => new() { { "declarationId", 1 }, { "revision", 1 } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    internal static BsonDocument Validator() {
        var hash = PaymentSubmissionSchemas.Hash(); var fields = new BsonDocument {
            { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "declarationId", S.Number(1) }, { "revision", S.Number(1, long.MaxValue - 1) }, { "previousVersionId", Nullable(S.Number(1)) }, { "previousHash", Nullable(hash) },
            { "source", PaymentVerificationSchemas.Source() }, { "binding", hash }, { "action", S.Choice("claim", "release", "reject", "request_replacement") },
            { "state", S.Choice("pending", "assigned", "rejected", "replacement_requested") }, { "assigneeId", Nullable(S.Number(1)) },
            { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 2 }, { "maxItems", 2 }, { "items", ReceivingAccountSchemas.Person() } } },
            { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "operation", S.Choice(PaymentReviewActionRecords.Operation) }, { "requestKey", S.Text(128) },
            { "bodyHash", hash }, { "snapshotHash", hash }, { "recordedAt", new BsonDocument("bsonType", "date") }, { "fence", S.Number(0, long.MaxValue - 1) } };
        S.Metadata(fields); return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("recordedAt") });
    }
}
