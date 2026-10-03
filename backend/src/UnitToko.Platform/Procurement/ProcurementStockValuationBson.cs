using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementStockValuationBson
{
    internal const string Proposals = "procurementStockValuationProposals", Decisions = "procurementStockValuationDecisions",
        ReceiptHeads = "procurementStockValuationReceiptHeads", MatchHeads = "procurementStockValuationMatchHeads",
        Allocations = "procurementStockValuationAllocations", Policies = "procurementStockValuationPolicies";
    internal const string ProposeOperation = "procurement.stockvaluation.propose", DecideOperation = "procurement.stockvaluation.decide";
    internal static string Hash(BsonDocument row) => BankReceiptBson.Hash(row);
    internal static BsonDocument Input(ProcurementStockValuationInput value) {
        value = ProcurementStockValuationRules.Copy(value);
        return new() { { "location", R.Location(value.Location) }, { "receiptId", value.ReceiptId }, { "receiptRevision", value.ReceiptRevision },
            { "invoiceDocumentId", value.InvoiceDocumentId }, { "invoiceDocumentRevision", value.InvoiceDocumentRevision },
            { "invoiceMatchId", value.InvoiceMatchId }, { "matchLineNumber", value.MatchLineNumber }, { "packages", value.Packages },
            { "netAmount", value.NetAmount.ToString() }, { "taxAmount", value.TaxAmount.ToString() }, { "chargeAmount", value.ChargeAmount.ToString() },
            { "capitalizedAmount", value.CapitalizedAmount.ToString() }, { "policyId", value.PolicyId }, { "policyRevision", value.PolicyRevision },
            { "postingDate", InvoiceBson.Date(value.PostingDate) }, { "reason", value.Reason } };
    }
    internal static ProcurementStockValuationInput Input(BsonValue value) => ProcurementStockValuationRules.Copy(new(
        R.Location(value["location"]), value["receiptId"].AsInt64, value["receiptRevision"].AsInt64, value["invoiceDocumentId"].AsInt64,
        value["invoiceDocumentRevision"].AsInt64, value["invoiceMatchId"].AsInt64, value["matchLineNumber"].AsInt32,
        value["packages"].AsInt32, Money.Parse(value["netAmount"].AsString), Money.Parse(value["taxAmount"].AsString),
        Money.Parse(value["chargeAmount"].AsString), Money.Parse(value["capitalizedAmount"].AsString), value["policyId"].AsInt64,
        value["policyRevision"].AsInt64, InvoiceBson.Date(value["postingDate"]), value["reason"].AsString));
    internal static object ProposeBody(ProcurementStockValuationInput input, string binding) => new { input = ProcurementStockValuationRules.Copy(input), binding };
    internal static object DecideBody(long proposalId, long version, ProcurementStockValuationDecision decision, string binding) => new {
        proposalId, version, decision = Decision(decision), binding };
    internal static string Decision(ProcurementStockValuationDecision value) => value == ProcurementStockValuationDecision.Approve ? "approve" : "reject";
    internal static string Binding(long actor, long grant, ProcurementStockValuationInput input, BsonDocument receipt, BsonDocument invoice,
        BsonDocument match, BsonDocument policy, BsonDocument accounting, BsonDocument receiptCapacity, BsonDocument matchCapacity) => Hash(new() {
            { "actorId", actor }, { "actorGrantVersion", grant }, { "input", Input(input) }, { "receiptHash", receipt["snapshotHash"] },
            { "invoiceHash", invoice["snapshotHash"] }, { "matchHash", match["snapshotHash"] }, { "policy", PolicySnapshot(policy) },
            { "accounting", accounting }, { "receiptCapacity", receiptCapacity }, { "matchCapacity", matchCapacity } });
    internal static string DecisionBinding(long actor, long grant, BsonDocument proposal, ProcurementStockValuationDecision decision,
        BsonDocument receiptCapacity, BsonDocument matchCapacity, BsonDocument policy, BsonDocument accounting) => Hash(new() {
            { "actorId", actor }, { "actorGrantVersion", grant }, { "proposalHash", proposal["snapshotHash"] }, { "decision", Decision(decision) },
            { "receiptCapacity", receiptCapacity }, { "matchCapacity", matchCapacity }, { "policy", PolicySnapshot(policy) }, { "accounting", accounting } });
    internal static BsonDocument PolicySnapshot(BsonDocument policy) { var copy = policy.DeepClone().AsBsonDocument; copy.Remove("fence"); return copy; }
    internal static void Metadata(BsonDocument row, string operation, long auditId, UnitToko.Modules.Identity.Contracts.ActorContext actor,
        AuthorizationCommandMetadata metadata, object body, DateTimeOffset now) => R.Metadata(row, operation, auditId, actor, metadata, body, now);
    internal static BsonDocument Result(BsonDocument row) => row["operation"] == ProposeOperation ? new() {
        { "proposalId", row["_id"] }, { "version", 1L }, { "status", "proposed" }, { "recordedAt", row["recordedAt"] } } : new() {
        { "proposalId", row["proposalId"] }, { "decisionId", row["_id"] }, { "version", 1L },
        { "status", row["decision"] == "approve" ? "approved" : "rejected" }, { "allocationId", row["allocationId"] }, { "journalId", row["journalId"] }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 },
        { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "location", row["location"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
        { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => row["operation"].AsString + ":" + row["_id"].AsInt64;
    internal static PersistenceConflictException Changed(string code = "procurement_stock_valuation_changed") => new(code, "Reload the exact held receipt, matched invoice and approved valuation policy");
    internal static AuthorizationDeniedException Broken() => new("procurement_stock_valuation_integrity_unavailable");
}
