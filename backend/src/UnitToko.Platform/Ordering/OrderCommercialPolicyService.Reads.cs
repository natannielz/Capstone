using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Ordering.OrderCommercialPolicyBson;

namespace UnitToko.Platform.Ordering;

public sealed partial class OrderCommercialPolicyService
{
    private static BsonDocument ContextView(BsonDocument source) {
        var nodes = source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node).ToArray();
        return new() { { "organizationId", nodes.Single(n => n.Kind == ScopeNodeKind.BuyingOrganization).Id },
            { "branchId", nodes.Single(n => n.Kind == ScopeNodeKind.Branch).Id },
            { "divisionId", nodes.SingleOrDefault(n => n.Kind == ScopeNodeKind.Division) is { } division ? new BsonInt64(division.Id) : BsonNull.Value } };
    }
    private bool Active(BsonDocument version, Context context) => version["source"].Equals(context.Source) && version["sellerSources"].Equals(context.SellerSources) &&
        B.Date(version["effectiveAt"]) <= Now && B.Date(version["details"]["validFrom"]) <= Now && Now < B.Date(version["details"]["validUntil"]);
    private async Task FenceQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, CancellationToken ct) {
        var query = Query(actor, authority, permission); var clauses = query.Clauses;
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        await LiveSessionAsync(actor, tx, ct); _ = Query(actor, authority, permission); ct.ThrowIfCancellationRequested();
    }
    private async Task<BsonDocument?> VersionAsync(Context context, CancellationToken ct) => context.Head is null || context.Head["versionId"].IsBsonNull
        ? null : await records.RowAsync(context.Tx, B.Versions, context.Head["versionId"].AsInt64, ct);
    private BsonDocument CurrentView(Context context, BsonDocument? version) {
        var profile = version?["source"]["profile"] ?? context.Source["profile"]; var active = version is not null && Active(version, context);
        return new() { { "target", B.Target(context.Target) }, { "context", ContextView(context.Source) }, { "code", profile["code"] }, { "name", profile["name"] },
            { "seller", SellerLabels(context.SellerSources) }, { "sellerVersions", SellerVersions(context.SellerSources) },
            { "expectedScopeVersion", context.Source["target"]["version"] }, { "expectedProfileVersion", context.Source["profile"]["version"] },
            { "policyId", context.Head is null ? BsonNull.Value : context.Head["_id"] }, { "version", version is null ? 0L : version["version"] },
            { "versionId", version is null ? BsonNull.Value : version["_id"] }, { "rule", version is null ? BsonNull.Value : version["details"]["rule"] },
            { "validFrom", version is null ? BsonNull.Value : version["details"]["validFrom"] }, { "validUntil", version is null ? BsonNull.Value : version["details"]["validUntil"] },
            { "approvedAt", version is null ? BsonNull.Value : version["effectiveAt"] }, { "current", active }, { "active", active } };
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, OrderCommercialPolicyTarget target, CancellationToken ct = default) {
        OrderCommercialPolicyRules.RequireTarget(target); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, target, B.ReadPermission, token);
            await ApprovedPeopleAsync(context, token); var version = await VersionAsync(context, token);
            await FenceAsync(context, token); return CurrentView(context, version);
        }, ct);
    }
    private async Task<BsonDocument> ProposalViewAsync(Context context, BsonDocument proposal, CancellationToken ct) {
        var decision = await records.DecisionForAsync(context.Tx, proposal, ct);
        return new() { { "proposalId", proposal["_id"] }, { "policyId", proposal["policyId"] }, { "revision", 1L }, { "target", proposal["target"] },
            { "context", ContextView(proposal["source"].AsBsonDocument) }, { "code", proposal["source"]["profile"]["code"] }, { "name", proposal["source"]["profile"]["name"] },
            { "seller", SellerLabels(proposal["sellerSources"].AsBsonDocument) }, { "sellerVersions", B.SellerVersions(B.Input(proposal["input"]).SellerVersions) },
            { "rule", proposal["details"]["rule"] }, { "validFrom", proposal["details"]["validFrom"] }, { "validUntil", proposal["details"]["validUntil"] },
            { "baseVersion", proposal["baseVersion"] }, { "proposedAt", proposal["recordedAt"] }, { "state", decision is null ? "pending" : decision["decision"] == "approve" ? "approved" : "rejected" },
            { "decision", decision is null ? BsonNull.Value : decision["decision"] }, { "policyVersion", decision is null ? BsonNull.Value : decision["policyVersion"] },
            { "versionId", decision is null ? BsonNull.Value : decision["versionId"] },
            { "bindingCurrent", proposal["source"].Equals(context.Source) && proposal["sellerSources"].Equals(context.SellerSources) && proposal["baseVersion"] == context.Head!["version"] } };
    }
    public async Task<BsonDocument> ReadProposalAsync(ActorContext actor, long proposalId, CancellationToken ct = default) {
        if (proposalId <= 0) throw OrderCommercialPolicyRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, token, B.ReadPermission);
            var decision = await records.DecisionForAsync(tx, context.Proposal!, token); BindPeople(context, context.Proposal!, decision);
            await PeopleAsync(context, decision?["actorId"].AsInt64, decision is null ? null : context.Proposal!["actorId"].AsInt64, token);
            var view = await ProposalViewAsync(context, context.Proposal!, token); await FenceAsync(context, token); return view;
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        var permission = B.Permission(operation); Metadata(new(key, "Recover original commercial policy operation", "commercial-policy-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, permission); var row = await ForKeyAsync(actor, authority, tx, operation, key, token);
            if (row is null) { await FenceQueryAsync(actor, authority, tx, permission, token); return null; }
            var context = await ContextAsync(actor, authority, tx, B.Target(row["target"]), permission, token);
            var proposal = await records.ProposalAsync(tx, row["proposalId"].AsInt64, token); BindPeople(context, proposal, operation == B.Prefix + "propose" ? null : row);
            await PeopleAsync(context, operation == B.Prefix + "propose" ? null : actor.UserId, operation == B.Prefix + "propose" ? null : proposal["actorId"].AsInt64, token);
            await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }
}
