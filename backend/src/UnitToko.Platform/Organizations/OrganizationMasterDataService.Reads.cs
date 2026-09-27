using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Organizations;

public sealed partial class OrganizationMasterDataService
{
    public async Task<OrganizationPage> ListAsync(ActorContext actor, int offset, int limit, AuthorizationReadMetadata audit,
        CancellationToken cancellationToken = default)
    {
        AuthorizationStore.ValidatePage(offset, limit);
        var readId = (await ids.NextAsync("authorizationReadEvents", cancellationToken)).Value;
        return await store.ReadAsync(actor, async (snapshot, session, token) =>
        {
            DemandRead(actor, snapshot);
            var nodes = snapshot.Directory.Nodes.OrderBy(node => node.Id).Skip(offset).Take(limit).ToArray();
            var profiles = await Collection("organizationProfiles").Find(session, F.In("_id", nodes.Select(node => node.Id))).ToListAsync(token);
            var result = new OrganizationPage(snapshot.Directory.Nodes.Length,
                nodes.Select(node => Summarize(node, profiles.SingleOrDefault(profile => profile["_id"].AsInt64 == node.Id), actor, snapshot)).ToImmutableArray(),
                GlobalActions(actor, snapshot));
            await RecordReadAsync(readId, actor, snapshot, audit, new() { { "offset", offset }, { "limit", limit } }, result.Items.Length, session, token);
            return result;
        }, cancellationToken);
    }
    public async Task<OrganizationSummary> ReadAsync(ActorContext actor, long scopeId, AuthorizationReadMetadata audit,
        CancellationToken cancellationToken = default)
    {
        ValidateVersion(scopeId, 1);
        var readId = (await ids.NextAsync("authorizationReadEvents", cancellationToken)).Value;
        return await store.ReadAsync(actor, async (snapshot, session, token) =>
        {
            DemandRead(actor, snapshot);
            var node = snapshot.Directory.Nodes.SingleOrDefault(value => value.Id == scopeId) ?? throw new AuthorizationDeniedException("resource_unavailable");
            var profile = await Collection("organizationProfiles").Find(session, F.Eq("_id", scopeId)).FirstOrDefaultAsync(token);
            var result = Summarize(node, profile, actor, snapshot);
            await RecordReadAsync(readId, actor, snapshot, audit, new("subjectId", scopeId), 1, session, token);
            return result;
        }, cancellationToken);
    }
    public async Task<OrganizationProposalPage> ListProposalsAsync(ActorContext actor, int offset, int limit, AuthorizationReadMetadata audit,
        CancellationToken cancellationToken = default)
    {
        AuthorizationStore.ValidatePage(offset, limit);
        var readId = (await ids.NextAsync("authorizationReadEvents", cancellationToken)).Value;
        return await store.ReadAsync(actor, async (snapshot, session, token) =>
        {
            DemandRead(actor, snapshot);
            var count = await Collection("organizationProposals").CountDocumentsAsync(session, F.Empty, cancellationToken: token);
            var values = await Collection("organizationProposals").Find(session, F.Empty).Sort(new BsonDocument("_id", -1)).Skip(offset).Limit(limit).ToListAsync(token);
            var items = ImmutableArray.CreateBuilder<OrganizationProposalSummary>();
            foreach (var value in values) items.Add(await SummarizeProposalAsync(value, actor, snapshot, session, token));
            await RecordReadAsync(readId, actor, snapshot, audit, new() { { "offset", offset }, { "limit", limit } }, items.Count, session, token);
            return new OrganizationProposalPage(count, items.ToImmutable(), GlobalActions(actor, snapshot));
        }, cancellationToken);
    }
    public async Task<OrganizationProposalSummary> ReadProposalAsync(ActorContext actor, long proposalId, AuthorizationReadMetadata audit,
        CancellationToken cancellationToken = default)
    {
        ValidateVersion(proposalId, 1);
        var readId = (await ids.NextAsync("authorizationReadEvents", cancellationToken)).Value;
        return await store.ReadAsync(actor, async (snapshot, session, token) =>
        {
            DemandRead(actor, snapshot);
            var proposal = await FindAsync("organizationProposals", proposalId, session, token);
            var result = await SummarizeProposalAsync(proposal, actor, snapshot, session, token);
            await RecordReadAsync(readId, actor, snapshot, audit, new("subjectId", proposalId), 1, session, token);
            return result;
        }, cancellationToken);
    }
    private Task RecordReadAsync(long id, ActorContext actor, AuthorizationSnapshot snapshot, AuthorizationReadMetadata audit,
        BsonDocument selection, int count, IClientSessionHandle session, CancellationToken token) =>
        store.RecordPrivilegedReadAsync(id, actor, snapshot, "organization.workbench.read", audit, selection, count, [new(GrantScopeKind.System)], session, token);
    private ImmutableArray<string> GlobalActions(ActorContext actor, AuthorizationSnapshot snapshot)
    {
        var actions = ImmutableArray.CreateBuilder<string>();
        if (Decision(actor, snapshot, Permissions.TechnicalConfigure).Allowed) actions.Add("propose");
        return actions.ToImmutable();
    }
    private OrganizationSummary Summarize(ScopeNode node, BsonDocument? profile, ActorContext actor, AuthorizationSnapshot snapshot)
    {
        var actions = ImmutableArray.CreateBuilder<string>();
        if (Decision(actor, snapshot, Permissions.TechnicalConfigure).Allowed && node.Status is AuthorizationStatus.Active or AuthorizationStatus.Suspended)
        {
            try
            {
                OrganizationChangePolicy.ValidateHierarchy(new(OrganizationChangeKind.UpdateProfile, node.Kind, node.ParentId, "REFERENCE", "Reference", node.Id, node.Version), snapshot.Directory, node);
                actions.Add("updateProfile");
                if (node.Status == AuthorizationStatus.Suspended) actions.Add("reactivate");
            }
            catch (DomainValidationException) { /* A stale/inactive parent never advertises a widening action. */ }
        }
        if (node.Status == AuthorizationStatus.Active && Decision(actor, snapshot, Permissions.AccessRevoke).Allowed) actions.Add("suspend");
        return new(node.Id, node.Kind, node.ParentId, AuthorizationBson.Name(node.Status), node.Version,
            profile?["code"].AsString, profile?["name"].AsString, profile?["version"].AsInt64, actions.ToImmutable());
    }
    private async Task<OrganizationProposalSummary> SummarizeProposalAsync(BsonDocument value, ActorContext actor,
        AuthorizationSnapshot snapshot, IClientSessionHandle session, CancellationToken token)
    {
        var request = Request(value); var impact = ImpactSummary(value["impact"].AsBsonDocument);
        var actions = ImmutableArray.CreateBuilder<string>();
        if (value["status"] == "pending")
        {
            if (OrganizationChangePolicy.CanDecide(actor, snapshot, value["requestedBy"].AsInt64, clock.UtcNow).Allowed)
            {
                actions.Add("reject");
                try
                {
                    await ValidateChangeAsync(request, snapshot, session, token);
                    var currentImpact = await ImpactAsync(request, snapshot.Directory, session, token);
                    if (currentImpact.Summary.Revision == impact.Revision && !currentImpact.Identities.Any(identity => identity["_id"].AsInt64 == actor.UserId)) actions.Add("approve");
                }
                catch (Exception error) when (error is DomainValidationException or PersistenceConflictException or AuthorizationDeniedException) { }
            }
            if (OrganizationChangePolicy.CanDecide(actor, snapshot, value["requestedBy"].AsInt64, clock.UtcNow, true).Allowed) actions.Add("withdraw");
        }
        var before = value["before"].AsBsonDocument;
        return new(value["_id"].AsInt64, request, impact,
            new(NullableText(before, "code"), NullableText(before, "name"), NullableText(before, "status"), before["scopeVersion"].AsInt64, before["profileVersion"].AsInt64),
            value["requestedBy"].AsInt64, value["reason"].AsString, value["status"].AsString, value["version"].AsInt64,
            new(value["createdAt"].ToUniversalTime(), TimeSpan.Zero), NullableLong(value, "approvedBy"), NullableDate(value, "approvedAt"),
            NullableLong(value, "decisionBy"), NullableDate(value, "decisionAt"), NullableText(value, "decisionReason"), NullableLong(value, "resultScopeId"), actions.ToImmutable());
    }
}
