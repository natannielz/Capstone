using MongoDB.Bson;
using B = UnitToko.Platform.Billing.BankReceiptBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class BankReceiptSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Accounts, B.Receipts, B.Confirmations];
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Flag => new("bsonType", "bool");
    private static BsonDocument Money => new() { { "bsonType", "decimal" }, { "minimum", new Decimal128(0.01m) }, { "maximum", new Decimal128(999999999999999.99m) } };
    private static BsonDocument Number => new() { { "bsonType", "int" }, { "minimum", 2 }, { "maximum", 501 } };
    internal static BsonDocument PolicyFields() => new() {
        { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } }, { "revision", S.Number(1, long.MaxValue-1) }, { "scope", ReceivingAccountSchemas.Scope() },
        { "physicalIdentity", Hash }, { "bankCode", S.Text(32) }, { "identityNamespace", S.Text(80) },
        { "cashMapping", S.Text(80) }, { "suspenseMapping", S.Text(80) }, { "trustedSourceReference", S.Text(256) }, { "operatingOwnerReference", S.Text(256) },
        { "approvalReference", S.Text(256) }, { "approvedAt", Date }, { "expiresAt", Date }, { "active", Flag } };
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies or B.Accounts => [new("physicalIdentity", 1)], B.Receipts => [new("canonicalIdentity", 1)],
        B.Confirmations => [new() { { "actorId", 1 }, { "requestKey", 1 } }, new() { { "importId", 1 }, { "rowNumber", 1 } }], _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        BsonDocument fields;
        if (name == B.Policies) fields = PolicyFields();
        else if (name == B.Accounts) fields = new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } }, { "scope", ReceivingAccountSchemas.Scope() },
            { "physicalIdentity", Hash }, { "identityNamespace", S.Text(80) }, { "firstReceiptId", S.Number(1) }, { "createdAt", Date }, { "snapshotHash", Hash } };
        else if (name == B.Receipts) fields = new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } }, { "scope", ReceivingAccountSchemas.Scope() },
            { "canonicalIdentity", Hash }, { "physicalIdentity", Hash }, { "identityNamespace", S.Text(80) }, { "transactionId", S.Text(80) }, { "bookedDate", S.Text(10) }, { "amount", Money },
            { "importId", S.Number(1) }, { "rowNumber", Number }, { "importHash", Hash }, { "rowHash", Hash }, { "confirmationId", S.Number(1) },
            { "actorId", S.Number(1) }, { "recordedAt", Date }, { "policy", S.Object(PolicyFields()) },
            { "journalId", Nullable(S.Number(1)) }, { "journalFingerprint", Nullable(Hash) }, { "postingFence", S.Number(0,1) }, { "snapshotHash", Nullable(Hash) } };
        else if (name == B.Confirmations) {
            fields = new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } }, { "scope", ReceivingAccountSchemas.Scope() }, { "receiptId", S.Number(1) }, { "canonicalIdentity", Hash }, { "createdReceipt", Flag },
                { "importId", S.Number(1) }, { "rowNumber", Number }, { "importHash", Hash }, { "rowHash", Hash }, { "binding", Hash }, { "policy", S.Object(PolicyFields()) },
                { "observed", S.Object(new() { { "accountNumber", S.Text(34) }, { "transactionId", S.Text(80) }, { "bookedDate", S.Text(10) }, { "amount", Money } }) },
                { "sourceReference", S.Text(256) }, { "attested", new BsonDocument("enum", new BsonArray { true }) },
                { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 2 }, { "maxItems", 2 }, { "items", ReceivingAccountSchemas.Person() } } },
                { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "operation", S.Choice(B.Operation) }, { "requestKey", S.Text(128) },
                { "bodyHash", Hash }, { "snapshotHash", Hash }, { "recordedAt", Date } };
            S.Metadata(fields);
        } else throw new ArgumentOutOfRangeException(nameof(name));
        fields.Add("fence", S.Number(0,long.MaxValue-1));
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) };
        if (name == B.Policies) { terms.Add(S.DateRange("approvedAt")); terms.Add(S.DateRange("expiresAt")); terms.Add(new BsonDocument("$expr",new BsonDocument("$gt",new BsonArray { "$expiresAt", "$approvedAt" }))); }
        else terms.Add(S.DateRange(name == B.Accounts ? "createdAt" : "recordedAt"));
        return new("$and",terms);
    }
}
