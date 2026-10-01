using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using E = UnitToko.Platform.Billing.ReturnEntitlementBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class ReturnFinancialApprovalSchemas
{
    internal static readonly string[] Collections = [A.Policies, A.Events];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Version(long minimum = 0) => S.Number(minimum, long.MaxValue - 1);
    private static BsonDocument Money(decimal minimum = 0) => new() { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(minimum) },
        { "maximum", new BsonDecimal128(DomainValues.MaximumMoney) }, { "multipleOf", new BsonDecimal128(.01m) } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Array(BsonDocument item, int maximum = 100) => new() { { "bsonType", "array" }, { "minItems", 0 }, { "maxItems", maximum }, { "items", item } };
    private static BsonDocument Header() => new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
    internal static BsonDocument PolicyFields() {
        var fields = Header(); fields.Add("revision", Version(1)); fields.Add("scope", ReceivingAccountSchemas.Scope());
        fields.Add("creditRule", S.Choice("credit_original_components")); fields.Add("reservationRule", S.Choice("reserve_released_allocations"));
        fields.Add("independenceRule", S.Choice("request_and_funding_makers"));
        foreach (var field in new[] { "arMapping", "refundLiabilityMapping", "refundLiabilityAccountCode" }) fields.Add(field, S.Text(80));
        fields.Add("refundLiabilityAccountId", S.Number(1));
        foreach (var field in new[] { "creditNoteProcedureReference", "taxTreatmentReference", "chargeTreatmentReference", "refundReservationProcedureReference", "operatingOwnerReference", "approvalReference" }) fields.Add(field, S.Text(256));
        fields.Add("active", new BsonDocument("bsonType", "bool")); fields.Add("approvedAt", Date); fields.Add("expiresAt", Date); return fields;
    }
    internal static BsonDocument Input() => S.Object(new() { { "expectedInvoiceVersion", Version() }, { "releases", Array(S.Object(new() {
        { "originalAllocationId", S.Number(1) }, { "expectedReceiptVersion", Version() }, { "amount", Money(.01m) } })) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        A.Policies => PaymentAllocationSchemas.Keys(UnitToko.Platform.Billing.PaymentAllocationBson.Policies),
        A.Events => [new() { { "actorId", 1 }, { "requestKey", 1 } }, new("returnId", 1), new() { { "invoiceId", 1 }, { "invoiceVersion", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    private static BsonDocument Op(string op, params BsonValue[] args) => new(op, new BsonArray(args));
    private static BsonDocument Sum(string path) => new("$sum", "$" + path);
    private static BsonDocument SortedDistinct(string path, string id) {
        var values = new BsonDocument("$map", new BsonDocument { { "input", "$" + path }, { "as", "entry" }, { "in", "$$entry." + id } });
        return Op("$and", Op("$eq", new BsonDocument("$size", values), new BsonDocument("$size", Op("$setUnion", values, new BsonArray()))),
            Op("$eq", values, new BsonDocument("$sortArray", new BsonDocument { { "input", values }, { "sortBy", 1 } })));
    }
    private static BsonDocument StatePair(string path) => Op("$and",
        Op("$eq", Op("$eq", "$" + path + ".version", 0L), Op("$and", Op("$eq", "$" + path + ".lastEventId", BsonNull.Value), Op("$eq", "$" + path + ".lastEventHash", BsonNull.Value))),
        Op("$eq", Op("$ne", "$" + path + ".lastEventId", BsonNull.Value), Op("$ne", "$" + path + ".lastEventHash", BsonNull.Value)));
    internal static BsonDocument Validator(string name) {
        var fields = Header(); var expressions = new BsonArray(); var terms = new BsonArray();
        if (name == A.Policies) {
            fields = PolicyFields(); fields.Add("fence", Version()); terms.Add(S.DateRange("approvedAt")); terms.Add(S.DateRange("expiresAt"));
            expressions.Add(Op("$gt", "$expiresAt", "$approvedAt")); expressions.Add(Op("$ne", "$arMapping", "$refundLiabilityMapping"));
        } else if (name == A.Events) {
            fields.AddRange(new BsonDocument { { "operation", S.Choice(A.Operation) },
                { "request", ReturnEntitlementSchemas.Validator(E.Requests)["$and"][0]["$jsonSchema"].DeepClone() },
                { "caseId", S.Number(1) }, { "returnId", S.Number(1) }, { "invoiceId", S.Number(1) }, { "action", S.Choice("credit", "refund") },
                { "invoice", PaymentAllocationSchemas.InvoiceSource() }, { "party", PaymentVerificationSchemas.Source()["properties"]["party"].DeepClone() },
                { "invoiceBefore", PaymentAllocationSchemas.Balance(false) }, { "invoiceVersion", Version(1) },
                { "receipts", Array(S.Object(new() { { "receiptId", S.Number(1) }, { "receiptHash", Hash }, { "receiptTotal", Money(.01m) },
                    { "before", PaymentAllocationSchemas.Balance(true) }, { "version", Version(1) }, { "released", Money(.01m) }, { "remainingReceipt", Money() } })) },
                { "links", Array(S.Object(new() { { "originalAllocationId", S.Number(1) }, { "originalAllocationHash", Hash }, { "originalJournalId", S.Number(1) }, { "originalJournalFingerprint", Hash },
                    { "allocationActorId", S.Number(1) }, { "verifiedBy", S.Number(1) }, { "verificationId", S.Number(1) }, { "verificationHash", Hash },
                    { "declarationId", S.Number(1) }, { "declarationRevision", Version(1) }, { "recordedBy", S.Number(1) },
                    { "receiptId", S.Number(1) }, { "receiptHash", Hash }, { "receiptTotal", Money(.01m) },
                    { "suspenseAccount", S.Object(new() { { "accountId", S.Number(1) }, { "accountCode", S.Text(80) } }) },
                    { "source", PaymentAllocationSchemas.InvoiceSource() }, { "originallyAllocated", Money(.01m) }, { "previouslyReleased", Money() }, { "amount", Money(.01m) } })) },
                { "input", Input() }, { "credit", Money(.01m) }, { "reservedRefund", Money() }, { "remainingInvoice", Money() },
                { "policy", S.Object(PolicyFields()) }, { "binding", Hash }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
                { "recordedAt", Date }, { "postingDate", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z" } } },
                { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "fence", Version() }, { "postingFence", S.Number(0, 1) },
                { "journalId", Nullable(S.Number(1)) }, { "journalFingerprint", Nullable(Hash) }, { "snapshotHash", Nullable(Hash) },
                { "people", Array(ReceivingAccountSchemas.Person(), 312) } }); S.Metadata(fields);
            foreach (var field in new[] { "recordedAt", "policy.approvedAt", "policy.expiresAt" }) terms.Add(S.DateRange(field));
            foreach (var (left, right) in new[] { ("$caseId", "$request.caseId"), ("$returnId", "$request._id"), ("$invoiceId", "$request.invoiceId"),
                ("$invoiceId", "$invoice.invoiceId"), ("$action", "$request.action"), ("$credit", "$request.credit"),
                ("$invoice", "$request.source.invoice"), ("$input.expectedInvoiceVersion", "$invoiceBefore.version") }) expressions.Add(Op("$eq", left, right));
            expressions.Add(Op("$eq", "$request.credit", Op("$add", "$request.net", "$request.tax", "$request.charges")));
            expressions.Add(Op("$eq", "$invoiceVersion", Op("$add", "$invoiceBefore.version", 1L)));
            expressions.Add(StatePair("invoiceBefore"));
            expressions.Add(Op("$gte", "$recordedAt", "$request.requestedAt")); expressions.Add(Op("$gte", "$recordedAt", "$policy.approvedAt"));
            expressions.Add(Op("$lt", "$recordedAt", "$policy.expiresAt"));
            expressions.Add(Op("$eq", "$reservedRefund", Sum("links.amount"))); expressions.Add(Op("$eq", "$reservedRefund", Sum("receipts.released")));
            expressions.Add(Op("$lte", "$reservedRefund", "$credit")); expressions.Add(Op("$lte", "$reservedRefund", "$invoiceBefore.paid"));
            expressions.Add(Op("$lte", Op("$add", "$invoiceBefore.paid", "$invoiceBefore.credited"), "$invoice.total"));
            expressions.Add(Op("$eq", "$remainingInvoice", Op("$subtract", "$invoice.total", Op("$add", Op("$subtract", "$invoiceBefore.paid", "$reservedRefund"), "$invoiceBefore.credited", "$credit"))));
            expressions.Add(Op("$or", Op("$and", Op("$eq", "$action", "credit"), Op("$eq", new BsonDocument("$size", "$links"), 0), Op("$eq", new BsonDocument("$size", "$receipts"), 0)),
                Op("$and", Op("$eq", "$action", "refund"), Op("$gt", new BsonDocument("$size", "$links"), 0), Op("$gt", new BsonDocument("$size", "$receipts"), 0), Op("$gt", "$reservedRefund", new BsonDecimal128(0m)))));
            foreach (var (path, id) in new[] { ("links", "originalAllocationId"), ("input.releases", "originalAllocationId"), ("receipts", "receiptId"), ("people", "userId") }) expressions.Add(SortedDistinct(path, id));
            expressions.Add(Op("$eq", new BsonDocument("$size", "$links"), new BsonDocument("$size", "$input.releases")));
            // The posting owner increments its fence before finalizing the journal in the same transaction.
            expressions.Add(Op("$or", Op("$and", Op("$eq", "$journalId", BsonNull.Value), Op("$eq", "$journalFingerprint", BsonNull.Value), Op("$eq", "$snapshotHash", BsonNull.Value)),
                Op("$and", Op("$ne", "$journalId", BsonNull.Value), Op("$ne", "$journalFingerprint", BsonNull.Value), Op("$ne", "$snapshotHash", BsonNull.Value), Op("$eq", "$postingFence", 1L))));
            expressions.Add(new BsonDocument("$allElementsTrue", new BsonArray { new BsonDocument("$map", new BsonDocument { { "input", "$receipts" }, { "as", "receipt" }, { "in", Op("$and",
                Op("$eq", "$$receipt.version", Op("$add", "$$receipt.before.version", 1L)), Op("$lte", "$$receipt.released", "$$receipt.before.allocated"),
                Op("$eq", "$$receipt.remainingReceipt", Op("$subtract", "$$receipt.receiptTotal", Op("$add", "$$receipt.before.allocated", "$$receipt.before.reservedForRefund")))) } }) }));
            expressions.Add(BsonDocument.Parse("""
                { "$eq": [
                  { "$map": { "input": "$links", "as": "link", "in": { "id": "$$link.originalAllocationId", "amount": "$$link.amount" } } },
                  { "$map": { "input": "$input.releases", "as": "line", "in": { "id": "$$line.originalAllocationId", "amount": "$$line.amount" } } }
                ] }
                """));
            expressions.Add(BsonDocument.Parse("""
                { "$allElementsTrue": [{ "$map": { "input": "$receipts", "as": "receipt", "in": { "$and": [
                  { "$eq": ["$$receipt.released", { "$sum": { "$map": { "input": { "$filter": { "input": "$links", "as": "link", "cond": { "$eq": ["$$link.receiptId", "$$receipt.receiptId"] } } }, "as": "link", "in": "$$link.amount" } } }] },
                  { "$eq": [{ "$eq": ["$$receipt.before.version", 0] }, { "$and": [{ "$eq": ["$$receipt.before.lastEventId", null] }, { "$eq": ["$$receipt.before.lastEventHash", null] }] }] },
                  { "$eq": [{ "$ne": ["$$receipt.before.lastEventId", null] }, { "$ne": ["$$receipt.before.lastEventHash", null] }] }
                ] } } }] }
                """));
            expressions.Add(BsonDocument.Parse("""
                { "$allElementsTrue": [{ "$map": { "input": "$links", "as": "link", "in": { "$and": [
                  { "$eq": ["$$link.source", "$invoice"] },
                  { "$lte": [{ "$add": ["$$link.previouslyReleased", "$$link.amount"] }, "$$link.originallyAllocated"] },
                  { "$lte": ["$$link.originallyAllocated", "$invoice.total"] },
                  { "$lte": ["$$link.originallyAllocated", "$$link.receiptTotal"] },
                  { "$eq": [{ "$size": { "$filter": { "input": "$receipts", "as": "receipt", "cond": { "$and": [
                    { "$eq": ["$$receipt.receiptId", "$$link.receiptId"] }, { "$eq": ["$$receipt.receiptHash", "$$link.receiptHash"] },
                    { "$eq": ["$$receipt.receiptTotal", "$$link.receiptTotal"] },
                    { "$eq": [{ "$size": { "$filter": { "input": "$input.releases", "as": "line", "cond": { "$and": [
                      { "$eq": ["$$line.originalAllocationId", "$$link.originalAllocationId"] },
                      { "$eq": ["$$line.expectedReceiptVersion", "$$receipt.before.version"] }
                    ] } } } }, 1] }
                  ] } } } }, 1] }
                ] } } }] }
                """));
        } else throw new ArgumentOutOfRangeException(nameof(name));
        terms.Insert(0, new BsonDocument("$jsonSchema", S.Object(fields))); if (expressions.Count > 0) terms.Add(new BsonDocument("$expr", new BsonDocument("$and", expressions)));
        return new("$and", terms);
    }
}
