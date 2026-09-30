using MongoDB.Bson;
using B = UnitToko.Platform.Billing.PaymentManualReviewRecords;
using V = UnitToko.Platform.Billing.PaymentVerificationBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PaymentManualReviewSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Versions];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Nullable(BsonDocument rule) => new("anyOf", new BsonArray { rule, new BsonDocument("bsonType", "null") });
    internal static BsonDocument Key(string name) => name == B.Policies ? new("scope", 1) : new() { { "declarationId", 1 }, { "revision", 1 } };
    internal static BsonDocument Failure() => S.Object(new() {
        { "outboxId", new BsonDocument("bsonType", "objectId") }, { "jobState", S.Choice("retry", "dead") },
        { "attempt", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 } } }, { "failureId", S.Number(1) }, { "failureHash", Hash },
        { "readAuditId", S.Number(1) }, { "readHash", Hash }, { "bindingHash", Hash }, { "pipelineId", S.Text(256) },
        { "safeFailure", S.Choice("ocr_unavailable", "ocr_timeout", "ocr_invalid_output") }, { "failedAt", Date } });
    internal static BsonDocument PolicyFields() => new() {
        { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
        { "revision", S.Number(1, long.MaxValue - 1) }, { "scope", ReceivingAccountSchemas.Scope() },
        { "procedureReference", S.Text(256) }, { "operatingOwnerReference", S.Text(256) }, { "approvalReference", S.Text(256) },
        { "allowedFailures", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 3 }, { "uniqueItems", true },
            { "items", S.Choice("ocr_unavailable", "ocr_timeout", "ocr_invalid_output") } } },
        { "active", new BsonDocument("bsonType", "bool") }, { "approvedAt", Date }, { "expiresAt", Date } };
    internal static BsonDocument Review() => S.Object(new() { { "kind", S.Choice("manual") }, { "revision", S.Number(1, long.MaxValue - 1) },
        { "versionId", S.Number(1) }, { "versionHash", Hash }, { "failureId", S.Number(1) }, { "fields", PaymentCorrectionSchemas.Fields() } });
    internal static BsonDocument VerificationValidator() {
        var manual = PaymentVerificationSchemas.Validator(V.Decisions).DeepClone().AsBsonDocument;
        var fields = manual["$and"][0]["$jsonSchema"]["properties"].AsBsonDocument;
        fields["schemaVersion"]["enum"] = new BsonArray { 2 }; fields["review"] = Review();
        return new("$or", new BsonArray { PaymentVerificationSchemas.Validator(V.Decisions), manual });
    }
    internal static BsonDocument Validator(string name) {
        var fields = PolicyFields();
        if (name == B.Versions) {
            fields = new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
                { "declarationId", S.Number(1) }, { "revision", S.Number(1, long.MaxValue - 1) }, { "previousVersionId", Nullable(S.Number(1)) }, { "previousHash", Nullable(Hash) },
                { "source", PaymentVerificationSchemas.Source() }, { "failure", Failure() }, { "policy", S.Object(PolicyFields()) }, { "binding", Hash },
                { "fields", PaymentCorrectionSchemas.Fields() }, { "imageReviewed", new BsonDocument("enum", new BsonArray { true }) },
                { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 2 }, { "maxItems", 2 }, { "items", ReceivingAccountSchemas.Person() } } },
                { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "operation", S.Choice(B.Operation) }, { "requestKey", S.Text(128) },
                { "bodyHash", Hash }, { "snapshotHash", Hash }, { "recordedAt", Date } }; S.Metadata(fields);
        } else if (name != B.Policies) throw new ArgumentOutOfRangeException(nameof(name));
        fields.Add("fence", S.Number(0, long.MaxValue - 1));
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange(name == B.Policies ? "approvedAt" : "recordedAt") };
        if (name == B.Policies) { terms.Add(S.DateRange("expiresAt")); terms.Add(new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$expiresAt", "$approvedAt" }))); }
        return new("$and", terms);
    }
}
