using MongoDB.Bson;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PaymentVerificationSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Decisions, B.Parties];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Nullable(BsonDocument rule) => new("anyOf", new BsonArray { rule, new BsonDocument("bsonType", "null") });
    private static BsonDocument Draft(string field) => PaymentDraftSchemas.Validator(UnitToko.Platform.Billing.PaymentDraftBson.Versions)["$and"][0]["$jsonSchema"]["properties"][field].DeepClone().AsBsonDocument;
    internal static BsonDocument Source() => S.Object(new() { { "declarationId", S.Number(1) }, { "revision", S.Number(1) }, { "versionId", S.Number(1) },
        { "versionHash", Hash }, { "submissionId", S.Number(1) }, { "submissionHash", Hash }, { "recordedBy", S.Number(1) }, { "uploaderId", S.Number(1) },
        { "reservationId", S.Number(1) }, { "evidence", PaymentSubmissionSchemas.Reference() }, { "input", Draft("input") }, { "invoices", Draft("invoices") },
        { "party", Draft("party") }, { "scopes", Draft("scopes") } });
    internal static BsonDocument Review() => S.Object(new() { { "binding", PaymentCorrectionSchemas.Binding() }, { "revision", S.Number(0, long.MaxValue - 1) },
        { "versionId", Nullable(S.Number(1)) }, { "versionHash", Nullable(Hash) }, { "originalFields", PaymentCorrectionSchemas.Fields() }, { "fields", PaymentCorrectionSchemas.Fields() } });
    internal static BsonDocument PolicyFields() => new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
        { "revision", S.Number(1, long.MaxValue - 1) }, { "scope", ReceivingAccountSchemas.Scope() }, { "payerVerificationProcedureReference", S.Text(256) },
        { "operatingOwnerReference", S.Text(256) }, { "approvalReference", S.Text(256) }, { "active", new BsonDocument("bsonType", "bool") }, { "approvedAt", Date }, { "expiresAt", Date } };
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies => [new() { { "scope.fulfillmentOrganizationId", 1 }, { "scope.shopId", 1 }, { "scope.channelId", 1 }, { "scope.warehouseId", 1 } }],
        B.Decisions => [new("declarationId", 1), new() { { "actorId", 1 }, { "requestKey", 1 } }], B.Parties => [new("_id", 1)], _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        if (name == B.Policies) fields = PolicyFields();
        else if (name == B.Parties) fields.AddRange(new BsonDocument { { "party", Draft("party") }, { "firstVerificationId", S.Number(1) },
            { "receiptHash", Hash }, { "recordedAt", Date }, { "snapshotHash", Hash } });
        else if (name == B.Decisions) {
            fields.AddRange(new BsonDocument { { "declarationId", S.Number(1) }, { "receiptId", S.Number(1) }, { "receiptHash", Hash },
                { "source", Source() }, { "review", Review() }, { "policy", S.Object(PolicyFields()) }, { "binding", Hash },
                { "payerVerificationReference", S.Text(256) }, { "imageReviewed", new BsonDocument("enum", new BsonArray { true }) }, { "bankReviewed", new BsonDocument("enum", new BsonArray { true }) }, { "payerVerified", new BsonDocument("enum", new BsonArray { true }) },
                { "resolutions", new BsonDocument { { "bsonType", "array" }, { "maxItems", 8 }, { "items", S.Object(new() { { "field", S.Text(40) }, { "reason", S.Text(1000) } }) } } },
                { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 2 }, { "maxItems", 2 }, { "items", ReceivingAccountSchemas.Person() } } },
                { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "operation", S.Choice(B.Operation) }, { "requestKey", S.Text(128) },
                { "bodyHash", Hash }, { "snapshotHash", Hash }, { "recordedAt", Date } });
            S.Metadata(fields);
        } else throw new ArgumentOutOfRangeException(nameof(name));
        fields.Add("fence", S.Number(0, long.MaxValue - 1));
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) };
        if (name == B.Policies) { terms.Add(S.DateRange("approvedAt")); terms.Add(S.DateRange("expiresAt")); terms.Add(new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$expiresAt", "$approvedAt" }))); }
        else terms.Add(S.DateRange("recordedAt"));
        return new("$and", terms);
    }
}
