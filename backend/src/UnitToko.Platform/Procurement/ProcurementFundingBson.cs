using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using P = UnitToko.Platform.Procurement.PurchaseOrderBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementFundingBson
{
    internal const string Policies = "procurementFundingPolicies", Requests = "procurementFundingRequests", Decisions = "procurementFundingDecisions",
        Heads = "procurementFundingHeads", Allocations = "procurementFundingAllocations", Prefix = "procurement.funding.";
    internal static BsonDocument Input(ProcurementFundingInput input) { input = ProcurementFundingRules.Copy(input); return new() {
        { "location", R.Location(input.Location) }, { "orderId", input.OrderId }, { "orderVersion", input.OrderVersion }, { "orderApprovalId", input.OrderApprovalId },
        { "policyId", input.PolicyId }, { "policyRevision", input.PolicyRevision }, { "amount", input.Amount.ToString() }, { "businessDate", P.Date(input.BusinessDate) }, { "neededBy", P.Date(input.NeededBy) } }; }
    internal static ProcurementFundingInput Input(BsonValue row) => ProcurementFundingRules.Copy(new(R.Location(row["location"]), row["orderId"].AsInt64, row["orderVersion"].AsInt64,
        row["orderApprovalId"].AsInt64, row["policyId"].AsInt64, row["policyRevision"].AsInt64, Money.Parse(row["amount"].AsString), P.Date(row["businessDate"]), P.Date(row["neededBy"])));
    internal static string Decision(ProcurementFundingDecision value) => value switch { ProcurementFundingDecision.Approve => "approve", ProcurementFundingDecision.Reject => "reject", _ => throw ProcurementFundingRules.Invalid() };
    internal static ProcurementFundingDecision Decision(BsonValue value) => value.AsString switch { "approve" => ProcurementFundingDecision.Approve, "reject" => ProcurementFundingDecision.Reject, _ => throw Broken() };
    internal static string Permission(string operation) => operation switch { Prefix + "request" => Permissions.FundingRequest, Prefix + "approve" or Prefix + "reject" => Permissions.FundingApprove, _ => throw ProcurementFundingRules.Invalid() };
    internal static BsonDocument PolicySnapshot(BsonDocument row) => new(row.Where(field => field.Name != "fence"));
    internal static BsonDocument PolicyView(BsonDocument row) { var view = new BsonDocument("id", row["_id"]); view.AddRange(row.Where(field => field.Name is not ("_id" or "fence" or "schemaVersion"))); return view; }
    internal static BsonDocument Person(PersonEvidence value) => P.Person(value);
    internal static PersonEvidence Person(BsonValue value) => P.Person(value);
    internal static string Hash(BsonDocument row) => P.Hash(row);
    internal static object RequestBody(ProcurementFundingInput input, string binding) => new { input = ProcurementFundingRules.Copy(input), binding };
    internal static object DecideBody(long requestId, long expectedVersion, ProcurementFundingDecision decision, string binding) => new { requestId, expectedVersion, decision = Decision(decision), binding };
    internal static string Binding(long actor, long grantVersion, ProcurementFundingInput input, BsonDocument source, BsonDocument sources, BsonDocument policy, BsonDocument capacity) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "input", Input(input) }, { "orderSource", source }, { "sources", sources }, { "policy", PolicySnapshot(policy) }, { "capacity", capacity } });
    internal static string DecisionBinding(long actor, long grantVersion, BsonDocument request, ProcurementFundingDecision decision, BsonDocument sources, BsonDocument? policy, BsonDocument? capacity) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "requestHash", request["snapshotHash"] }, { "decision", Decision(decision) }, { "sources", sources },
        { "policy", policy is null ? BsonNull.Value : PolicySnapshot(policy) }, { "capacity", capacity is null ? BsonNull.Value : capacity } });
    internal static void Metadata(BsonDocument row, string operation, long auditId, ActorContext actor, AuthorizationCommandMetadata metadata, object body, DateTimeOffset at) => P.Metadata(row, operation, auditId, actor, metadata, body, at);
    internal static BsonDocument Result(BsonDocument row) => new() { { "requestId", row["requestId"] }, { "requestVersion", 1L }, { "decisionId", row.Contains("decision") ? row["_id"] : BsonNull.Value },
        { "authorizationId", row.GetValue("authorizationId", BsonNull.Value) }, { "authorizedAmount", row.GetValue("authorizedAmount", BsonNull.Value) },
        { "status", !row.Contains("decision") ? "requested" : row["decision"] == "approve" ? "approved" : "rejected" } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] }, { "requestKey", row["requestKey"] }, { "location", row["location"] }, { "orderId", row["orderId"] },
        { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Prefix + "command:" + row["auditId"].AsInt64;
    internal static void RequireBinding(string binding) { if (binding is not { Length: 64 } || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw ProcurementFundingRules.Invalid(); }
    internal static PersistenceConflictException Broken() => new("procurement_funding_integrity_unavailable", "The complete advance authorization history is unavailable");
    internal static PersistenceConflictException Changed() => new("procurement_funding_changed", "Reload the approved purchase order, funding policy and remaining authorization capacity");
}
