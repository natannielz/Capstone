using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using D = UnitToko.Platform.Billing.RefundSettlementBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class RefundSettlementSchemas
{
    internal static readonly string[] Collections = [D.Policies, D.Events];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Day => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z" } };
    private static BsonDocument Version(long minimum = 0) => S.Number(minimum, long.MaxValue - 1);
    private static BsonDocument Money(decimal minimum = 0) => new() { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(minimum) },
        { "maximum", new BsonDecimal128(DomainValues.MaximumMoney) }, { "multipleOf", new BsonDecimal128(.01m) } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Vector(BsonDocument item, int minimum = 1, int maximum = 100) => new() { { "bsonType", "array" },
        { "minItems", minimum }, { "maxItems", maximum }, { "items", item } };
    private static BsonDocument Header() => new() { { "_id", S.Number(1) },
        { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
    internal static BsonDocument PolicyFields() {
        var fields = Header(); fields.Add("revision", Version(1)); fields.Add("scope", ReceivingAccountSchemas.Scope());
        fields.Add("rule", S.Choice("independent_exact_bank_debit"));
        foreach (var field in new[] { "bankDebitVerificationProcedureReference", "beneficiaryVerificationProcedureReference", "operatingOwnerReference", "approvalReference" }) fields.Add(field, S.Text(256));
        fields.Add("active", new BsonDocument("bsonType", "bool")); fields.Add("approvedAt", Date); fields.Add("expiresAt", Date); return fields;
    }
    internal static BsonDocument Debit() => S.Object(new() { { "importId", S.Number(1) }, { "importHash", Hash },
        { "rowNumber", new BsonDocument { { "bsonType", "int" }, { "minimum", 2 }, { "maximum", BankStatementParser.MaximumRows + 1 } } },
        { "rowHash", Hash }, { "accountId", S.Number(1) }, { "accountVersionId", S.Number(1) }, { "physicalIdentity", Hash },
        { "identityNamespace", S.Text(80) }, { "transactionId", S.Text(80) }, { "canonicalIdentity", Hash }, { "bookedDate", Day },
        { "amount", Money(.01m) }, { "importedBy", S.Number(1) }, { "scope", ReceivingAccountSchemas.Scope() }, { "snapshotHash", Hash } });
    internal static BsonDocument Input() => S.Object(new() { { "bankImportId", S.Number(1) },
        { "bankRowNumber", new BsonDocument { { "bsonType", "int" }, { "minimum", 2 }, { "maximum", BankStatementParser.MaximumRows + 1 } } },
        { "beneficiaryVerificationReference", S.Text(256) }, { "transferVerificationReference", S.Text(256) } });
    private static BsonDocument Account() => S.Object(new() { { "mappingKey", S.Text(80) }, { "accountId", S.Number(1) }, { "accountCode", S.Text(80) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        D.Policies => PaymentAllocationSchemas.Keys(UnitToko.Platform.Billing.PaymentAllocationBson.Policies),
        D.Events => [new() { { "actorId", 1 }, { "requestKey", 1 } }, new("payoutId", 1), new("financialApprovalId", 1), new("canonicalIdentity", 1),
            new() { { "receipts.receiptId", 1 }, { "receipts.version", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    private static BsonDocument Op(string op, params BsonValue[] values) => new(op, new BsonArray(values));
    private static BsonDocument SortedDistinct(string path, string field) {
        var values = new BsonDocument("$map", new BsonDocument { { "input", "$" + path }, { "as", "item" }, { "in", "$$item." + field } });
        return Op("$and", Op("$eq", new BsonDocument("$size", values), new BsonDocument("$size", Op("$setUnion", values, new BsonArray()))),
            Op("$eq", values, new BsonDocument("$sortArray", new BsonDocument { { "input", values }, { "sortBy", 1 } })));
    }
    internal static BsonDocument Validator(string name) {
        var fields = Header(); var terms = new BsonArray(); var expressions = new BsonArray();
        if (name == D.Policies) {
            fields = PolicyFields(); fields.Add("fence", Version());
            terms.Add(S.DateRange("approvedAt")); terms.Add(S.DateRange("expiresAt"));
            expressions.Add(Op("$gt", "$expiresAt", "$approvedAt")); expressions.Add(Op("$eq", "$scope.warehouseId", BsonNull.Value));
        } else if (name == D.Events) {
            fields.AddRange(new BsonDocument { { "operation", S.Choice(D.Operation) }, { "payoutId", S.Number(1) }, { "payoutHash", Hash },
                { "financialApprovalId", S.Number(1) }, { "approvalHash", Hash }, { "caseId", S.Number(1) }, { "returnId", S.Number(1) }, { "invoiceId", S.Number(1) },
                { "party", RefundPayoutSchemas.Party() }, { "invoice", PaymentAllocationSchemas.InvoiceSource() }, { "amount", Money(.01m) },
                { "canonicalIdentity", Hash }, { "debit", Debit() }, { "input", Input() }, { "policy", S.Object(PolicyFields()) }, { "binding", Hash },
                { "attested", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } },
                { "receipts", Vector(S.Object(new() { { "receiptId", S.Number(1) }, { "receiptHash", Hash }, { "receiptTotal", Money(.01m) },
                    { "before", PaymentAllocationSchemas.Balance(true) }, { "version", Version(1) }, { "paidBefore", Money() }, { "paidAfter", Money(.01m) }, { "pending", Money() }, { "available", Money() } })) },
                { "releases", Vector(S.Object(new() { { "originalAllocationId", S.Number(1) }, { "receiptId", S.Number(1) }, { "originallyAllocated", Money(.01m) },
                    { "previouslyReleased", Money() }, { "amount", Money(.01m) }, { "releasedAfter", Money(.01m) } })) },
                { "people", Vector(ReceivingAccountSchemas.Person(), 2, 3) }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
                { "recordedAt", Date }, { "postingDate", Day }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "fence", Version() },
                { "postingFence", S.Number(0, 1) }, { "journalId", Nullable(S.Number(1)) }, { "journalFingerprint", Nullable(Hash) }, { "snapshotHash", Nullable(Hash) },
                { "liabilityAccount", Account() }, { "cashAccount", Account() } }); S.Metadata(fields);
            foreach (var path in new[] { "recordedAt", "policy.approvedAt", "policy.expiresAt" }) terms.Add(S.DateRange(path));
            foreach (var (left, right) in new[] { ("invoiceId", "invoice.invoiceId"), ("canonicalIdentity", "debit.canonicalIdentity"),
                ("amount", "debit.amount"), ("input.bankImportId", "debit.importId"), ("input.bankRowNumber", "debit.rowNumber") }) expressions.Add(Op("$eq", "$" + left, "$" + right));
            foreach (var field in new[] { "fulfillmentOrganizationId", "shopId", "channelId" }) {
                expressions.Add(Op("$eq", "$party." + field, "$debit.scope." + field)); expressions.Add(Op("$eq", "$party." + field, "$policy.scope." + field));
            }
            foreach (var path in new[] { "policy.scope.warehouseId", "debit.scope.warehouseId" }) expressions.Add(Op("$eq", "$" + path, BsonNull.Value));
            expressions.Add(Op("$eq", "$policy.active", true)); expressions.Add(Op("$gte", "$recordedAt", "$policy.approvedAt")); expressions.Add(Op("$lt", "$recordedAt", "$policy.expiresAt"));
            expressions.Add(Op("$eq", "$postingDate", new BsonDocument("$dateToString", new BsonDocument { { "date", "$recordedAt" }, { "format", "%Y-%m-%d" }, { "timezone", "Asia/Jakarta" } })));
            expressions.Add(Op("$gte", "$debit.bookedDate", "2000-01-01")); expressions.Add(Op("$lte", "$debit.bookedDate", "$postingDate"));
            expressions.Add(Op("$ne", new BsonDocument("$dateFromString", new BsonDocument { { "dateString", "$debit.bookedDate" }, { "format", "%Y-%m-%d" }, { "onError", BsonNull.Value }, { "onNull", BsonNull.Value } }), BsonNull.Value));
            expressions.Add(Op("$ne", "$liabilityAccount.accountId", "$cashAccount.accountId")); expressions.Add(Op("$ne", "$liabilityAccount.mappingKey", "$cashAccount.mappingKey"));
            expressions.Add(Op("$eq", "$amount", new BsonDocument("$sum", "$releases.amount")));
            foreach (var (path, field) in new[] { ("receipts", "receiptId"), ("releases", "originalAllocationId"), ("people", "userId") }) expressions.Add(SortedDistinct(path, field));
            expressions.Add(Op("$or", Op("$and", Op("$eq", "$journalId", BsonNull.Value), Op("$eq", "$journalFingerprint", BsonNull.Value), Op("$eq", "$snapshotHash", BsonNull.Value)),
                Op("$and", Op("$ne", "$journalId", BsonNull.Value), Op("$ne", "$journalFingerprint", BsonNull.Value), Op("$ne", "$snapshotHash", BsonNull.Value), Op("$eq", "$postingFence", 1L))));
            expressions.Add(BsonDocument.Parse("""
                { "$eq": [ { "$map": { "input": "$receipts", "as": "r", "in": "$$r.receiptId" } },
                  { "$sortArray": { "input": { "$setUnion": [ { "$map": { "input": "$releases", "as": "r", "in": "$$r.receiptId" } }, [] ] }, "sortBy": 1 } } ] }
                """));
            expressions.Add(BsonDocument.Parse("""
                { "$allElementsTrue": [{ "$map": { "input": "$receipts", "as": "r", "in": { "$and": [
                  { "$eq": ["$$r.version", { "$add": ["$$r.before.version", 1] }] }, { "$gt": ["$$r.before.version", 0] },
                  { "$ne": ["$$r.before.lastEventId", null] }, { "$ne": ["$$r.before.lastEventHash", null] },
                  { "$lte": [{ "$add": ["$$r.before.allocated", "$$r.before.reservedForRefund"] }, "$$r.receiptTotal"] },
                  { "$eq": ["$$r.paidAfter", { "$add": ["$$r.paidBefore", { "$sum": { "$map": {
                    "input": { "$filter": { "input": "$releases", "as": "l", "cond": { "$eq": ["$$l.receiptId", "$$r.receiptId"] } } },
                    "as": "l", "in": "$$l.amount" } } }] }] },
                  { "$lte": ["$$r.paidAfter", "$$r.before.reservedForRefund"] },
                  { "$eq": ["$$r.pending", { "$subtract": ["$$r.before.reservedForRefund", "$$r.paidAfter"] }] },
                  { "$eq": ["$$r.available", { "$subtract": ["$$r.receiptTotal", { "$add": ["$$r.before.allocated", "$$r.before.reservedForRefund"] }] }] }
                ] } } }] }
                """));
            expressions.Add(BsonDocument.Parse("""
                { "$allElementsTrue": [{ "$map": { "input": "$releases", "as": "l", "in": { "$and": [
                  { "$eq": ["$$l.releasedAfter", { "$add": ["$$l.previouslyReleased", "$$l.amount"] }] },
                  { "$lte": ["$$l.releasedAfter", "$$l.originallyAllocated"] }, { "$lte": ["$$l.originallyAllocated", "$invoice.total"] },
                  { "$eq": [{ "$size": { "$filter": { "input": "$receipts", "as": "r", "cond": { "$and": [
                    { "$eq": ["$$r.receiptId", "$$l.receiptId"] }, { "$lte": ["$$l.originallyAllocated", "$$r.receiptTotal"] }
                  ] } } } }, 1] }
                ] } } }] }
                """));
            expressions.Add(BsonDocument.Parse("""
                { "$and": [ { "$ne": ["$actorId", "$debit.importedBy"] },
                  { "$eq": [{ "$size": { "$filter": { "input": "$people", "as": "p", "cond": { "$eq": ["$$p.userId", "$actorId"] } } } }, 1] },
                  { "$eq": [{ "$size": { "$filter": { "input": "$people", "as": "p", "cond": { "$eq": ["$$p.userId", "$debit.importedBy"] } } } }, 1] },
                  { "$allElementsTrue": [{ "$map": { "input": "$people", "as": "p", "in": { "$and": [
                    { "$eq": ["$$p.verification", "Verified"] }, { "$gt": ["$$p.personId", 0] }, { "$gt": ["$$p.version", 0] }
                  ] } } }] },
                  { "$let": { "vars": { "actor": { "$arrayElemAt": [{ "$filter": { "input": "$people", "as": "p", "cond": { "$eq": ["$$p.userId", "$actorId"] } } }, 0] } },
                    "in": { "$allElementsTrue": [{ "$map": { "input": "$people", "as": "p", "in": { "$or": [
                      { "$eq": ["$$p.userId", "$actorId"] }, { "$ne": ["$$p.personId", "$$actor.personId"] }
                    ] } } }] } } }
                ] }
                """));
        } else throw new ArgumentOutOfRangeException(nameof(name));
        terms.Insert(0, new BsonDocument("$jsonSchema", S.Object(fields)));
        if (expressions.Count > 0) terms.Add(new BsonDocument("$expr", Op("$and", expressions.ToArray()))); return new("$and", terms);
    }
}
