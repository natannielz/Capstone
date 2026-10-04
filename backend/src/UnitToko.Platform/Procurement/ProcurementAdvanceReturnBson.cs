using MongoDB.Bson;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementAdvanceReturnBson
{
    internal const string Policies = "procurementAdvanceReturnPolicies", Proposals = "procurementAdvanceReturnProposals",
        Decisions = "procurementAdvanceReturnDecisions", Lines = "procurementAdvanceReturnLines";
    internal const string ProposeOperation = "procurement.advancereturn.propose", DecideOperation = "procurement.advancereturn.decide";
    internal static readonly string[] Collections = [Policies, Proposals, Decisions, Lines];
    internal static string Hash(BsonDocument row) => BankReceiptBson.Hash(row);
    internal static BsonDocument PolicySnapshot(BsonDocument row) { var result = row.DeepClone().AsBsonDocument; result.Remove("fence"); return result; }
    internal static string Decision(ProcurementAdvanceReturnDecision value) => value == ProcurementAdvanceReturnDecision.Approve ? "approve" : "reject";
    internal static BsonDocument Input(ProcurementAdvanceReturnInput value)
    {
        value = ProcurementAdvanceReturnRules.Copy(value);
        return new() { { "location", R.Location(value.Location) }, { "receiptId", value.ReceiptId },
            { "policyId", value.PolicyId }, { "policyRevision", value.PolicyRevision },
            { "postingDate", InvoiceBson.Date(value.PostingDate) }, { "reason", value.Reason },
            { "lines", new BsonArray(value.Lines.Select(line => new BsonDocument { { "settlementId", line.SettlementId }, { "amount", line.Amount.ToString() } })) } };
    }
    internal static ProcurementAdvanceReturnInput Input(BsonValue value) => ProcurementAdvanceReturnRules.Copy(new(
        R.Location(value["location"]), value["receiptId"].AsInt64, value["policyId"].AsInt64,
        value["policyRevision"].AsInt64, InvoiceBson.Date(value["postingDate"]), value["reason"].AsString,
        [.. value["lines"].AsBsonArray.Select(line => new ProcurementAdvanceReturnLineInput(line["settlementId"].AsInt64,
            UnitToko.Modules.Accounting.Money.Parse(line["amount"].AsString)))]));
    internal static object ProposeBody(ProcurementAdvanceReturnInput input, string binding) => new { input = ProcurementAdvanceReturnRules.Copy(input), binding };
    internal static object DecideBody(long proposalId, long revision, ProcurementAdvanceReturnDecision decision, string binding) =>
        new { proposalId, revision, decision = Decision(decision), binding };
    internal static string Binding(long actor, long grantVersion, BsonDocument input, BsonDocument source, BsonDocument policy,
        BsonDocument accounting, BsonDocument capacity) => Hash(new() { { "actorId", actor }, { "actorGrantVersion", grantVersion },
            { "input", input }, { "source", source }, { "policy", PolicySnapshot(policy) }, { "accounting", accounting }, { "capacity", capacity } });
    internal static string DecisionBinding(long actor, long grantVersion, BsonDocument proposal, ProcurementAdvanceReturnDecision decision,
        BsonDocument source, BsonDocument policy, BsonDocument accounting, BsonDocument capacity) => Hash(new() {
            { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "proposalHash", proposal["snapshotHash"] },
            { "decision", Decision(decision) }, { "source", source }, { "policy", PolicySnapshot(policy) },
            { "accounting", accounting }, { "capacity", capacity } });
    internal static BsonDocument Result(BsonDocument row) => row["operation"] == ProposeOperation ? new() {
        { "proposalId", row["_id"] }, { "version", 1L }, { "status", "proposed" }, { "recordedAt", row["recordedAt"] }
    } : new() { { "proposalId", row["proposalId"] }, { "decisionId", row["_id"] }, { "version", 1L },
        { "status", row["decision"] == "approve" ? "returned" : "rejected" }, { "receiptId", row["receiptId"] },
        { "returnLineIds", row["returnLineIds"] }, { "journalId", row["journalId"] }, { "recordedAt", row["recordedAt"] } };
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
    internal static void Metadata(BsonDocument row, string operation, long auditId,
        UnitToko.Modules.Identity.Contracts.ActorContext actor, AuthorizationCommandMetadata metadata, object body, DateTimeOffset at)
    {
        row.Add("operation", operation).Add("actorId", actor.UserId).Add("actorGrantVersion", actor.GrantVersion)
            .Add("recordedAt", at.UtcDateTime).Add("auditId", auditId).Add("reason", metadata.Reason)
            .Add("correlationId", metadata.CorrelationId).Add("requestKey", metadata.IdempotencyKey)
            .Add("bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason));
        row.Add("snapshotHash", Hash(row));
    }
    internal static UnitToko.Modules.Contracts.DomainValidationException Invalid() => ProcurementAdvanceReturnRules.Invalid();
    internal static UnitToko.Platform.Mongo.PersistenceConflictException Changed(string code = "procurement_advance_return_changed") =>
        new(code, "Reload the verified credit, remaining advances and approved Finance policy");
    internal static UnitToko.Platform.Authorization.AuthorizationDeniedException Broken() => new("procurement_advance_return_integrity_unavailable");
}
