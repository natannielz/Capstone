using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using S = UnitToko.Platform.Procurement.SupplierBson;

namespace UnitToko.Platform.Procurement;

internal static class SupplierBankBson
{
    internal const string Policies = "supplierBankPolicies", Heads = "supplierBankHeads", Proposals = "supplierBankProposals",
        Observations = "supplierBankObservations", Decisions = "supplierBankDecisions", Versions = "supplierBankVersions", Prefix = "procurement.supplierbank.";
    internal static BsonDocument Input(SupplierBankInput value) { value = SupplierBankRules.Copy(value); return new() {
        { "owner", S.Owner(value.Owner) }, { "supplierId", value.SupplierId }, { "supplierVersionId", value.SupplierVersionId }, { "supplierVersion", value.SupplierVersion },
        { "baseVersion", value.BaseVersion }, { "policyId", value.PolicyId }, { "policyRevision", value.PolicyRevision }, { "details", ReceivingAccountBson.Details(value.Details) } }; }
    internal static SupplierBankInput Input(BsonValue row) => SupplierBankRules.Copy(new(S.Owner(row["owner"]), row["supplierId"].AsInt64, row["supplierVersionId"].AsInt64,
        row["supplierVersion"].AsInt64, row["baseVersion"].AsInt64, row["policyId"].AsInt64, row["policyRevision"].AsInt64, ReceivingAccountBson.Details(row["details"])));
    internal static string Decision(SupplierBankDecision value) => value switch { SupplierBankDecision.Approve => "approve", SupplierBankDecision.Reject => "reject", _ => throw SupplierBankRules.Invalid() };
    internal static SupplierBankDecision Decision(BsonValue value) => value.AsString switch { "approve" => SupplierBankDecision.Approve, "reject" => SupplierBankDecision.Reject, _ => throw Broken() };
    internal static string Permission(string operation) => operation switch { Prefix + "propose" => Permissions.SupplierBankPropose,
        Prefix + "observe" or Prefix + "approve" or Prefix + "reject" => Permissions.SupplierBankApprove, _ => throw SupplierBankRules.Invalid() };
    internal static string Hash(BsonDocument row) => S.Hash(row);
    internal static BsonDocument Snapshot(BsonDocument row) => new(row.Where(field => field.Name != "fence"));
    internal static BsonDocument PolicyView(BsonDocument row) { var value = Snapshot(row); value.Remove("_id"); value.Remove("schemaVersion"); value.Add("id", row["_id"]); return value; }
    internal static BsonDocument SupplierView(BsonValue source) { var version = source["version"]; return new() {
        { "id", version["supplierId"] }, { "versionId", version["_id"] }, { "version", version["version"] }, { "subjectReference", version["details"]["subjectReference"] },
        { "legalName", version["details"]["legalName"] }, { "displayName", version["details"]["displayName"] } }; }
    internal static BsonDocument Masked(BsonValue value) { var details = ReceivingAccountBson.Details(value); return new() {
        { "bankCode", details.BankCode }, { "maskedAccount", ReceivingAccountPolicy.Mask(details) }, { "currency", details.Currency } }; }
    internal static object ProposeBody(SupplierBankInput input, string binding) => new { input = SupplierBankRules.Copy(input), binding };
    internal static object ObserveBody(long proposalId, long expectedVersion, ReceivingAccountDetails observed, string sourceReference, string binding, bool attested) =>
        new { proposalId, expectedVersion, observed = ReceivingAccountPolicy.Copy(observed), sourceReference, binding, attested };
    internal static object DecideBody(long proposalId, long expectedVersion, SupplierBankDecision decision, long? observationId, string binding) =>
        new { proposalId, expectedVersion, decision = Decision(decision), observationId, binding };
    internal static string Binding(long actor, long grantVersion, SupplierBankInput input, BsonDocument supplier, BsonDocument sources, BsonDocument policy) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "input", Input(input) }, { "supplier", supplier }, { "sources", sources }, { "policy", Snapshot(policy) } });
    internal static string ObservationBinding(long actor, long grants, BsonDocument proposal, ReceivingAccountDetails observed, string sourceReference) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grants }, { "proposalHash", proposal["snapshotHash"] }, { "observed", ReceivingAccountBson.Details(observed) }, { "sourceReference", sourceReference } });
    internal static string DecisionBinding(long actor, long grants, BsonDocument proposal, SupplierBankDecision decision, BsonDocument? observation, long currentVersion, BsonDocument sources) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grants }, { "proposalHash", proposal["snapshotHash"] }, { "decision", Decision(decision) },
        { "observationHash", observation?["snapshotHash"] ?? BsonNull.Value }, { "currentVersion", currentVersion }, { "sources", sources } });
    internal static void Metadata(BsonDocument row, string operation, long auditId, ActorContext actor, AuthorizationCommandMetadata metadata, object body, DateTimeOffset at) =>
        S.Metadata(row, operation, auditId, actor, metadata, body, at);
    internal static BsonDocument Result(BsonDocument row) => new() { { "beneficiaryId", row["beneficiaryId"] }, { "proposalId", row["proposalId"] }, { "proposalVersion", 1L },
        { "recordId", row["_id"] }, { "operation", row["operation"] }, { "versionId", row.GetValue("versionId", BsonNull.Value) }, { "version", row.GetValue("bankVersion", BsonNull.Value) } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] }, { "actorId", row["actorId"] },
        { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] }, { "requestKey", row["requestKey"] },
        { "owner", row["owner"] }, { "supplierId", row["supplierId"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Prefix + "command:" + row["auditId"].AsInt64;
    internal static void RequireBinding(string binding) { if (binding is not { Length: 64 } || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw SupplierBankRules.Invalid(); }
    internal static PersistenceConflictException Broken() => new("supplier_bank_integrity_unavailable", "The complete supplier beneficiary history is unavailable");
    internal static PersistenceConflictException Changed() => new("supplier_bank_changed", "Reload the supplier, bank beneficiary and independent verification policy");
}
