using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ProcurementQualityAcceptanceSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Proposals, B.Decisions, B.Allocations, B.ReceiptHeads];
    private static BsonDocument Long(long minimum = 0) => S.Number(minimum, long.MaxValue - 2);
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Int => new() { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } };
    private static BsonDocument Day => new() { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Header() => new() { { "_id", Long(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
    private static BsonDocument People(int minimum, int maximum) => new() { { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", maximum }, { "items", S.Object(new() {
        { "userId", Long(1) }, { "personId", Long(1) }, { "verification", S.Choice("Verified") }, { "version", Long(1) } }) } };
    private static BsonDocument ObservationFields() => new() {
        { "observedAt", Date }, { "acceptedPackages", Int }, { "qualityReference", S.Text(256) }, { "putawayReference", S.Text(256) },
        { "segregationReference", S.Text(256) }, { "acceptedCondition", S.Choice("sealed_undamaged", "intact_as_defined") }, { "lotReference", S.Text(128) },
        { "expiresOn", Nullable(Day) }, { "nonExpiringReason", Nullable(S.Text(1000)) } };
    internal static BsonDocument Observation() => S.Object(ObservationFields());
    internal static BsonDocument Input() {
        var fields = new BsonDocument { { "location", ProcurementRequestSchemas.Location() }, { "receiptId", Long(1) },
            { "receiptRevision", S.Number(1, 1) }, { "policyId", Long(1) }, { "policyRevision", Long(1) }, { "reason", S.Text(1000) } };
        fields.AddRange(ObservationFields()); return S.Object(fields);
    }
    private static BsonDocument PolicyFields(bool live) {
        var fields = Header(); fields.AddRange(new BsonDocument { { "revision", Long(1) }, { "active", new BsonDocument("bsonType", "bool") },
            { "location", ProcurementRequestSchemas.Location() }, { "qualityProcedureReference", S.Text(256) }, { "putawayProcedureReference", S.Text(256) },
            { "allowNonExpiring", new BsonDocument("bsonType", "bool") }, { "nonExpiringProcedureReference", Nullable(S.Text(256)) },
            { "maximumObservationAgeMilliseconds", S.Number(1, 2592000000L) }, { "approvalReference", S.Text(256) },
            { "proposedBy", Long(1) }, { "approvedBy", Long(1) }, { "people", People(2, 2) }, { "approvedAt", Date }, { "expiresAt", Date } });
        if (live) fields.Add("fence", Long()); return fields;
    }
    internal static BsonDocument Policy() => S.Object(PolicyFields(false));
    private static BsonDocument CapacityFields() => new() { { "version", Long() }, { "acceptedPackages", S.Number(0, 1000000) },
        { "remainingPackages", S.Number(0, 1000000) }, { "lastAllocationId", Nullable(Long(1)) }, { "lastHash", Nullable(Hash) } };
    internal static BsonDocument Capacity() => S.Object(CapacityFields());
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies => [new("_id", 1)],
        B.Proposals => [new() { { "actorId", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Decisions => [new("proposalId", 1), new() { { "actorId", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Allocations => [new("proposalId", 1), new("decisionId", 1), new() { { "receiptId", 1 }, { "sequence", 1 } }],
        B.ReceiptHeads => [new("receiptId", 1)], _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument[] SupportKeys(string name) => name switch {
        B.Proposals or B.Decisions => [new() { { "input.receiptId", 1 }, { "_id", 1 } }],
        B.Allocations => [new() { { "receiptId", 1 }, { "_id", 1 } }], _ => [] };
    internal static BsonDocument Validator(string name) {
        var fields = Header(); var checks = new BsonArray(); var dates = new List<string>();
        switch (name) {
            case B.Policies:
                fields = PolicyFields(true); PolicyChecks(checks, "$"); dates.AddRange(["approvedAt", "expiresAt"]); break;
            case B.ReceiptHeads:
                fields.AddRange(CapacityFields()); fields["version"] = Long(1);
                fields.AddRange(new BsonDocument { { "receiptId", Long(1) }, { "sourceHash", Hash }, { "createdAt", Date }, { "updatedAt", Date }, { "fence", Long() } });
                checks.Add(Op("$eq", "$_id", "$receiptId")); CapacityChecks(checks, "$"); checks.Add(Op("$lte", "$createdAt", "$updatedAt")); dates.AddRange(["createdAt", "updatedAt"]); break;
            case B.Proposals:
            case B.Decisions:
                var decision = name == B.Decisions;
                fields.AddRange(new BsonDocument { { "proposalId", Long(1) }, { "version", S.Number(1, 1) }, { "location", ProcurementRequestSchemas.Location() },
                    { "input", Input() }, { "receiptHash", Hash }, { "policy", Policy() }, { "binding", Hash }, { "people", People(decision ? 2 : 1, decision ? 2 : 1) },
                    { "operation", S.Choice(decision ? B.DecideOperation : B.ProposeOperation) }, { "actorId", Long(1) }, { "actorGrantVersion", Long(1) },
                    { "recordedAt", Date }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash } });
                S.Metadata(fields); PolicyChecks(checks, "$policy."); ObservationChecks(checks, "$input."); dates.AddRange(["recordedAt", "input.observedAt"]);
                foreach (var (a, b) in new[] { ("location", "input.location"), ("location", "policy.location"), ("input.policyId", "policy._id"), ("input.policyRevision", "policy.revision") }) checks.Add(Op("$eq", "$" + a, "$" + b));
                checks.Add(Op("$lte", "$input.observedAt", "$recordedAt"));
                if (!decision) {
                    fields.AddRange(new BsonDocument { { "inspectionHash", Hash }, { "lotId", Long(1) }, { "balanceId", Long(1) }, { "poolId", Long(1) },
                        { "poolSource", ProcurementStockReceiptSchemas.PoolSource() }, { "sourceConfigurationHash", Hash },
                        { "pool", S.Validator("inventoryPools")["$jsonSchema"].DeepClone() },
                        { "heldBalance", InventorySchemas.BalanceValidator()["$and"][0]["$jsonSchema"].DeepClone() }, { "capacity", Capacity() } });
                    checks.Add(Op("$eq", "$_id", "$proposalId")); checks.Add(Op("$eq", "$reason", "$input.reason")); CapacityChecks(checks, "$capacity.");
                    checks.Add(Op("$lte", "$input.acceptedPackages", "$capacity.remainingPackages"));
                    foreach (var (a, b) in new[] { ("lotId", "heldBalance.lotId"), ("balanceId", "heldBalance._id"), ("poolId", "pool._id"), ("poolId", "heldBalance.physicalPoolId"), ("heldBalance.held", "heldBalance.onHand") }) checks.Add(Op("$eq", "$" + a, "$" + b));
                    checks.Add(Op("$eq", "$heldBalance.reserved", 0L)); checks.Add(Op("$eq", "$heldBalance.expired", 0L));
                    checks.Add(Op("$eq", "$heldBalance.held", Op("$add", "$capacity.acceptedPackages", "$capacity.remainingPackages")));
                } else {
                    fields.AddRange(new BsonDocument { { "proposalHash", Hash }, { "decision", S.Choice("approve", "reject") }, { "observation", Nullable(Observation()) },
                        { "capacityBefore", Capacity() }, { "capacityAfter", Capacity() }, { "allocationId", Nullable(Long(1)) } });
                    CapacityChecks(checks, "$capacityBefore."); CapacityChecks(checks, "$capacityAfter.");
                    var accepted = new BsonArray { Op("$ne", "$observation", BsonNull.Value), Op("$ne", "$allocationId", BsonNull.Value),
                        Op("$lte", "$observation.observedAt", "$recordedAt"), Op("$eq", "$capacityAfter.version", Op("$add", "$capacityBefore.version", 1L)),
                        Op("$eq", "$capacityAfter.acceptedPackages", Op("$add", "$capacityBefore.acceptedPackages", "$input.acceptedPackages")),
                        Op("$eq", "$capacityAfter.remainingPackages", Op("$subtract", "$capacityBefore.remainingPackages", "$input.acceptedPackages")),
                        Op("$eq", "$capacityAfter.lastAllocationId", "$allocationId"), Op("$eq", "$capacityAfter.lastHash", "$proposalHash") };
                    foreach (var field in new[] { "acceptedPackages", "putawayReference", "segregationReference", "acceptedCondition", "lotReference", "expiresOn", "nonExpiringReason" }) accepted.Add(Op("$eq", "$observation." + field, "$input." + field));
                    checks.Add(new BsonDocument("$cond", new BsonArray { Op("$eq", "$decision", "approve"), new BsonDocument("$and", accepted),
                        Op("$and", Op("$eq", "$observation", BsonNull.Value), Op("$eq", "$allocationId", BsonNull.Value), Op("$eq", "$capacityBefore", "$capacityAfter")) }));
                }
                break;
            case B.Allocations:
                fields.AddRange(new BsonDocument { { "proposalId", Long(1) }, { "decisionId", Long(1) }, { "proposalHash", Hash }, { "receiptId", Long(1) },
                    { "receiptHash", Hash }, { "packages", Int }, { "sequence", Long(1) }, { "capacityBefore", Capacity() }, { "capacityAfter", Capacity() }, { "recordedAt", Date }, { "snapshotHash", Hash } });
                CapacityChecks(checks, "$capacityBefore."); CapacityChecks(checks, "$capacityAfter."); dates.Add("recordedAt");
                checks.Add(Op("$eq", "$sequence", "$capacityAfter.version")); checks.Add(Op("$eq", "$capacityAfter.version", Op("$add", "$capacityBefore.version", 1L)));
                checks.Add(Op("$eq", "$capacityAfter.acceptedPackages", Op("$add", "$capacityBefore.acceptedPackages", "$packages")));
                checks.Add(Op("$eq", "$capacityAfter.remainingPackages", Op("$subtract", "$capacityBefore.remainingPackages", "$packages")));
                checks.Add(Op("$eq", "$capacityAfter.lastAllocationId", "$_id")); checks.Add(Op("$eq", "$capacityAfter.lastHash", "$proposalHash")); break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), new BsonDocument("$expr", new BsonDocument("$and", checks)) };
        foreach (var date in dates) terms.Add(S.DateRange(date)); return new("$and", terms);
    }
    private static void ObservationChecks(BsonArray checks, string prefix) => checks.Add(Op("$eq", Op("$eq", prefix + "expiresOn", BsonNull.Value), Op("$ne", prefix + "nonExpiringReason", BsonNull.Value)));
    private static void PolicyChecks(BsonArray checks, string prefix) {
        checks.Add(Op("$lt", prefix + "approvedAt", prefix + "expiresAt")); checks.Add(Op("$ne", prefix + "proposedBy", prefix + "approvedBy"));
        checks.Add(Op("$eq", prefix + "allowNonExpiring", Op("$ne", prefix + "nonExpiringProcedureReference", BsonNull.Value)));
    }
    private static void CapacityChecks(BsonArray checks, string prefix) {
        checks.Add(Op("$eq", Op("$eq", prefix + "version", 0L), Op("$eq", prefix + "acceptedPackages", 0L)));
        foreach (var field in new[] { "lastAllocationId", "lastHash" }) checks.Add(Op("$eq", Op("$eq", prefix + "version", 0L), Op("$eq", prefix + field, BsonNull.Value)));
        checks.Add(Op("$lte", Op("$add", prefix + "acceptedPackages", prefix + "remainingPackages"), 1000000L));
    }
    private static BsonDocument Op(string name, params BsonValue[] values) => new(name, new BsonArray(values));
}
