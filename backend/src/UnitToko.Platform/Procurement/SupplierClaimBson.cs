using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Mongo;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using P = UnitToko.Platform.Procurement.PurchaseOrderBson;

namespace UnitToko.Platform.Procurement;

internal static class SupplierClaimBson
{
    internal const string Requests = "supplierClaimRequests", Decisions = "supplierClaimDecisions", Prefix = "procurement.supplierclaim.";
    internal static string Basis(SupplierClaimBasis value) => value switch { SupplierClaimBasis.PhysicalReceived => "physical_received", SupplierClaimBasis.ShortageObserved => "shortage_observed", _ => throw SupplierClaimRules.Invalid() };
    internal static SupplierClaimBasis Basis(BsonValue value) => value.AsString switch { "physical_received" => SupplierClaimBasis.PhysicalReceived, "shortage_observed" => SupplierClaimBasis.ShortageObserved, _ => throw Broken() };
    internal static string Resolution(SupplierClaimResolution value) => value switch { SupplierClaimResolution.Replacement => "replacement", SupplierClaimResolution.Credit => "credit", SupplierClaimResolution.CashRefund => "cash_refund", SupplierClaimResolution.Investigation => "investigation", _ => throw SupplierClaimRules.Invalid() };
    internal static SupplierClaimResolution Resolution(BsonValue value) => value.AsString switch { "replacement" => SupplierClaimResolution.Replacement, "credit" => SupplierClaimResolution.Credit, "cash_refund" => SupplierClaimResolution.CashRefund, "investigation" => SupplierClaimResolution.Investigation, _ => throw Broken() };
    internal static string Decision(SupplierClaimDecision value) => value switch { SupplierClaimDecision.Approve => "approve", SupplierClaimDecision.Reject => "reject", _ => throw SupplierClaimRules.Invalid() };
    internal static SupplierClaimDecision Decision(BsonValue value) => value.AsString switch { "approve" => SupplierClaimDecision.Approve, "reject" => SupplierClaimDecision.Reject, _ => throw Broken() };
    internal static BsonDocument Line(SupplierClaimLineInput line) => new() {
        { "basis", Basis(line.Basis) }, { "inspectionId", line.InspectionId }, { "inspectionLineNumber", line.InspectionLineNumber }, { "packages", line.Packages }, { "issue", line.Issue },
        { "invoiceDocumentId", line.InvoiceDocumentId is { } d ? d : BsonNull.Value }, { "invoiceDocumentLineNumber", line.InvoiceDocumentLineNumber is { } n ? n : BsonNull.Value },
        { "invoiceMatchId", line.InvoiceMatchId is { } m ? m : BsonNull.Value }, { "invoiceMatchLineNumber", line.InvoiceMatchLineNumber is { } x ? x : BsonNull.Value } };
    internal static SupplierClaimLineInput Line(BsonValue line) => new(Basis(line["basis"]), line["inspectionId"].AsInt64, line["inspectionLineNumber"].AsInt32, line["packages"].AsInt32, line["issue"].AsString,
        line["invoiceDocumentId"].IsBsonNull ? null : line["invoiceDocumentId"].AsInt64, line["invoiceDocumentLineNumber"].IsBsonNull ? null : line["invoiceDocumentLineNumber"].AsInt32,
        line["invoiceMatchId"].IsBsonNull ? null : line["invoiceMatchId"].AsInt64, line["invoiceMatchLineNumber"].IsBsonNull ? null : line["invoiceMatchLineNumber"].AsInt32);
    internal static BsonDocument Input(SupplierClaimInput input) { input = SupplierClaimRules.Copy(input); return new() {
        { "location", R.Location(input.Location) }, { "orderId", input.OrderId }, { "orderVersion", input.OrderVersion }, { "orderApprovalId", input.OrderApprovalId },
        { "claimReference", input.ClaimReference }, { "businessDate", P.Date(input.BusinessDate) }, { "responseDueOn", P.Date(input.ResponseDueOn) },
        { "deadlineBasisReference", input.DeadlineBasisReference }, { "requestedResolution", Resolution(input.RequestedResolution) }, { "reason", input.Reason }, { "lines", new BsonArray(input.Lines.Select(Line)) } }; }
    internal static SupplierClaimInput Input(BsonValue row) => SupplierClaimRules.Copy(new SupplierClaimInput(R.Location(row["location"]), row["orderId"].AsInt64, row["orderVersion"].AsInt64, row["orderApprovalId"].AsInt64,
        row["claimReference"].AsString, P.Date(row["businessDate"]), P.Date(row["responseDueOn"]), row["deadlineBasisReference"].AsString, Resolution(row["requestedResolution"]), row["reason"].AsString, [.. row["lines"].AsBsonArray.Select(Line)]));
    internal static BsonDocument DecisionInput(SupplierClaimDecisionInput input) { input = SupplierClaimRules.Copy(input); return new() { { "decision", Decision(input.Decision) }, { "explanation", input.Explanation } }; }
    internal static SupplierClaimDecisionInput DecisionInput(BsonValue row) => SupplierClaimRules.Copy(new SupplierClaimDecisionInput(Decision(row["decision"]), row["explanation"].AsString));
    internal static object RequestBody(SupplierClaimInput input, string binding) => new { input = SupplierClaimRules.Copy(input), binding };
    internal static object DecisionBody(long claimId, long expectedVersion, SupplierClaimDecisionInput input, string binding) => new { claimId, expectedVersion, input = SupplierClaimRules.Copy(input), binding };
    internal static string Hash(BsonDocument row) => P.Hash(row);
    internal static string Binding(long actorId, long grantVersion, SupplierClaimInput input, BsonDocument source, BsonArray lines, BsonArray capacity) => Hash(new() {
        { "actorId", actorId }, { "actorGrantVersion", grantVersion }, { "input", Input(input) }, { "source", source }, { "lines", lines }, { "capacity", capacity } });
    internal static string DecisionBinding(long actorId, long grantVersion, BsonDocument request, SupplierClaimDecision decision, BsonArray capacity) => Hash(new() {
        { "actorId", actorId }, { "actorGrantVersion", grantVersion }, { "requestHash", request["snapshotHash"] }, { "decision", Decision(decision) }, { "capacity", capacity } });
    internal static string State(BsonDocument row) => !row.Contains("decision") ? "requested" : row["decision"] == "approve" ? "approved_pending_supplier_action" : "rejected_quarantined";
    internal static BsonDocument Result(BsonDocument row) => new() { { "claimId", row["claimId"] }, { "claimVersion", 1L }, { "decisionId", row.Contains("decision") ? row["_id"] : BsonNull.Value },
        { "decision", row.GetValue("decision", BsonNull.Value) }, { "state", State(row) } };
    internal static BsonDocument RequestView(BsonDocument row) => new() { { "id", row["_id"] }, { "version", 1L }, { "input", row["input"] }, { "order", PurchaseOrderService.FundingOrderView(row["source"].AsBsonDocument) },
        { "lines", row["lines"] }, { "requestedBy", row["actorId"] }, { "recordedAt", row["recordedAt"] }, { "state", "requested" } };
    internal static BsonDocument DecisionView(BsonDocument row) => new() { { "id", row["_id"] }, { "claimId", row["claimId"] }, { "decision", row["decision"] }, { "explanation", row["input"]["explanation"] },
        { "decidedBy", row["actorId"] }, { "recordedAt", row["recordedAt"] }, { "state", State(row) } };
    internal static string EffectKey(BsonDocument row) => Prefix + "command:" + row["auditId"].AsInt64;
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] }, { "requestKey", row["requestKey"] }, { "location", row["location"] }, { "orderId", row["orderId"] },
        { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string Permission(string operation) => operation switch { Prefix + "request" => Permissions.SupplierClaim, Prefix + "approve" or Prefix + "reject" => Permissions.SupplierClaimApprove, _ => throw SupplierClaimRules.Invalid() };
    internal static PersistenceConflictException Broken() => new("supplier_claim_integrity_unavailable", "The complete supplier claim and quarantine disposition history is unavailable");
    internal static PersistenceConflictException Changed(string code = "supplier_claim_changed") => new(code, "Reload the inspected source and current supplier claim quantities");
}
