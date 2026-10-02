using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Organizations;

namespace UnitToko.Platform.Ordering;

internal static class OrderSpendingPolicyBson
{
    internal const string Heads = "orderSpendingPolicyHeads", Proposals = "orderSpendingPolicyProposals",
        Decisions = "orderSpendingPolicyDecisions", Versions = "orderSpendingPolicyVersions", Prefix = "ordering.spending.policy.";
    internal static string Kind(ScopeNodeKind kind) => AuthorizationBson.Name(kind);
    internal static BsonDocument Target(OrderSpendingPolicyTarget target) => new() { { "kind", Kind(target.Kind) }, { "id", target.Id } };
    internal static OrderSpendingPolicyTarget Target(BsonValue row) {
        var target = new OrderSpendingPolicyTarget(OrganizationAddressBson.Kind(row["kind"]), row["id"].AsInt64);
        OrderSpendingPolicyRules.RequireTarget(target); return target;
    }
    internal static string Mode(OrderSpendingMode value) => value switch { OrderSpendingMode.EveryOrder => "every_order",
        OrderSpendingMode.AboveThreshold => "above_threshold", OrderSpendingMode.NotRequired => "not_required", _ => throw Broken() };
    internal static OrderSpendingRule Rule(BsonValue row) => new(row["mode"].AsString switch {
        "every_order" => OrderSpendingMode.EveryOrder, "above_threshold" => OrderSpendingMode.AboveThreshold,
        "not_required" => OrderSpendingMode.NotRequired, _ => throw Broken() },
        row["threshold"].IsBsonNull ? null : new Money(Decimal128.ToDecimal(row["threshold"].AsDecimal128)));
    internal static BsonDocument Rule(OrderSpendingRule value) => new() { { "mode", Mode(value.Mode) },
        { "threshold", value.Threshold is { } threshold ? new BsonDecimal128(threshold.Value) : BsonNull.Value } };
    internal static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    internal static BsonDocument Details(OrderSpendingRule rule, DateTimeOffset from, DateTimeOffset until) {
        OrderSpendingPolicyRules.RequireTerms(rule, from, until);
        return new() { { "rule", Rule(rule) }, { "validFrom", from.UtcDateTime }, { "validUntil", until.UtcDateTime } };
    }
    internal static BsonDocument Details(OrderSpendingPolicyInput input) => Details(input.Rule, input.ValidFrom, input.ValidUntil);
    internal static BsonDocument Details(BsonValue row) => Details(Rule(row["rule"]), Date(row["validFrom"]), Date(row["validUntil"]));
    internal static BsonDocument Input(OrderSpendingPolicyInput value) { value = OrderSpendingPolicyRules.Copy(value); return new() {
        { "target", Target(value.Target) }, { "expectedScopeVersion", value.ExpectedScopeVersion }, { "expectedProfileVersion", value.ExpectedProfileVersion },
        { "expectedPolicyVersion", value.ExpectedPolicyVersion }, { "rule", Rule(value.Rule) }, { "validFrom", value.ValidFrom.UtcDateTime }, { "validUntil", value.ValidUntil.UtcDateTime } }; }
    internal static OrderSpendingPolicyInput Input(BsonValue row) => OrderSpendingPolicyRules.Copy(new(Target(row["target"]), row["expectedScopeVersion"].AsInt64,
        row["expectedProfileVersion"].AsInt64, row["expectedPolicyVersion"].AsInt64, Rule(row["rule"]), Date(row["validFrom"]), Date(row["validUntil"])));
    internal static string Decision(OrderSpendingPolicyDecision value) => value switch { OrderSpendingPolicyDecision.Approve => "approve", OrderSpendingPolicyDecision.Reject => "reject", _ => throw Broken() };
    internal static OrderSpendingPolicyDecision Decision(BsonValue row) => row.AsString switch { "approve" => OrderSpendingPolicyDecision.Approve, "reject" => OrderSpendingPolicyDecision.Reject, _ => throw Broken() };
    internal static BsonDocument DecisionInput(long id, long revision, OrderSpendingPolicyDecision decision) => new() { { "proposalId", id }, { "expectedRevision", revision }, { "decision", Decision(decision) } };
    internal static object ProposeBody(OrderSpendingPolicyInput input, string binding) => new { input = OrderSpendingPolicyRules.Copy(input), binding };
    internal static object DecideBody(long proposalId, long revision, OrderSpendingPolicyDecision decision, string binding) => new { input = new { proposalId, expectedRevision = revision, decision = Decision(decision) }, binding };
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(p => p.Name is not ("snapshotHash" or "fence"))));
    internal static string Binding(OrderSpendingPolicyInput input, BsonDocument source, BsonDocument? head) => Hash(new() {
        { "input", Input(input) }, { "sourceHash", source["snapshotHash"] }, { "policyId", head is null ? BsonNull.Value : head["_id"] },
        { "policyVersion", head is null ? 0L : head["version"] }, { "versionId", head is null ? BsonNull.Value : head["versionId"] } });
    internal static string DecisionBinding(BsonDocument proposal, OrderSpendingPolicyDecision decision) => Hash(new() {
        { "proposalId", proposal["_id"] }, { "proposalHash", proposal["snapshotHash"] }, { "sourceHash", proposal["source"]["snapshotHash"] }, { "baseVersion", proposal["baseVersion"] }, { "decision", Decision(decision) } });
    internal static BsonDocument Person(PersonEvidence value) => OrganizationAddressBson.Person(value);
    internal static PersonEvidence Person(BsonValue row) => OrganizationAddressBson.Person(row);
    internal static string Permission(string operation) => operation switch {
        Prefix + "propose" => Permissions.OrderSpendingPolicyPropose, Prefix + "approve" or Prefix + "reject" => Permissions.OrderSpendingPolicyApprove, _ => throw OrderSpendingPolicyRules.Invalid() };
    internal static void Metadata(BsonDocument row, string operation, long auditId, ActorContext actor, AuthorizationCommandMetadata metadata, object body, DateTimeOffset now) {
        row.Add("operation", operation); row.Add("actorId", actor.UserId); row.Add("actorGrantVersion", actor.GrantVersion); row.Add("recordedAt", now.UtcDateTime);
        row.Add("requestKey", metadata.IdempotencyKey); row.Add("bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason)); row.Add("reason", metadata.Reason);
        row.Add("correlationId", metadata.CorrelationId); row.Add("auditId", auditId); row.Add("snapshotHash", Hash(row));
    }
    internal static BsonDocument Result(BsonDocument row) => new() { { "policyId", row["policyId"] }, { "proposalId", row["proposalId"] }, { "recordId", row["_id"] },
        { "operation", row["operation"] }, { "policyVersion", row.GetValue("policyVersion", BsonNull.Value) }, { "versionId", row.GetValue("versionId", BsonNull.Value) }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "requestKey", row["requestKey"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Prefix + "command:" + row["auditId"].AsInt64;
    internal static void RequireBinding(string binding) { if (binding is not { Length: 64 } || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw OrderSpendingPolicyRules.Invalid(); }
    internal static PersistenceConflictException Broken() => new("order_spending_policy_integrity_unavailable", "The complete approved spending policy history is unavailable");
    internal static PersistenceConflictException Changed() => new("order_spending_policy_changed", "Reload the current organization, policy and proposal before continuing");
}
