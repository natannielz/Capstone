using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.ProcurementDisbursementBson;
using F = UnitToko.Platform.Procurement.ProcurementFundingBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ProcurementDisbursementSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Drafts, B.Records, B.Heads, B.Allocations];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Bool => new("bsonType", "bool");
    private static BsonDocument Nullable(BsonDocument rule) => new("anyOf", new BsonArray { rule, new BsonDocument("bsonType", "null") });
    private static BsonDocument Money(bool positive = false) => new() { { "bsonType", "string" }, { "pattern", positive ? @"\A(?!0\.00\z)(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" : @"\A(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" } };
    private static BsonDocument Header() => new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
    private static BsonDocument Person => S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } });
    internal static BsonDocument Input() {
        var fields = new BsonDocument("location", ProcurementRequestSchemas.Location());
        foreach (var name in new[] { "fundingRequestId", "fundingDecisionId", "authorizationId", "supplierId", "beneficiaryVersionId", "beneficiaryVersion", "payingAccountId", "payingAccountVersionId", "policyId", "policyRevision" }) fields.Add(name, S.Number(1));
        fields.Add("amount", Money(true)); fields.Add("transferDate", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } }); fields.Add("transferReference", S.Text(160)); return S.Object(fields);
    }
    private static BsonDocument PolicyFields() { var fields = Header(); fields.AddRange(new BsonDocument { { "location", ProcurementRequestSchemas.Location() }, { "revision", S.Number(1, long.MaxValue - 1) },
        { "payingAccountId", S.Number(1) }, { "payingAccountVersionId", S.Number(1) }, { "allowPartialRecordings", Bool }, { "reserveUnverified", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } },
        { "payoutRecordingProcedureReference", S.Text(256) }, { "beneficiaryVerificationProcedureReference", S.Text(256) }, { "operatingOwnerReference", S.Text(256) }, { "approvalReference", S.Text(256) },
        { "approvedAt", Date }, { "expiresAt", Date }, { "active", Bool } }); return fields; }
    internal static BsonDocument Policy() => S.Object(PolicyFields());
    internal static BsonDocument Capacity() => S.Object(new() { { "authorizationId", S.Number(1) }, { "authorizedAmount", Money(true) }, { "recordedAmount", Money() }, { "remainingAmount", Money() }, { "version", S.Number(0, long.MaxValue - 1) } });
    internal static BsonDocument Funding() => S.Object(new() { { "request", ProcurementFundingSchemas.Validator(F.Requests)["$and"][0]["$jsonSchema"].DeepClone() },
        { "decision", ProcurementFundingSchemas.Validator(F.Decisions)["$and"][0]["$jsonSchema"].DeepClone() }, { "authorization", ProcurementFundingSchemas.Validator(F.Allocations)["$and"][0]["$jsonSchema"].DeepClone() } });
    internal static BsonDocument Beneficiary() => S.Object(new() { { "beneficiaryId", S.Number(1) }, { "versionId", S.Number(1) }, { "version", S.Number(1) }, { "versionHash", Hash },
        { "owner", SupplierSchemas.Owner() }, { "supplierId", S.Number(1) }, { "supplier", SupplierBankSchemas.Supplier() }, { "details", SupplierBankSchemas.Details() }, { "physicalIdentity", Hash }, { "policy", SupplierBankSchemas.Policy() },
        { "proposalId", S.Number(1) }, { "proposalHash", Hash }, { "observationId", S.Number(1) }, { "observationHash", Hash }, { "decisionId", S.Number(1) }, { "decisionHash", Hash }, { "effectiveAt", Date } });
    internal static BsonDocument AccountView() => S.Object(new() { { "id", S.Number(1) }, { "versionId", S.Number(1) }, { "version", S.Number(1) }, { "bankCode", S.Text(32) }, { "bankName", S.Text(160) },
        { "maskedAccount", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A(?:\*{4}|\*{1,30}[0-9]{4})\z" } } }, { "currency", S.Choice("IDR") } });
    internal static BsonDocument Source() => S.Object(new() { { "funding", Funding() }, { "beneficiary", Beneficiary() }, { "payingBank", RefundPayoutSchemas.PayingBank() }, { "payingAccount", AccountView() }, { "policy", Policy() } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies => [new("_id", 1)], B.Drafts => [new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Records => [new("draftId", 1), new("pinId", 1), new("allocationId", 1), new("reservationId", 1), new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Heads => [new("authorizationId", 1)], B.Allocations => [new("disbursementId", 1), new() { { "authorizationId", 1 }, { "sequence", 1 } }], _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        if (!Collections.Contains(name)) throw new ArgumentOutOfRangeException(nameof(name)); var fields = Header(); var checks = new BsonArray(); var dates = new List<string>();
        if (name == B.Policies) { fields = PolicyFields(); fields.Add("fence", S.Number(0, long.MaxValue - 1)); checks.Add(Gt("$expiresAt", "$approvedAt")); dates.AddRange(["approvedAt", "expiresAt"]); }
        else if (name is B.Heads or B.Allocations) {
            fields.AddRange(new BsonDocument { { "authorizationId", S.Number(1) }, { "authorizationHash", Hash } });
            if (name == B.Heads) {
                fields.AddRange(new BsonDocument { { "authorizedAmount", Money(true) }, { "recordedAmount", Money(true) }, { "version", S.Number(1, long.MaxValue - 1) }, { "fence", S.Number(0, long.MaxValue - 1) }, { "createdAt", Date }, { "updatedAt", Date } });
                checks.Add(new BsonDocument("$lte", new BsonArray { Amount("$recordedAmount"), Amount("$authorizedAmount") })); checks.Add(new BsonDocument("$gte", new BsonArray { "$updatedAt", "$createdAt" })); dates.AddRange(["createdAt", "updatedAt"]);
            } else {
                fields.AddRange(new BsonDocument { { "disbursementId", S.Number(1) }, { "disbursementHash", Hash }, { "amount", Money(true) }, { "sequence", S.Number(1, long.MaxValue - 1) },
                    { "previousAllocationId", Nullable(S.Number(1)) }, { "previousHash", Nullable(Hash) }, { "recordedAt", Date }, { "snapshotHash", Hash } });
                foreach (var field in new[] { "previousAllocationId", "previousHash" }) checks.Add(Eq(Eq("$sequence", 1L), Eq("$" + field, BsonNull.Value))); dates.Add("recordedAt");
            }
        } else {
            fields.AddRange(new BsonDocument { { "draftId", S.Number(1) }, { "version", S.Number(1, 1) }, { "authorizationId", S.Number(1) }, { "location", ProcurementRequestSchemas.Location() },
                { "input", Input() }, { "source", Source() }, { "capacity", Capacity() }, { "binding", Hash }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
                { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 2 }, { "maxItems", 2 }, { "items", Person } } },
                { "recordedAt", Date }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash } }); S.Metadata(fields); dates.Add("recordedAt");
            if (name == B.Drafts) {
                fields.AddRange(new BsonDocument { { "operation", S.Choice(B.DraftOperation) }, { "fence", S.Number(0, long.MaxValue - 1) }, { "recordedDisbursementId", Nullable(S.Number(1)) } }); checks.Add(Eq("$_id", "$draftId"));
            } else fields.AddRange(new BsonDocument { { "operation", S.Choice(B.RecordOperation) }, { "draftHash", Hash }, { "state", S.Choice("recorded_unverified") },
                { "reservationId", S.Number(1) }, { "evidence", PaymentSubmissionSchemas.Reference() }, { "pinId", S.Number(1) }, { "allocationId", S.Number(1) } });
            foreach (var field in new[] { "location", "authorizationId" }) checks.Add(Eq("$" + field, "$input." + field));
            checks.Add(Eq("$source.funding.authorization._id", "$authorizationId")); checks.Add(Eq("$source.funding.request._id", "$input.fundingRequestId")); checks.Add(Eq("$source.funding.decision._id", "$input.fundingDecisionId")); checks.Add(Eq("$source.funding.decision.decision", "approve"));
            checks.Add(Eq("$source.policy._id", "$input.policyId")); checks.Add(Eq("$source.policy.revision", "$input.policyRevision")); checks.Add(Eq("$source.policy.location", "$location"));
            checks.Add(Eq("$source.policy.payingAccountId", "$input.payingAccountId")); checks.Add(Eq("$source.policy.payingAccountVersionId", "$input.payingAccountVersionId"));
            checks.Add(Eq("$source.beneficiary.supplierId", "$input.supplierId")); checks.Add(Eq("$source.beneficiary.versionId", "$input.beneficiaryVersionId")); checks.Add(Eq("$source.beneficiary.version", "$input.beneficiaryVersion"));
            checks.Add(Eq("$capacity.authorizationId", "$authorizationId")); checks.Add(Eq("$capacity.authorizedAmount", "$source.funding.decision.authorizedAmount"));
            checks.Add(Eq(Amount("$capacity.authorizedAmount"), new BsonDocument("$add", new BsonArray { Amount("$capacity.recordedAmount"), Amount("$capacity.remainingAmount") })));
            checks.Add(Eq(Eq("$capacity.version", 0L), Eq(Amount("$capacity.recordedAmount"), new BsonDecimal128(0m))));
            checks.Add(new BsonDocument("$lte", new BsonArray { Amount("$input.amount"), Amount("$capacity.remainingAmount") }));
            checks.Add(new BsonDocument("$or", new BsonArray { "$source.policy.allowPartialRecordings", new BsonDocument("$and", new BsonArray { Eq(Amount("$capacity.recordedAmount"), new BsonDecimal128(0m)), Eq("$input.amount", "$capacity.authorizedAmount") }) }));
            checks.Add(Eq("$source.policy.active", true)); checks.Add(new BsonDocument("$lte", new BsonArray { "$source.policy.approvedAt", "$recordedAt" })); checks.Add(Gt("$source.policy.expiresAt", "$recordedAt"));
            checks.Add(new BsonDocument("$lte", new BsonArray { "$source.funding.decision.recordedAt", "$recordedAt" })); checks.Add(new BsonDocument("$lte", new BsonArray { "$source.beneficiary.effectiveAt", "$recordedAt" }));
            checks.Add(new BsonDocument("$ne", new BsonArray { new BsonDocument("$dateFromString", new BsonDocument { { "dateString", "$input.transferDate" }, { "format", "%Y-%m-%d" }, { "onError", BsonNull.Value }, { "onNull", BsonNull.Value } }), BsonNull.Value }));
            dates.AddRange(["source.policy.approvedAt", "source.policy.expiresAt"]);
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) }; if (checks.Count > 0) terms.Add(new BsonDocument("$expr", new BsonDocument("$and", checks)));
        foreach (var date in dates) terms.Add(S.DateRange(date)); return new("$and", terms);
    }
    private static BsonDocument Eq(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
    private static BsonDocument Gt(BsonValue left, BsonValue right) => new("$gt", new BsonArray { left, right });
    private static BsonDocument Amount(string path) => new("$convert", new BsonDocument { { "input", path }, { "to", "decimal" }, { "onError", new BsonDecimal128(-1m) }, { "onNull", new BsonDecimal128(-1m) } });
}
