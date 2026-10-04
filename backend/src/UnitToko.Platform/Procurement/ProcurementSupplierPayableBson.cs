using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementSupplierPayableBson
{
    internal const string Policies = "procurementSupplierPayablePolicies", Proposals = "procurementSupplierPayableProposals",
        Decisions = "procurementSupplierPayableDecisions", Payables = "procurementSupplierPayables",
        Applications = "procurementSupplierPayableApplications", PayableHeads = "procurementSupplierPayableHeads",
        AdvanceHeads = "procurementSupplierAdvanceHeads";
    internal const string ProposeOperation = "procurement.payable.propose", DecideOperation = "procurement.payable.decide";
    internal static readonly string[] Collections = [Policies, Proposals, Decisions, Payables, Applications, PayableHeads, AdvanceHeads];
    internal static string Kind(ProcurementSupplierPayableKind value) => value == ProcurementSupplierPayableKind.Recognize ? "recognize" : "apply_advance";
    internal static ProcurementSupplierPayableKind Kind(string value) => value switch {
        "recognize" => ProcurementSupplierPayableKind.Recognize, "apply_advance" => ProcurementSupplierPayableKind.ApplyAdvance, _ => throw Invalid() };
    internal static string Decision(ProcurementSupplierPayableDecision value) => value == ProcurementSupplierPayableDecision.Approve ? "approve" : "reject";
    internal static string Hash(BsonDocument value) => BankReceiptBson.Hash(value);
    internal static BsonDocument Snapshot(BsonDocument value) => BankReceiptBson.Snapshot(value);
    internal static BsonDocument PolicySnapshot(BsonDocument value) { var copy = value.DeepClone().AsBsonDocument; copy.Remove("fence"); return copy; }
    internal static BsonDocument CapacitySnapshot(BsonDocument value) { var copy = value.DeepClone().AsBsonDocument;
        copy.Remove("payableHead"); copy.Remove("advanceHead"); return copy; }
    internal static BsonDocument Input(ProcurementSupplierPayableInput value)
    {
        value = ProcurementSupplierPayableRules.Copy(value);
        return new() { { "kind", Kind(value.Kind) }, { "location", R.Location(value.Location) },
            { "valuationAllocationId", value.ValuationAllocationId is { } valuation ? new BsonInt64(valuation) : BsonNull.Value },
            { "payableId", value.PayableId is { } payable ? new BsonInt64(payable) : BsonNull.Value },
            { "settlementId", value.SettlementId is { } settlement ? new BsonInt64(settlement) : BsonNull.Value },
            { "amount", value.Amount is { } amount ? new BsonString(amount.ToString()) : BsonNull.Value },
            { "policyId", value.PolicyId }, { "policyRevision", value.PolicyRevision },
            { "postingDate", InvoiceBson.Date(value.PostingDate) }, { "reason", value.Reason } };
    }
    internal static ProcurementSupplierPayableInput Input(BsonValue value) => ProcurementSupplierPayableRules.Copy(new(
        Kind(value["kind"].AsString), R.Location(value["location"]), value["valuationAllocationId"].IsBsonNull ? null : value["valuationAllocationId"].AsInt64,
        value["payableId"].IsBsonNull ? null : value["payableId"].AsInt64,
        value["settlementId"].IsBsonNull ? null : value["settlementId"].AsInt64,
        value["amount"].IsBsonNull ? null : Money.Parse(value["amount"].AsString),
        value["policyId"].AsInt64, value["policyRevision"].AsInt64, InvoiceBson.Date(value["postingDate"]), value["reason"].AsString));
    internal static object ProposeBody(ProcurementSupplierPayableInput input, string binding) => new { input = ProcurementSupplierPayableRules.Copy(input), binding };
    internal static object DecideBody(long proposalId, long revision, ProcurementSupplierPayableDecision decision, string binding) =>
        new { proposalId, revision, decision = Decision(decision), binding };
    internal static string Binding(long actor, long grantVersion, BsonDocument input, BsonDocument source,
        BsonDocument policy, BsonDocument accounting, BsonDocument capacity) => Hash(new() {
            { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "input", input }, { "source", source },
            { "policy", PolicySnapshot(policy) }, { "accounting", accounting }, { "capacity", CapacitySnapshot(capacity) } });
    internal static string DecisionBinding(long actor, long grantVersion, BsonDocument proposal, ProcurementSupplierPayableDecision decision,
        BsonDocument source, BsonDocument policy, BsonDocument accounting, BsonDocument capacity) => Hash(new() {
            { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "proposalHash", proposal["snapshotHash"] },
            { "decision", Decision(decision) }, { "source", source }, { "policy", PolicySnapshot(policy) }, { "accounting", accounting }, { "capacity", CapacitySnapshot(capacity) } });
    internal static BsonDocument Result(BsonDocument row) => row["operation"] == ProposeOperation ? new() {
        { "proposalId", row["_id"] }, { "version", 1L }, { "status", "proposed" }, { "recordedAt", row["recordedAt"] }
    } : new() { { "proposalId", row["proposalId"] }, { "decisionId", row["_id"] }, { "version", 1L },
        { "status", row["decision"] == "approve" ? "approved" : "rejected" },
        { "payableId", row["payableId"] }, { "applicationId", row["applicationId"] }, { "journalId", row["journalId"] },
        { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 },
        { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "location", row["location"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
        { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static BsonDocument Outbox(BsonDocument row) => new() { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
        { "kind", row["operation"] }, { "sourceKey", SourceKey(row) }, { "actorId", row["actorId"] },
        { "requestKey", row["requestKey"] }, { "location", row["location"] }, { "payload", Result(row) },
        { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } };
    internal static string SourceKey(BsonDocument row) => row["operation"].AsString + ":" + row["_id"].AsInt64;
    internal static void Metadata(BsonDocument row, string operation, long auditId, UnitToko.Modules.Identity.Contracts.ActorContext actor,
        AuthorizationCommandMetadata metadata, object body, DateTimeOffset at)
    {
        row.Add("operation", operation).Add("actorId", actor.UserId).Add("actorGrantVersion", actor.GrantVersion)
            .Add("recordedAt", at.UtcDateTime).Add("auditId", auditId).Add("reason", metadata.Reason)
            .Add("correlationId", metadata.CorrelationId).Add("requestKey", metadata.IdempotencyKey)
            .Add("bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason));
        row.Add("snapshotHash", Hash(row));
    }
    internal static BsonDocument EmptyPayable(long payableId, BsonDocument payable) => new() { { "payableId", payableId },
        { "version", 0L }, { "totalAmount", payable["amount"] }, { "appliedAmount", "0.00" },
        { "remainingAmount", payable["amount"] }, { "lastApplicationId", BsonNull.Value }, { "lastHash", BsonNull.Value } };
    internal static BsonDocument EmptyAdvance(long settlementId, BsonDocument settlement) => new() { { "settlementId", settlementId },
        { "version", 0L }, { "totalAmount", settlement["amount"] }, { "appliedAmount", "0.00" },
        { "remainingAmount", settlement["amount"] }, { "lastApplicationId", BsonNull.Value }, { "lastHash", BsonNull.Value } };
    internal static BsonDocument After(BsonDocument before, Money amount, long applicationId, string hash)
    {
        var remaining = Money.Parse(before["remainingAmount"].AsString);
        if (amount.Value <= 0 || amount.Value > remaining.Value || before["version"].AsInt64 >= long.MaxValue - 1) throw Changed("procurement_supplier_payable_capacity_exceeded");
        var result = before.DeepClone().AsBsonDocument;
        result["version"] = before["version"].AsInt64 + 1;
        result["appliedAmount"] = Money.Add(Money.Parse(before["appliedAmount"].AsString), amount).ToString();
        result["remainingAmount"] = new Money(remaining.Value - amount.Value).ToString();
        if (before.Contains("stateVersion")) {
            if (before["stateVersion"] != 2) throw Broken();
            result["lastEffectKind"] = "apply_advance"; result["lastEffectId"] = applicationId;
        } else result["lastApplicationId"] = applicationId;
        result["lastHash"] = hash; return result;
    }
    internal static BsonDocument AfterReturn(BsonDocument before, Money amount, long returnLineId, string hash)
    {
        var remaining = Money.Parse(before["remainingAmount"].AsString);
        if (amount.Value <= 0 || amount.Value > remaining.Value || before["version"].AsInt64 >= long.MaxValue - 1)
            throw Changed("procurement_advance_return_capacity_exceeded");
        var returned = before.Contains("stateVersion") ? Money.Parse(before["returnedAmount"].AsString) : new Money(0m);
        var result = new BsonDocument { { "stateVersion", 2 }, { "settlementId", before["settlementId"] },
            { "version", before["version"].AsInt64 + 1 }, { "totalAmount", before["totalAmount"] },
            { "appliedAmount", before["appliedAmount"] }, { "returnedAmount", Money.Add(returned, amount).ToString() },
            { "remainingAmount", new Money(remaining.Value - amount.Value).ToString() },
            { "lastEffectKind", "return" }, { "lastEffectId", returnLineId }, { "lastHash", hash } };
        if (Money.Add(Money.Add(Money.Parse(result["appliedAmount"].AsString), Money.Parse(result["returnedAmount"].AsString)),
            Money.Parse(result["remainingAmount"].AsString)) != Money.Parse(result["totalAmount"].AsString)) throw Broken();
        return result;
    }
    internal static PersistenceConflictException Changed(string code = "procurement_supplier_payable_changed") =>
        new(code, "Reload the approved valuation, payable, verified advance and Finance policy");
    internal static UnitToko.Modules.Contracts.DomainValidationException Invalid() => ProcurementSupplierPayableRules.Invalid();
    internal static AuthorizationDeniedException Broken() => new("procurement_supplier_payable_integrity_unavailable");
}
