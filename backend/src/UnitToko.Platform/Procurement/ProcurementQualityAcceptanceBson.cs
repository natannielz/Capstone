using MongoDB.Bson;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementQualityAcceptanceBson
{
    internal const string Policies = "procurementQualityAcceptancePolicies", Proposals = "procurementQualityAcceptanceProposals",
        Decisions = "procurementQualityAcceptanceDecisions", Allocations = "procurementQualityAcceptanceAllocations",
        ReceiptHeads = "procurementQualityAcceptanceReceiptHeads";
    internal const string ProposeOperation = "procurement.qualityacceptance.propose", DecideOperation = "procurement.qualityacceptance.decide";
    internal static string Hash(BsonDocument row) => R.Hash(row);
    internal static BsonDocument Input(ProcurementQualityAcceptanceInput value) {
        value = ProcurementQualityAcceptanceRules.Copy(value);
        return new() { { "location", R.Location(value.Location) }, { "receiptId", value.ReceiptId }, { "receiptRevision", value.ReceiptRevision },
            { "acceptedPackages", value.AcceptedPackages }, { "policyId", value.PolicyId }, { "policyRevision", value.PolicyRevision },
            { "observedAt", value.ObservedAt.UtcDateTime }, { "qualityReference", value.QualityReference },
            { "putawayReference", value.PutawayReference }, { "segregationReference", value.SegregationReference },
            { "acceptedCondition", value.AcceptedCondition }, { "lotReference", value.LotReference },
            { "expiresOn", value.ExpiresOn is { } day ? InvoiceBson.Date(day) : BsonNull.Value },
            { "nonExpiringReason", value.NonExpiringReason is { } reason ? reason : BsonNull.Value }, { "reason", value.Reason } };
    }
    internal static ProcurementQualityAcceptanceInput Input(BsonValue row) => ProcurementQualityAcceptanceRules.Copy(new ProcurementQualityAcceptanceInput(
        R.Location(row["location"]), row["receiptId"].AsInt64, row["receiptRevision"].AsInt64,
        row["acceptedPackages"].AsInt32, row["policyId"].AsInt64, row["policyRevision"].AsInt64,
        new DateTimeOffset(row["observedAt"].ToUniversalTime()), row["qualityReference"].AsString,
        row["putawayReference"].AsString, row["segregationReference"].AsString, row["acceptedCondition"].AsString,
        row["lotReference"].AsString, row["expiresOn"].IsBsonNull ? null : InvoiceBson.Date(row["expiresOn"]),
        row["nonExpiringReason"].IsBsonNull ? null : row["nonExpiringReason"].AsString, row["reason"].AsString));
    internal static BsonDocument Observation(ProcurementQualityAcceptanceObservation value) {
        value = ProcurementQualityAcceptanceRules.Copy(value);
        return new() { { "observedAt", value.ObservedAt.UtcDateTime }, { "acceptedPackages", value.AcceptedPackages },
            { "qualityReference", value.QualityReference }, { "putawayReference", value.PutawayReference },
            { "segregationReference", value.SegregationReference }, { "acceptedCondition", value.AcceptedCondition },
            { "lotReference", value.LotReference }, { "expiresOn", value.ExpiresOn is { } day ? InvoiceBson.Date(day) : BsonNull.Value },
            { "nonExpiringReason", value.NonExpiringReason is { } reason ? reason : BsonNull.Value } };
    }
    internal static ProcurementQualityAcceptanceObservation Observation(BsonValue row) => ProcurementQualityAcceptanceRules.Copy(new ProcurementQualityAcceptanceObservation(
        new DateTimeOffset(row["observedAt"].ToUniversalTime()), row["acceptedPackages"].AsInt32, row["qualityReference"].AsString,
        row["putawayReference"].AsString, row["segregationReference"].AsString, row["acceptedCondition"].AsString,
        row["lotReference"].AsString, row["expiresOn"].IsBsonNull ? null : InvoiceBson.Date(row["expiresOn"]),
        row["nonExpiringReason"].IsBsonNull ? null : row["nonExpiringReason"].AsString));
    internal static string Decision(ProcurementQualityAcceptanceDecision value) => value == ProcurementQualityAcceptanceDecision.Approve ? "approve" : "reject";
    internal static object ProposeBody(ProcurementQualityAcceptanceInput input, string binding) => new { input = ProcurementQualityAcceptanceRules.Copy(input), binding };
    internal static object DecideBody(long proposalId, long version, ProcurementQualityAcceptanceDecision decision,
        ProcurementQualityAcceptanceObservation? observation, string binding) =>
        new { proposalId, version, decision = Decision(decision), observation = observation is null ? null : ProcurementQualityAcceptanceRules.Copy(observation), binding };
    internal static BsonDocument PolicySnapshot(BsonDocument policy) { var copy = policy.DeepClone().AsBsonDocument; copy.Remove("fence"); return copy; }
    internal static string SourceConfigurationHash(BsonDocument source) { var copy = source.DeepClone().AsBsonDocument; copy.Remove("poolFence"); return Hash(copy); }
    private static BsonDocument PoolIdentity(BsonDocument pool) { var copy = pool.DeepClone().AsBsonDocument; copy.Remove("fence"); return copy; }
    internal static string Binding(long actor, long grant, ProcurementQualityAcceptanceInput input, BsonDocument receipt,
        BsonDocument pool, string sourceConfigurationHash, BsonDocument policy, BsonDocument capacity) => Hash(new() { { "actorId", actor }, { "actorGrantVersion", grant },
        { "input", Input(input) }, { "receiptHash", receipt["snapshotHash"] }, { "pool", PoolIdentity(pool) },
        { "sourceConfigurationHash", sourceConfigurationHash },
        { "policy", PolicySnapshot(policy) }, { "capacity", capacity } });
    internal static string DecisionBinding(long actor, long grant, BsonDocument proposal, ProcurementQualityAcceptanceDecision decision,
        ProcurementQualityAcceptanceObservation? observation, BsonDocument pool, string sourceConfigurationHash,
        BsonDocument policy, BsonDocument capacity) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grant }, { "proposalHash", proposal["snapshotHash"] },
        { "decision", Decision(decision) }, { "observation", observation is null ? BsonNull.Value : Observation(observation) },
        { "pool", PoolIdentity(pool) }, { "sourceConfigurationHash", sourceConfigurationHash },
        { "policy", PolicySnapshot(policy) }, { "capacity", capacity } });
    internal static void Metadata(BsonDocument row, string operation, long auditId, ActorContext actor,
        AuthorizationCommandMetadata metadata, object body, DateTimeOffset now) => R.Metadata(row, operation, auditId, actor, metadata, body, now);
    internal static BsonDocument Result(BsonDocument row) => row["operation"] == ProposeOperation ? new() {
        { "proposalId", row["_id"] }, { "decisionId", BsonNull.Value }, { "allocationId", BsonNull.Value },
        { "version", 1L }, { "status", "proposed" }, { "recordedAt", row["recordedAt"] } } : new() {
        { "proposalId", row["proposalId"] }, { "decisionId", row["_id"] }, { "allocationId", row["allocationId"] },
        { "version", 1L }, { "status", row["decision"] == "approve" ? "approved_held" : "rejected" }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 },
        { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "location", row["location"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
        { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => row["operation"].AsString + ":" + row["_id"].AsInt64;
    internal static PersistenceConflictException Changed(string code = "procurement_quality_acceptance_changed") =>
        new(code, "Reload the held receipt, explicit quality evidence, approved policy and accepted capacity");
    internal static AuthorizationDeniedException Broken() => new("procurement_quality_acceptance_integrity_unavailable");
}
