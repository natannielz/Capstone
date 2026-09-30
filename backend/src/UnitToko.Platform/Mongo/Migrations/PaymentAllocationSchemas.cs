using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Billing;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
// Forward-only storage owned by payment allocation. No business policies are seeded.
internal static class PaymentAllocationSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Events, B.Receipts, B.Invoices];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Money(decimal minimum = 0) => new() { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(minimum) },
        { "maximum", new BsonDecimal128(DomainValues.MaximumMoney) }, { "multipleOf", new BsonDecimal128(.01m) } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Party => PaymentVerificationSchemas.Source()["properties"]["party"].DeepClone().AsBsonDocument;
    internal static BsonDocument InvoiceSource() {
        var fields = PaymentVerificationSchemas.Source()["properties"]["invoices"]["items"]["properties"].DeepClone().AsBsonDocument;
        fields.Add("arMapping", S.Text(80)); fields.Add("arAccountId", S.Number(1)); fields.Add("arAccountCode", S.Text(80)); fields.Add("dueDate", S.Text(10)); return S.Object(fields);
    }
    internal static BsonDocument Balance(bool receipt) => S.Object(new() { { "version", S.Number(0, long.MaxValue - 1) },
        { receipt ? "allocated" : "paid", Money() }, { receipt ? "reservedForRefund" : "credited", Money() },
        { "lastEventId", Nullable(S.Number(1)) }, { "lastEventHash", Nullable(Hash) } });
    internal static BsonDocument PolicyFields() => new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
        { "revision", S.Number(1, long.MaxValue - 1) }, { "scope", ReceivingAccountSchemas.Scope() }, { "suspenseMapping", S.Text(80) }, { "arMapping", S.Text(80) },
        { "procedureReference", S.Text(256) }, { "operatingOwnerReference", S.Text(256) }, { "approvalReference", S.Text(256) }, { "active", new BsonDocument("bsonType", "bool") },
        { "approvedAt", new BsonDocument("bsonType", "date") }, { "expiresAt", new BsonDocument("bsonType", "date") } };
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies => [new() { { "scope.fulfillmentOrganizationId", 1 }, { "scope.shopId", 1 }, { "scope.channelId", 1 }, { "scope.warehouseId", 1 } }],
        B.Events => [new() { { "actorId", 1 }, { "requestKey", 1 } }, new() { { "receiptId", 1 }, { "receiptVersion", 1 } }, new() { { "lines.invoiceId", 1 }, { "lines.version", 1 } }],
        B.Receipts or B.Invoices => [new("_id", 1)], _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        if (name == B.Policies) fields = PolicyFields();
        else if (name is B.Receipts or B.Invoices) {
            fields.AddRange(Balance(name == B.Receipts)["properties"].DeepClone().AsBsonDocument);
            fields["version"] = S.Number(1, long.MaxValue - 1); fields["lastEventId"] = S.Number(1); fields["lastEventHash"] = Hash;
            fields.Add("sourceHash", Hash); fields.Add("party", Party); fields.Add("total", Money(.01m)); fields.Add("snapshotHash", Hash);
        } else if (name == B.Events) {
            fields.AddRange(new BsonDocument { { "verificationId", S.Number(1) }, { "verificationHash", Hash }, { "declarationId", S.Number(1) }, { "declarationRevision", S.Number(1) }, { "recordedBy", S.Number(1) },
                { "receiptId", S.Number(1) }, { "receiptHash", Hash }, { "receiptTotal", Money(.01m) }, { "receiptVersion", S.Number(1, long.MaxValue - 1) },
                { "suspenseAccount", S.Object(new() { { "accountId", S.Number(1) }, { "accountCode", S.Text(80) } }) },
                { "receiptBefore", Balance(true) }, { "party", Party }, { "policy", S.Object(PolicyFields()) }, { "binding", Hash }, { "amount", Money(.01m) }, { "unapplied", Money() },
                { "lines", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", PaymentAllocationPolicy.MaximumInvoices }, { "items", S.Object(new() {
                    { "invoiceId", S.Number(1) }, { "version", S.Number(1, long.MaxValue - 1) }, { "source", InvoiceSource() }, { "before", Balance(false) }, { "amount", Money(.01m) }, { "remaining", Money() } }) } } },
                { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "recordedAt", new BsonDocument("bsonType", "date") }, { "postingDate", S.Text(10) },
                { "operation", S.Choice(B.Operation) }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Nullable(Hash) },
                { "journalId", Nullable(S.Number(1)) }, { "journalFingerprint", Nullable(Hash) }, { "postingFence", S.Number(0, 1) } });
            S.Metadata(fields);
        } else throw new ArgumentOutOfRangeException(nameof(name));
        fields.Add("fence", S.Number(0, long.MaxValue - 1)); var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) };
        if (name == B.Policies) { terms.Add(S.DateRange("approvedAt")); terms.Add(S.DateRange("expiresAt")); terms.Add(new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$expiresAt", "$approvedAt" }))); }
        else if (name == B.Events) terms.Add(S.DateRange("recordedAt"));
        else terms.Add(new BsonDocument("$expr", new BsonDocument("$lte", new BsonArray { new BsonDocument("$add", new BsonArray {
            name == B.Receipts ? "$allocated" : "$paid", name == B.Receipts ? "$reservedForRefund" : "$credited" }), "$total" })));
        return new("$and", terms);
    }
}
