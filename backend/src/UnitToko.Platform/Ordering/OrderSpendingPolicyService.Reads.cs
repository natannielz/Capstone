using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Ordering.OrderSpendingPolicyBson;

namespace UnitToko.Platform.Ordering;

public sealed partial class OrderSpendingPolicyService
{
    private static BsonDocument ContextView(BsonDocument source) {
        var nodes = source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node).ToArray();
        return new() { { "organizationId", nodes.Single(n => n.Kind == ScopeNodeKind.BuyingOrganization).Id },
            { "branchId", nodes.Single(n => n.Kind == ScopeNodeKind.Branch).Id },
            { "divisionId", nodes.SingleOrDefault(n => n.Kind == ScopeNodeKind.Division) is { } division ? new BsonInt64(division.Id) : BsonNull.Value } };
    }
    private bool Active(BsonDocument version, BsonDocument source) => version["source"].Equals(source) &&
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
        var profile = version?["source"]["profile"] ?? context.Source["profile"]; var active = version is not null && Active(version, context.Source);
        return new() { { "target", B.Target(B.Target(context.Source["target"])) }, { "context", ContextView(context.Source) }, { "code", profile["code"] }, { "name", profile["name"] },
            { "policyId", context.Head is null ? BsonNull.Value : context.Head["_id"] }, { "version", version is null ? 0L : version["version"] },
            { "versionId", version is null ? BsonNull.Value : version["_id"] }, { "rule", version is null ? BsonNull.Value : version["details"]["rule"] },
            { "validFrom", version is null ? BsonNull.Value : version["details"]["validFrom"] }, { "validUntil", version is null ? BsonNull.Value : version["details"]["validUntil"] },
            { "approvedAt", version is null ? BsonNull.Value : version["effectiveAt"] }, { "current", active }, { "active", active } };
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, OrderSpendingPolicyTarget target, CancellationToken ct = default) {
        OrderSpendingPolicyRules.RequireTarget(target); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, target, Permissions.OrderSpendingPolicyRead, token);
            await ApprovedPeopleAsync(context, token); var version = await VersionAsync(context, token);
            await FenceAsync(context, token); return CurrentView(context, version);
        }, ct);
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw OrderSpendingPolicyRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var query = Query(actor, authority, Permissions.OrderSpendingPolicyRead); var contexts = new List<Context>();
            foreach (var node in authority.Directory.Nodes.Where(n => n.Kind is ScopeNodeKind.Branch or ScopeNodeKind.Division &&
                n.Status == AuthorizationStatus.Active && (!beforeId.HasValue || n.Id < beforeId)).OrderByDescending(n => n.Id)) {
                var target = new OrderSpendingPolicyTarget(node.Kind, node.Id); var nodes = DirectoryTarget(authority, target);
                if (!query.Matches(Resource(actor, nodes))) continue;
                if (!await Rows("organizationProfiles").Find(tx, new BsonDocument("_id", target.Id)).AnyAsync(token)) continue;
                var context = await ContextAsync(actor, authority, tx, target, Permissions.OrderSpendingPolicyRead, token);
                await ApprovedPeopleAsync(context, token); contexts.Add(context); if (contexts.Count == 101) break;
            }
            var items = new BsonArray();
            foreach (var context in contexts.Take(100)) {
                items.Add(new BsonDocument { { "target", B.Target(B.Target(context.Source["target"])) }, { "context", ContextView(context.Source) },
                    { "code", context.Source["profile"]["code"] }, { "name", context.Source["profile"]["name"] },
                    { "expectedScopeVersion", context.Source["target"]["version"] }, { "expectedProfileVersion", context.Source["profile"]["version"] },
                    { "policyVersion", context.Head is null ? 0L : context.Head["version"] }, { "policyId", context.Head is null ? BsonNull.Value : context.Head["_id"] },
                    { "versionId", context.Head is null ? BsonNull.Value : context.Head["versionId"] } });
            }
            foreach (var context in contexts) await FenceAsync(context, token);
            await FenceQueryAsync(actor, authority, tx, Permissions.OrderSpendingPolicyRead, token);
            foreach (var context in contexts) Current(context); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "items", items }, { "hasMore", contexts.Count > 100 }, { "nextTargetId", contexts.Count > 100 ? items[99]["target"]["id"] : BsonNull.Value } };
        }, ct);
    }
    private async Task<BsonDocument> ProposalViewAsync(Context context, BsonDocument proposal, CancellationToken ct) {
        var decision = await records.DecisionForAsync(context.Tx, proposal, ct);
        return new() { { "proposalId", proposal["_id"] }, { "policyId", proposal["policyId"] }, { "revision", 1L }, { "target", proposal["target"] },
            { "context", ContextView(proposal["source"].AsBsonDocument) }, { "code", proposal["source"]["profile"]["code"] }, { "name", proposal["source"]["profile"]["name"] },
            { "rule", proposal["details"]["rule"] }, { "validFrom", proposal["details"]["validFrom"] }, { "validUntil", proposal["details"]["validUntil"] },
            { "baseVersion", proposal["baseVersion"] }, { "proposedAt", proposal["recordedAt"] }, { "state", decision is null ? "pending" : decision["decision"] == "approve" ? "approved" : "rejected" },
            { "decision", decision is null ? BsonNull.Value : decision["decision"] }, { "policyVersion", decision is null ? BsonNull.Value : decision["policyVersion"] },
            { "versionId", decision is null ? BsonNull.Value : decision["versionId"] },
            { "bindingCurrent", proposal["source"].Equals(context.Source) && proposal["baseVersion"] == context.Head!["version"] } };
    }
    public async Task<BsonDocument> ReadProposalAsync(ActorContext actor, long proposalId, CancellationToken ct = default) {
        if (proposalId <= 0) throw OrderSpendingPolicyRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, token, Permissions.OrderSpendingPolicyRead);
            var decision = await records.DecisionForAsync(tx, context.Proposal!, token); BindPeople(context, context.Proposal!, decision);
            await PeopleAsync(context, decision?["actorId"].AsInt64, decision is null ? null : context.Proposal!["actorId"].AsInt64, token);
            var view = await ProposalViewAsync(context, context.Proposal!, token); await FenceAsync(context, token); return view;
        }, ct);
    }
    public async Task<BsonDocument> ListProposalsAsync(ActorContext actor, OrderSpendingPolicyTarget target, long? beforeId = null, CancellationToken ct = default) {
        OrderSpendingPolicyRules.RequireTarget(target); if (beforeId is <= 0) throw OrderSpendingPolicyRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, target, Permissions.OrderSpendingPolicyRead, token);
            var filter = new BsonDocument("target", B.Target(target)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray();
            var proofs = new List<PersonAuthorityProof>();
            foreach (var row in rows.Take(25)) {
                var proposal = await records.ProposalAsync(tx, row["_id"].AsInt64, token); var decision = await records.DecisionForAsync(tx, proposal, token);
                BindPeople(context, proposal, decision); await PeopleAsync(context, decision?["actorId"].AsInt64, decision is null ? null : proposal["actorId"].AsInt64, token);
                proofs.Add(context.People!); items.Add(await ProposalViewAsync(context, proposal, token));
            }
            await ApprovedPeopleAsync(context, token); await FenceAsync(context, token);
            foreach (var proof in proofs) await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, token);
            Current(context); foreach (var proof in proofs) proof.RequireCurrent(tx, clock.UtcNow); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "items", items }, { "nextProposalId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        var permission = B.Permission(operation); Metadata(new(key, "Recover original spending policy operation", "spending-policy-recovery")); await StorageAsync(ct);
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
