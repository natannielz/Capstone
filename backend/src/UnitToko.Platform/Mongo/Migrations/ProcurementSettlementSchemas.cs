using MongoDB.Bson;
using UnitToko.Modules.Billing;
using B = UnitToko.Platform.Procurement.ProcurementSettlementBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ProcurementSettlementSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Events];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Bool => new("bsonType", "bool");
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Day => new() { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } };
    private static BsonDocument Code => new() { { "bsonType", "string" }, { "pattern", @"\A[A-Za-z][A-Za-z0-9._-]{0,63}\z" } };
    private static BsonDocument Header() => new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
    private static BsonDocument PolicyFields() {
        var fields = Header(); fields.AddRange(new BsonDocument { { "location", ProcurementRequestSchemas.Location() }, { "revision", S.Number(1, long.MaxValue - 1) },
            { "active", Bool }, { "approvedAt", Date }, { "expiresAt", Date }, { "rule", S.Choice("independent_exact_bank_debit") }, { "allowReferenceOnly", Bool }, { "currency", S.Choice("IDR") } });
        foreach (var name in new[] { "payingAccountId", "payingAccountVersionId", "bookId", "chartVersionId", "supplierAdvanceAccountId", "cashAccountId" }) fields.Add(name, S.Number(1));
        foreach (var name in new[] { "supplierAdvanceMapping", "supplierAdvanceAccountCode", "cashMapping", "cashAccountCode" }) fields.Add(name, Code);
        foreach (var name in new[] { "bankDebitVerificationProcedureReference", "beneficiaryVerificationProcedureReference", "trustedBankChannelReference", "operatingOwnerReference", "approvalReference" }) fields.Add(name, S.Text(256));
        return fields;
    }
    internal static BsonDocument Policy() => S.Object(PolicyFields());
    internal static BsonDocument Input() => S.Object(new() { { "policyId", S.Number(1) }, { "policyRevision", S.Number(1) }, { "bankImportId", S.Number(1) },
        { "bankRowNumber", new BsonDocument { { "bsonType", "int" }, { "minimum", 2 }, { "maximum", BankStatementParser.MaximumRows + 1 } } }, { "observedBeneficiary", SupplierBankSchemas.Details() },
        { "beneficiaryVerificationReference", S.Text(256) }, { "transferVerificationReference", S.Text(256) } });
    private static BsonDocument Account() => S.Object(new() { { "mappingKey", Code }, { "accountId", S.Number(1) }, { "accountCode", Code } });
    internal static BsonDocument Accounting() => S.Object(new() { { "bookId", S.Number(1) }, { "chartVersionId", S.Number(1) }, { "periodId", S.Number(1) },
        { "periodMonth", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])\z" } } }, { "supplierAdvance", Account() }, { "cash", Account() } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies => [new("_id", 1)], B.Events => [new("disbursementId", 1), new("canonicalIdentity", 1), new("auditId", 1), new() { { "actorId", 1 }, { "requestKey", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    private static BsonDocument Op(string operation, params BsonValue[] values) => new(operation, new BsonArray(values));
    internal static BsonDocument Validator(string name) {
        var fields = PolicyFields(); var checks = new BsonArray(); var terms = new BsonArray();
        if (name == B.Policies) {
            fields.Add("fence", S.Number(0, long.MaxValue - 1)); checks.Add(Op("$gt", "$expiresAt", "$approvedAt"));
            checks.Add(Op("$ne", "$supplierAdvanceAccountId", "$cashAccountId")); checks.Add(Op("$ne", "$supplierAdvanceMapping", "$cashMapping"));
            terms.Add(S.DateRange("approvedAt")); terms.Add(S.DateRange("expiresAt"));
        } else if (name == B.Events) {
            fields = Header(); fields.AddRange(new BsonDocument { { "operation", S.Choice(B.Operation) }, { "disbursementId", S.Number(1) }, { "disbursementHash", Hash }, { "draftId", S.Number(1) },
                { "authorizationId", S.Number(1) }, { "location", ProcurementRequestSchemas.Location() }, { "amount", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A(?!0\.00\z)(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" } } },
                { "canonicalIdentity", Hash }, { "debit", RefundSettlementSchemas.Debit() }, { "debitReference", Nullable(S.Text(160)) }, { "input", Input() }, { "policy", Policy() }, { "accounting", Accounting() },
                { "binding", Hash }, { "attested", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } },
                { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 2 }, { "maxItems", 3 }, { "items", ReceivingAccountSchemas.Person() } } },
                { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "recordedAt", Date }, { "postingDate", Day }, { "requestKey", S.Text(128) }, { "bodyHash", Hash },
                { "fence", S.Number(0, long.MaxValue - 1) }, { "postingFence", S.Number(0, 1) }, { "journalId", Nullable(S.Number(1)) }, { "journalFingerprint", Nullable(Hash) }, { "snapshotHash", Nullable(Hash) } }); S.Metadata(fields);
            foreach (var (left, right) in new[] { ("location", "policy.location"), ("canonicalIdentity", "debit.canonicalIdentity"), ("input.policyId", "policy._id"), ("input.policyRevision", "policy.revision"),
                ("input.bankImportId", "debit.importId"), ("input.bankRowNumber", "debit.rowNumber"), ("policy.payingAccountId", "debit.accountId"), ("policy.payingAccountVersionId", "debit.accountVersionId"),
                ("accounting.bookId", "policy.bookId"), ("accounting.chartVersionId", "policy.chartVersionId") }) checks.Add(Op("$eq", "$" + left, "$" + right));
            foreach (var (field, scope) in new[] { ("organizationId", "fulfillmentOrganizationId"), ("shopId", "shopId"), ("channelId", "channelId") }) checks.Add(Op("$eq", "$location." + field, "$debit.scope." + scope));
            checks.Add(Op("$or", Op("$eq", "$debit.scope.warehouseId", BsonNull.Value), Op("$eq", "$debit.scope.warehouseId", "$location.warehouseId")));
            foreach (var prefix in new[] { "supplierAdvance", "cash" }) foreach (var (suffix, field) in new[] { ("Mapping", "mappingKey"), ("AccountId", "accountId"), ("AccountCode", "accountCode") }) checks.Add(Op("$eq", "$accounting." + prefix + "." + field, "$policy." + prefix + suffix));
            checks.Add(Op("$ne", "$accounting.supplierAdvance.accountId", "$accounting.cash.accountId")); checks.Add(Op("$ne", "$accounting.supplierAdvance.mappingKey", "$accounting.cash.mappingKey"));
            checks.Add(Op("$eq", new BsonDocument("$convert", new BsonDocument { { "input", "$amount" }, { "to", "decimal" }, { "onError", -1 }, { "onNull", -1 } }), "$debit.amount"));
            checks.Add(Op("$eq", "$policy.active", true)); checks.Add(Op("$eq", "$policy.allowReferenceOnly", true)); checks.Add(Op("$gte", "$recordedAt", "$policy.approvedAt")); checks.Add(Op("$lt", "$recordedAt", "$policy.expiresAt"));
            checks.Add(Op("$eq", "$postingDate", new BsonDocument("$dateToString", new BsonDocument { { "date", "$recordedAt" }, { "format", "%Y-%m-%d" }, { "timezone", "Asia/Jakarta" } })));
            checks.Add(Op("$eq", "$accounting.periodMonth", new BsonDocument("$substrCP", new BsonArray { "$postingDate", 0, 7 })));
            checks.Add(Op("$gte", "$debit.bookedDate", "2000-01-01")); checks.Add(Op("$lte", "$debit.bookedDate", "$postingDate"));
            checks.Add(Op("$ne", new BsonDocument("$dateFromString", new BsonDocument { { "dateString", "$debit.bookedDate" }, { "format", "%Y-%m-%d" }, { "onError", BsonNull.Value }, { "onNull", BsonNull.Value } }), BsonNull.Value));
            checks.Add(Op("$or", Op("$and", Op("$eq", "$journalId", BsonNull.Value), Op("$eq", "$journalFingerprint", BsonNull.Value), Op("$eq", "$snapshotHash", BsonNull.Value)),
                Op("$and", Op("$ne", "$journalId", BsonNull.Value), Op("$ne", "$journalFingerprint", BsonNull.Value), Op("$ne", "$snapshotHash", BsonNull.Value), Op("$eq", "$postingFence", 1L))));
            foreach (var date in new[] { "recordedAt", "policy.approvedAt", "policy.expiresAt" }) terms.Add(S.DateRange(date));
        } else throw new ArgumentOutOfRangeException(nameof(name));
        terms.Insert(0, new BsonDocument("$jsonSchema", S.Object(fields))); terms.Add(new BsonDocument("$expr", new BsonDocument("$and", checks))); return new("$and", terms);
    }
}
