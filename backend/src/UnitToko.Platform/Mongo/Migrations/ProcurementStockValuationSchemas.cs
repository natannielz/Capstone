using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.ProcurementStockValuationBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ProcurementStockValuationSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Proposals, B.Decisions, B.ReceiptHeads, B.MatchHeads, B.Allocations];
    private static BsonDocument Long(long min = 0) => S.Number(min, long.MaxValue - 2);
    private static BsonDocument Int(int max = 1000000) => new() { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", max } };
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Bool => new("bsonType", "bool");
    private static BsonDocument Code => new() { { "bsonType", "string" }, { "pattern", @"\A[A-Za-z][A-Za-z0-9._-]{0,63}\z" } };
    private static BsonDocument Day => new() { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } };
    private static BsonDocument Money(bool positive = false) => new() { { "bsonType", "string" }, { "pattern", positive ? @"\A(?!0\.00\z)(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" : @"\A(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Header() => new() { { "_id", Long(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
    private static BsonDocument People(int min, int max) => new() { { "bsonType", "array" }, { "minItems", min }, { "maxItems", max }, { "items", S.Object(new() {
        { "userId", Long(1) }, { "personId", Long(1) }, { "verification", S.Choice("Verified") }, { "version", Long(1) } }) } };
    internal static BsonDocument Input() => S.Object(new() {
        { "location", ProcurementRequestSchemas.Location() }, { "receiptId", Long(1) }, { "receiptRevision", S.Number(1, 1) },
        { "invoiceDocumentId", Long(1) }, { "invoiceDocumentRevision", S.Number(1, 1) }, { "invoiceMatchId", Long(1) },
        { "matchLineNumber", Int(100) }, { "packages", Int() }, { "netAmount", Money() }, { "taxAmount", Money() }, { "chargeAmount", Money() },
        { "capitalizedAmount", Money(true) }, { "policyId", Long(1) }, { "policyRevision", Long(1) }, { "postingDate", Day }, { "reason", S.Text(1000) } });
    private static BsonDocument PolicyFields(bool live) {
        var fields = Header();
        fields.AddRange(new BsonDocument { { "revision", Long(1) }, { "active", Bool }, { "location", ProcurementRequestSchemas.Location() },
            { "currency", S.Choice("IDR") }, { "approvedAt", Date }, { "expiresAt", Date }, { "proposedBy", Long(1) }, { "approvedBy", Long(1) }, { "people", People(2, 2) },
            { "sourceRecognition", S.Choice("receipt_accrual") }, { "bookId", Long(1) }, { "chartVersionId", Long(1) },
            { "inventoryMapping", Code }, { "inventoryAccountId", Long(1) }, { "inventoryAccountCode", Code },
            { "receiptAccrualMapping", Code }, { "receiptAccrualAccountId", Long(1) }, { "receiptAccrualAccountCode", Code },
            { "capitalizeTax", Bool }, { "capitalizeCharge", Bool }, { "operatingOwnerReference", S.Text(256) }, { "approvalReference", S.Text(256) } });
        if (live) fields.Add("fence", Long()); return fields;
    }
    internal static BsonDocument Policy() => S.Object(PolicyFields(false));
    private static BsonDocument Account() => S.Object(new() { { "mappingKey", Code }, { "accountId", Long(1) }, { "accountCode", Code } });
    internal static BsonDocument Accounting() => S.Object(new() { { "bookId", Long(1) }, { "chartVersionId", Long(1) }, { "periodId", Long(1) },
        { "periodMonth", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])\z" } } },
        { "inventory", Account() }, { "receiptAccrual", Account() } });
    private static BsonDocument CapacityFields(bool match) {
        var fields = new BsonDocument { { "version", Long() }, { "allocatedPackages", S.Number(0, 1000000) }, { "remainingPackages", S.Number(0, 1000000) },
            { "lastAllocationId", Nullable(Long(1)) }, { "lastHash", Nullable(Hash) } };
        if (match) foreach (var prefix in new[] { "allocated", "remaining" }) foreach (var component in new[] { "Net", "Tax", "Charge" }) fields.Add(prefix + component + "Amount", Money());
        return fields;
    }
    internal static BsonDocument ReceiptCapacity() => S.Object(CapacityFields(false));
    internal static BsonDocument MatchCapacity() => S.Object(CapacityFields(true));
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies => [new("_id", 1)],
        B.Proposals => [new() { { "actorId", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Decisions => [new("proposalId", 1), new() { { "actorId", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.ReceiptHeads => [new("receiptId", 1)],
        B.MatchHeads => [new() { { "matchId", 1 }, { "matchLineNumber", 1 } }],
        B.Allocations => [new("proposalId", 1), new("decisionId", 1), new("journalId", 1), new() { { "receiptId", 1 }, { "receiptSequence", 1 } },
            new() { { "invoiceMatchId", 1 }, { "matchLineNumber", 1 }, { "matchSequence", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        var fields = Header(); var checks = new BsonArray(); var dates = new List<string>();
        switch (name) {
            case B.Policies:
                fields = PolicyFields(true); PolicyChecks(checks, "$"); dates.AddRange(["approvedAt", "expiresAt"]); break;
            case B.ReceiptHeads:
            case B.MatchHeads:
                var match = name == B.MatchHeads;
                if (match) fields["_id"] = new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[1-9][0-9]{0,18}:[1-9][0-9]{0,2}\z" } };
                fields.AddRange(new BsonDocument { { "sourceHash", Hash }, { "createdAt", Date }, { "updatedAt", Date }, { "fence", Long() } });
                fields.AddRange(CapacityFields(match)); fields["version"] = Long(1);
                if (match) { fields.Add("matchId", Long(1)).Add("matchLineNumber", Int(100));
                    checks.Add(Op("$eq", "$_id", Op("$concat", new BsonDocument("$toString", "$matchId"), ":", new BsonDocument("$toString", "$matchLineNumber")))); }
                else { fields.Add("receiptId", Long(1)); checks.Add(Op("$eq", "$_id", "$receiptId")); }
                CapacityChecks(checks, "$", match); checks.Add(Op("$lte", "$createdAt", "$updatedAt")); dates.AddRange(["createdAt", "updatedAt"]); break;
            case B.Proposals:
            case B.Decisions:
                var decision = name == B.Decisions;
                fields.AddRange(new BsonDocument { { "proposalId", Long(1) }, { "version", S.Number(1, 1) }, { "location", ProcurementRequestSchemas.Location() },
                    { "receiptHash", Hash }, { "invoiceHash", Hash }, { "matchHash", Hash }, { "policy", Policy() }, { "accounting", Accounting() },
                    { "binding", Hash }, { "people", People(1, 5) }, { "operation", S.Choice(decision ? B.DecideOperation : B.ProposeOperation) },
                    { "actorId", Long(1) }, { "actorGrantVersion", Long(1) }, { "recordedAt", Date }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash } });
                S.Metadata(fields); PolicyChecks(checks, "$policy."); AccountingChecks(checks); dates.Add("recordedAt");
                checks.Add(Op("$eq", "$location", "$policy.location"));
                if (!decision) {
                    fields.Add("input", Input()).Add("matchActorId", Long(1)).Add("matchLine", SupplierDocumentSchemas.ResolvedLine()).Add("receiptCapacity", ReceiptCapacity()).Add("matchCapacity", MatchCapacity());
                    foreach (var (left, right) in new[] { ("_id", "proposalId"), ("location", "input.location"), ("reason", "input.reason"), ("policy._id", "input.policyId"), ("policy.revision", "input.policyRevision") }) checks.Add(Op("$eq", "$" + left, "$" + right));
                    checks.Add(Op("$eq", "$matchLine.state", "matched")); AmountChecks(checks, "$input.");
                    checks.Add(Op("$or", "$policy.capitalizeTax", Op("$eq", "$input.taxAmount", "0.00")));
                    checks.Add(Op("$or", "$policy.capitalizeCharge", Op("$eq", "$input.chargeAmount", "0.00")));
                    checks.Add(Op("$lte", "$input.packages", "$receiptCapacity.remainingPackages")); checks.Add(Op("$lte", "$input.packages", "$matchCapacity.remainingPackages"));
                    CapacityChecks(checks, "$receiptCapacity.", false); CapacityChecks(checks, "$matchCapacity.", true);
                } else {
                    fields.AddRange(new BsonDocument { { "proposalHash", Hash }, { "input", Input() }, { "decision", S.Choice("approve", "reject") },
                        { "receiptBefore", ReceiptCapacity() }, { "receiptAfter", ReceiptCapacity() }, { "matchBefore", MatchCapacity() }, { "matchAfter", MatchCapacity() },
                        { "allocationId", Nullable(Long(1)) }, { "journalId", Nullable(Long(1)) }, { "journalFingerprint", Nullable(Hash) }, { "postingFence", S.Number(0, 1) } });
                    foreach (var prefix in new[] { "$receiptBefore.", "$receiptAfter.", "$matchBefore.", "$matchAfter." }) CapacityChecks(checks, prefix, prefix.StartsWith("$match", StringComparison.Ordinal));
                    checks.Add(Op("$eq", "$location", "$input.location")); AmountChecks(checks, "$input.");
                    checks.Add(Op("$eq", Op("$eq", "$decision", "reject"), Op("$eq", "$allocationId", BsonNull.Value)));
                    checks.Add(Op("$eq", Op("$eq", "$journalId", BsonNull.Value), Op("$eq", "$journalFingerprint", BsonNull.Value)));
                    checks.Add(new BsonDocument("$cond", new BsonArray { Op("$eq", "$decision", "reject"), Op("$and", Op("$eq", "$receiptBefore", "$receiptAfter"),
                        Op("$eq", "$matchBefore", "$matchAfter"), Op("$eq", "$journalId", BsonNull.Value), Op("$eq", "$postingFence", 0L)),
                        Op("$and", Op("$eq", "$receiptAfter.version", Op("$add", "$receiptBefore.version", 1L)), Op("$eq", "$matchAfter.version", Op("$add", "$matchBefore.version", 1L))) }));
                }
                break;
            case B.Allocations:
                fields.AddRange(new BsonDocument { { "proposalId", Long(1) }, { "decisionId", Long(1) }, { "proposalHash", Hash }, { "receiptId", Long(1) }, { "receiptHash", Hash },
                    { "invoiceDocumentId", Long(1) }, { "invoiceMatchId", Long(1) }, { "matchLineNumber", Int(100) }, { "matchHash", Hash }, { "packages", Int() },
                    { "netAmount", Money() }, { "taxAmount", Money() }, { "chargeAmount", Money() }, { "capitalizedAmount", Money(true) },
                    { "receiptSequence", Long(1) }, { "matchSequence", Long(1) }, { "receiptBefore", ReceiptCapacity() }, { "receiptAfter", ReceiptCapacity() },
                    { "matchBefore", MatchCapacity() }, { "matchAfter", MatchCapacity() }, { "journalId", Long(1) }, { "recordedAt", Date }, { "snapshotHash", Hash } });
                AmountChecks(checks, "$"); dates.Add("recordedAt");
                foreach (var prefix in new[] { "receipt", "match" }) {
                    CapacityChecks(checks, "$" + prefix + "Before.", prefix == "match"); CapacityChecks(checks, "$" + prefix + "After.", prefix == "match");
                    checks.Add(Op("$eq", "$" + prefix + "Sequence", "$" + prefix + "After.version"));
                    checks.Add(Op("$eq", "$" + prefix + "After.version", Op("$add", "$" + prefix + "Before.version", 1L)));
                    checks.Add(Op("$eq", "$" + prefix + "After.allocatedPackages", Op("$add", "$" + prefix + "Before.allocatedPackages", "$packages")));
                    checks.Add(Op("$eq", "$" + prefix + "After.remainingPackages", Op("$subtract", "$" + prefix + "Before.remainingPackages", "$packages")));
                    checks.Add(Op("$eq", "$" + prefix + "After.lastAllocationId", "$_id")); checks.Add(Op("$eq", "$" + prefix + "After.lastHash", "$proposalHash"));
                }
                foreach (var component in new[] { "Net", "Tax", "Charge" }) {
                    var amount = "$" + char.ToLowerInvariant(component[0]) + component[1..] + "Amount";
                    checks.Add(Op("$eq", Amount("$matchAfter.allocated" + component + "Amount"), Op("$add", Amount("$matchBefore.allocated" + component + "Amount"), Amount(amount))));
                    checks.Add(Op("$eq", Amount("$matchAfter.remaining" + component + "Amount"), Op("$subtract", Amount("$matchBefore.remaining" + component + "Amount"), Amount(amount))));
                }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
        var rules = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), new BsonDocument("$expr", new BsonDocument("$and", checks)) };
        foreach (var field in dates) rules.Add(S.DateRange(field)); return new("$and", rules);
    }
    private static void PolicyChecks(BsonArray checks, string prefix) {
        checks.Add(Op("$lt", prefix + "approvedAt", prefix + "expiresAt")); checks.Add(Op("$ne", prefix + "proposedBy", prefix + "approvedBy"));
        foreach (var field in new[] { "Mapping", "AccountId", "AccountCode" }) checks.Add(Op("$ne", prefix + "inventory" + field, prefix + "receiptAccrual" + field));
    }
    private static void AccountingChecks(BsonArray checks) {
        checks.Add(Op("$eq", "$accounting.bookId", "$policy.bookId")); checks.Add(Op("$eq", "$accounting.chartVersionId", "$policy.chartVersionId"));
        foreach (var role in new[] { "inventory", "receiptAccrual" }) foreach (var (field, suffix) in new[] { ("mappingKey", "Mapping"), ("accountId", "AccountId"), ("accountCode", "AccountCode") })
            checks.Add(Op("$eq", "$accounting." + role + "." + field, "$policy." + role + suffix));
    }
    private static void CapacityChecks(BsonArray checks, string prefix, bool match) {
        checks.Add(Op("$eq", Op("$eq", prefix + "version", 0L), Op("$eq", prefix + "allocatedPackages", 0L)));
        foreach (var field in new[] { "lastAllocationId", "lastHash" }) checks.Add(Op("$eq", Op("$eq", prefix + "version", 0L), Op("$eq", prefix + field, BsonNull.Value)));
        checks.Add(Op("$lte", Op("$add", prefix + "allocatedPackages", prefix + "remainingPackages"), 1000000L));
        if (match) foreach (var component in new[] { "Net", "Tax", "Charge" }) {
            checks.Add(Op("$or", Op("$ne", prefix + "version", 0L), Op("$eq", prefix + "allocated" + component + "Amount", "0.00")));
            checks.Add(Op("$or", Op("$ne", prefix + "remainingPackages", 0L), Op("$eq", prefix + "remaining" + component + "Amount", "0.00")));
        }
    }
    private static void AmountChecks(BsonArray checks, string prefix) => checks.Add(Op("$eq", Amount(prefix + "capitalizedAmount"),
        Op("$add", Amount(prefix + "netAmount"), Amount(prefix + "taxAmount"), Amount(prefix + "chargeAmount"))));
    private static BsonDocument Amount(string path) => new("$convert", new BsonDocument { { "input", path }, { "to", "decimal" }, { "onError", new BsonDecimal128(-1m) }, { "onNull", new BsonDecimal128(-1m) } });
    private static BsonDocument Op(string name, params BsonValue[] values) => new(name, new BsonArray(values));
}
