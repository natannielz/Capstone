using MongoDB.Bson;
using System.Globalization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Organizations;

namespace UnitToko.Platform.Ordering;

internal static class OrderCommercialPolicyBson
{
    internal const string Heads = "orderCommercialPolicyHeads", Proposals = "orderCommercialPolicyProposals",
        Decisions = "orderCommercialPolicyDecisions", Versions = "orderCommercialPolicyVersions", Prefix = "ordering.commercial.policy.";
    internal const string ReadPermission = "order.commercial.policy.read", ProposePermission = "order.commercial.policy.propose", ApprovePermission = "order.commercial.policy.approve";
    internal static string Kind(ScopeNodeKind kind) => AuthorizationBson.Name(kind);
    internal static BsonDocument Target(OrderCommercialPolicyTarget target) => new() { { "kind", Kind(target.Kind) }, { "id", target.Id }, { "seller", AccountingBson.Key(target.Seller) } };
    internal static OrderCommercialPolicyTarget Target(BsonValue row) {
        var target = new OrderCommercialPolicyTarget(OrganizationAddressBson.Kind(row["kind"]), row["id"].AsInt64, AccountingBson.ReadKey(row["seller"]));
        OrderCommercialPolicyRules.RequireTarget(target); return target;
    }
    private static string Applicability(OrderCommercialApplicability value) => value switch {
        OrderCommercialApplicability.Required => "required", OrderCommercialApplicability.NotApplicable => "not_applicable", _ => throw Broken() };
    private static OrderCommercialApplicability Applicability(BsonValue value) => value.AsString switch {
        "required" => OrderCommercialApplicability.Required, "not_applicable" => OrderCommercialApplicability.NotApplicable, _ => throw Broken() };
    internal static OrderCommercialRule Rule(BsonValue row) {
        var a = row["applicability"]; var t = row["terms"];
        return OrderCommercialPolicyRules.CopyRule(new(new(Applicability(a["budget"]), Applicability(a["costCenter"]), Applicability(a["spending"]), Applicability(a["creditHold"])),
            new Money(Decimal128.ToDecimal(row["charges"].AsDecimal128)), row["taxTreatment"].AsString, Decimal128.ToDecimal(row["taxRate"].AsDecimal128),
            row["exemptionReference"].IsBsonNull ? null : row["exemptionReference"].AsString, row["taxBase"].AsString, row["taxRounding"].AsString, row["rounding"].AsString,
            new(t["grouping"].AsString, t["termOrigin"].AsString, t["termDays"].AsInt32, t["dayRule"].AsString,
                [.. t["workingDays"].AsBsonArray.Select(value => (DayOfWeek)value.AsInt32)],
                [.. t["holidays"].AsBsonArray.Select(value => DateOnly.ParseExact(value.AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture))],
                t["numberingProcedure"].AsString, t["reissueProcedure"].AsString, t["taxDocumentProcess"].AsString)));
    }
    internal static BsonDocument Rule(OrderCommercialRule value) {
        value = OrderCommercialPolicyRules.CopyRule(value); var a = value.Applicability; var t = value.Terms;
        return new() { { "applicability", new BsonDocument { { "budget", Applicability(a.Budget) }, { "costCenter", Applicability(a.CostCenter) },
                { "spending", Applicability(a.Spending) }, { "creditHold", Applicability(a.CreditHold) } } },
            { "charges", new BsonDecimal128(value.Charges.Value) }, { "taxTreatment", value.TaxTreatment }, { "taxRate", new BsonDecimal128(value.TaxRate) },
            { "exemptionReference", value.ExemptionReference is null ? BsonNull.Value : new BsonString(value.ExemptionReference) },
            { "taxBase", value.TaxBase }, { "taxRounding", value.TaxRounding }, { "rounding", value.Rounding },
            { "terms", new BsonDocument { { "grouping", t.Grouping }, { "termOrigin", t.TermOrigin }, { "termDays", t.TermDays }, { "dayRule", t.DayRule },
                { "workingDays", new BsonArray(t.WorkingDays.Select(day => (int)day)) }, { "holidays", new BsonArray(t.Holidays.Select(day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) },
                { "numberingProcedure", t.NumberingProcedure }, { "reissueProcedure", t.ReissueProcedure }, { "taxDocumentProcess", t.TaxDocumentProcess } } } };
    }
    internal static BsonDocument SellerVersions(OrderCommercialSellerVersions value) => new() {
        { "organizationScopeVersion", value.OrganizationScopeVersion }, { "organizationProfileVersion", value.OrganizationProfileVersion },
        { "shopScopeVersion", value.ShopScopeVersion }, { "shopProfileVersion", value.ShopProfileVersion },
        { "channelScopeVersion", value.ChannelScopeVersion }, { "channelProfileVersion", value.ChannelProfileVersion } };
    internal static OrderCommercialSellerVersions SellerVersions(BsonValue value) => new(value["organizationScopeVersion"].AsInt64,
        value["organizationProfileVersion"].AsInt64, value["shopScopeVersion"].AsInt64, value["shopProfileVersion"].AsInt64,
        value["channelScopeVersion"].AsInt64, value["channelProfileVersion"].AsInt64);
    internal static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    internal static BsonDocument Details(OrderCommercialRule rule, DateTimeOffset from, DateTimeOffset until) {
        OrderCommercialPolicyRules.RequireTerms(rule, from, until);
        return new() { { "rule", Rule(rule) }, { "validFrom", from.UtcDateTime }, { "validUntil", until.UtcDateTime } };
    }
    internal static BsonDocument Details(OrderCommercialPolicyInput input) => Details(input.Rule, input.ValidFrom, input.ValidUntil);
    internal static BsonDocument Details(BsonValue row) => Details(Rule(row["rule"]), Date(row["validFrom"]), Date(row["validUntil"]));
    internal static BsonDocument Input(OrderCommercialPolicyInput value) { value = OrderCommercialPolicyRules.Copy(value); return new() {
        { "target", Target(value.Target) }, { "expectedScopeVersion", value.ExpectedScopeVersion }, { "expectedProfileVersion", value.ExpectedProfileVersion },
        { "sellerVersions", SellerVersions(value.SellerVersions) }, { "expectedPolicyVersion", value.ExpectedPolicyVersion }, { "rule", Rule(value.Rule) }, { "validFrom", value.ValidFrom.UtcDateTime }, { "validUntil", value.ValidUntil.UtcDateTime } }; }
    internal static OrderCommercialPolicyInput Input(BsonValue row) => OrderCommercialPolicyRules.Copy(new(Target(row["target"]), row["expectedScopeVersion"].AsInt64,
        row["expectedProfileVersion"].AsInt64, SellerVersions(row["sellerVersions"]), row["expectedPolicyVersion"].AsInt64, Rule(row["rule"]), Date(row["validFrom"]), Date(row["validUntil"])));
    internal static string Decision(OrderCommercialPolicyDecision value) => value switch { OrderCommercialPolicyDecision.Approve => "approve", OrderCommercialPolicyDecision.Reject => "reject", _ => throw Broken() };
    internal static OrderCommercialPolicyDecision Decision(BsonValue row) => row.AsString switch { "approve" => OrderCommercialPolicyDecision.Approve, "reject" => OrderCommercialPolicyDecision.Reject, _ => throw Broken() };
    internal static BsonDocument DecisionInput(long id, long revision, OrderCommercialPolicyDecision decision) => new() { { "proposalId", id }, { "expectedRevision", revision }, { "decision", Decision(decision) } };
    internal static object ProposeBody(OrderCommercialPolicyInput input, string binding) => new { input = OrderCommercialPolicyRules.Copy(input), binding };
    internal static object DecideBody(long proposalId, long revision, OrderCommercialPolicyDecision decision, string binding) => new { input = new { proposalId, expectedRevision = revision, decision = Decision(decision) }, binding };
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(p => p.Name is not ("snapshotHash" or "fence"))));
    internal static string Binding(OrderCommercialPolicyInput input, BsonDocument source, BsonDocument sellers, BsonDocument? head) => Hash(new() {
        { "input", Input(input) }, { "sourceHash", source["snapshotHash"] }, { "policyId", head is null ? BsonNull.Value : head["_id"] },
        { "sellerSources", sellers }, { "policyVersion", head is null ? 0L : head["version"] }, { "versionId", head is null ? BsonNull.Value : head["versionId"] } });
    internal static string DecisionBinding(BsonDocument proposal, OrderCommercialPolicyDecision decision) => Hash(new() {
        { "proposalId", proposal["_id"] }, { "proposalHash", proposal["snapshotHash"] }, { "sourceHash", proposal["source"]["snapshotHash"] },
        { "sellerSources", proposal["sellerSources"] }, { "baseVersion", proposal["baseVersion"] }, { "decision", Decision(decision) } });
    internal static BsonDocument Person(PersonEvidence value) => OrganizationAddressBson.Person(value);
    internal static PersonEvidence Person(BsonValue row) => OrganizationAddressBson.Person(row);
    internal static string Permission(string operation) => operation switch {
        Prefix + "propose" => ProposePermission, Prefix + "approve" or Prefix + "reject" => ApprovePermission, _ => throw OrderCommercialPolicyRules.Invalid() };
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
    internal static void RequireBinding(string binding) { if (binding is not { Length: 64 } || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw OrderCommercialPolicyRules.Invalid(); }
    internal static PersistenceConflictException Broken() => new("order_commercial_policy_integrity_unavailable", "The complete approved commercial bundle history is unavailable");
    internal static PersistenceConflictException Changed() => new("order_commercial_policy_changed", "Reload the current organization, policy and proposal before continuing");
}
