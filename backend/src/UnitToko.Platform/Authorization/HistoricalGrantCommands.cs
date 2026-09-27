using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Authorization;

public sealed partial class AccessAdministrationService
{
    public Task<GrantPreview> PreviewHistoricalGrantAsync(ActorContext actor, HistoricalGrantChangeRequest request,
        CancellationToken cancellationToken = default, AuthorizationReadMetadata? audit = null) =>
        AuditedReadAsync(actor, "access.grant.preview", audit, new("subjectId", request.UserId), async (snapshot, session, token) =>
        {
            var authority = DemandAdministrative(actor, snapshot, Permissions.AccessPropose);
            var candidate = MaterializeHistorical(actor, request, 1, "Preview");
            var membership = await ValidateHistoricalBeneficiaryAsync(candidate, snapshot.Directory, session, token);
            var current = await Collection("authorizationGrants").Find(session, F.Eq("userId", request.UserId)).ToListAsync(token);
            var existingGrants = current.Select(AuthorizationBson.Grant).Where(grant =>
                HistoricalReadPolicy.IsGrantActive(grant, request.UserId, clock.UtcNow) && grant.Scope == candidate.Scope &&
                HistoricalReadPolicy.IsBeneficiaryEligible(membership, grant, snapshot.Directory, clock.UtcNow)).ToArray();
            await FenceHistoricalGrantAsync(actor, snapshot, authority, candidate, membership, session, token, existingGrants);
            DemandAdministrative(actor, snapshot, Permissions.AccessPropose);
            RequireHistoricalBeneficiary(candidate, membership, snapshot.Directory);
            var existing = existingGrants.Where(grant => HistoricalReadPolicy.IsGrantActive(grant, request.UserId, clock.UtcNow))
                .SelectMany(grant => grant.Permissions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            return new AuditedValue<GrantPreview>(new(request.UserId, request.MembershipId, candidate.TemplateId, candidate.TemplateVersion,
                candidate.Permissions, existing, candidate.Scope, true, [], candidate.EffectiveAt, candidate.ExpiresAt), 1, [new(GrantScopeKind.System)]);
        }, cancellationToken);

    public async Task<AccessProposalResult> ProposeHistoricalGrantAsync(ActorContext actor, HistoricalGrantChangeRequest request,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        AuthorizationStore.ValidateMetadata(metadata);
        var proposalId = (await ids.NextAsync("authorizationProposals", cancellationToken)).Value;
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        var candidate = MaterializeHistorical(actor, request, proposalId, metadata.Reason);
        var result = await store.ExecuteCommandAsync(actor, "access.grant.propose", metadata,
            new { GrantKind = HistoricalReadPolicy.TemplateId, request.UserId, request.MembershipId, candidate.Permissions,
                request.Scope, request.EffectiveAt, request.ExpiresAt },
            (snapshot, session, token) => AuthorizeHistoricalGrantProposalAsync(actor, snapshot, candidate, session, token), async (session, token) =>
            {
                var proposal = Proposal(proposalId, "grant", request.UserId, candidate.Scope, actor.UserId, metadata.Reason, AuthorizationBson.Grant(candidate));
                await Collection("authorizationProposals").InsertOneAsync(session, proposal, cancellationToken: token);
                await AuditAsync(historyId, actor, request.UserId, "access.grant.proposed", metadata, new(), proposal, session, token);
                await AuthorizeHistoricalGrantProposalAsync(actor, await store.LoadAsync(actor, session, token), candidate, session, token);
                return ProposalResult(proposal);
            }, cancellationToken);
        return ParseProposalResult(result);
    }

    private PermissionGrant MaterializeHistorical(ActorContext actor, HistoricalGrantChangeRequest request, long id, string reason)
    {
        ValidateEffective(request.UserId, request.EffectiveAt, request.ExpiresAt);
        if (request.Permissions.IsDefaultOrEmpty || request.Permissions.Length > 2 ||
            request.Permissions.Distinct(StringComparer.Ordinal).Count() != request.Permissions.Length)
            throw new DomainValidationException("invalid_historical_grant", "Select each permitted historical read explicitly once");
        var candidate = new PermissionGrant(id, request.UserId, request.MembershipId, HistoricalReadPolicy.TemplateId,
            HistoricalReadPolicy.TemplateVersion, request.Permissions.Order(StringComparer.Ordinal).ToImmutableArray(), request.Scope,
            AuthorizationStatus.Pending, actor.UserId, 0, reason, request.EffectiveAt, request.ExpiresAt, DelegablePermissions: []);
        if (!HistoricalReadPolicy.IsGrantShape(candidate))
            throw new DomainValidationException("invalid_historical_grant", "Historical access requires an exact branch or division, an approved parent membership and selected read permissions");
        return candidate;
    }

    private async Task<Membership> ValidateHistoricalBeneficiaryAsync(PermissionGrant candidate, ScopeDirectory directory,
        IClientSessionHandle session, CancellationToken token)
    {
        await RequireActiveIdentityAsync(candidate.UserId, session, token);
        var membership = AuthorizationBson.Membership(await FindAsync("memberships", candidate.MembershipId, session, token));
        RequireHistoricalBeneficiary(candidate, membership, directory);
        return membership;
    }

    private static void ValidateHistoricalProposal(BsonDocument proposal, PermissionGrant candidate)
    {
        if (candidate.Id != proposal["_id"].AsInt64 || candidate.UserId != proposal["subjectId"].AsInt64 ||
            candidate.RequestedBy != proposal["requestedBy"].AsInt64 || candidate.Reason != proposal["reason"].AsString ||
            candidate.Scope != AuthorizationBson.Scope(proposal["scope"].AsBsonDocument))
            throw new AuthorizationDeniedException("invalid_historical_grant");
    }

    private void RequireHistoricalBeneficiary(PermissionGrant candidate, Membership membership, ScopeDirectory directory)
    {
        ValidateEffective(candidate.UserId, candidate.EffectiveAt, candidate.ExpiresAt);
        if (!HistoricalReadPolicy.IsGrantShape(candidate) || !HistoricalReadPolicy.IsTargetEligible(candidate.Scope, directory))
            throw new AuthorizationDeniedException("invalid_historical_grant");
        if (!HistoricalReadPolicy.IsBeneficiaryEligible(membership, candidate, directory, clock.UtcNow))
            throw new AuthorizationDeniedException("approved_membership_required");
    }

    private async Task<CommandAuthorization> AuthorizeHistoricalGrantApprovalAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        PermissionGrant candidate, IClientSessionHandle session, CancellationToken token)
    {
        var membership = await ValidateHistoricalBeneficiaryAsync(candidate, snapshot.Directory, session, token);
        var decision = HistoricalReadPolicy.CanApprove(actor, snapshot, candidate, membership, clock.UtcNow);
        if (!decision.Allowed) throw new AuthorizationDeniedException(decision.Code);
        await FenceHistoricalGrantAsync(actor, snapshot, decision, candidate, membership, session, token);
        var current = HistoricalReadPolicy.CanApprove(actor, snapshot, candidate, membership, clock.UtcNow);
        if (!current.Allowed) throw new AuthorizationDeniedException(current.Code);
        return new(current, ScopePolicy.RequiredNodes(membership.Scope, snapshot.Directory));
    }

    private async Task<CommandAuthorization> AuthorizeHistoricalGrantProposalAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        PermissionGrant candidate, IClientSessionHandle session, CancellationToken token)
    {
        var decision = DemandAdministrative(actor, snapshot, Permissions.AccessPropose);
        var membership = await ValidateHistoricalBeneficiaryAsync(candidate, snapshot.Directory, session, token);
        await FenceHistoricalGrantAsync(actor, snapshot, decision, candidate, membership, session, token);
        DemandAdministrative(actor, snapshot, Permissions.AccessPropose);
        RequireHistoricalBeneficiary(candidate, membership, snapshot.Directory);
        return new(decision, ScopePolicy.RequiredNodes(membership.Scope, snapshot.Directory));
    }

    private async Task FenceHistoricalGrantAsync(ActorContext actor, AuthorizationSnapshot snapshot, AuthorizationDecision decision,
        PermissionGrant candidate, Membership beneficiary, IClientSessionHandle session, CancellationToken token,
        IEnumerable<PermissionGrant>? additionalGrants = null)
    {
        var grants = snapshot.Grants.Where(grant => decision.GrantIds.Contains(grant.Id)).ToArray();
        var memberships = snapshot.Memberships.Where(membership => grants.Any(grant => grant.MembershipId == membership.Id))
            .Append(beneficiary).DistinctBy(membership => membership.Id).ToArray();
        var activeNodes = grants.SelectMany(grant => ScopePolicy.RequiredNodes(grant.Scope, snapshot.Directory))
            .Concat(memberships.SelectMany(membership => ScopePolicy.RequiredNodes(membership.Scope, snapshot.Directory))).Distinct();
        var fence = new HistoricalAuthorizationFence(database, store);
        await fence.FenceAsync(actor, activeNodes, ScopePolicy.RequiredNodes(candidate.Scope, snapshot.Directory)
            .Where(node => node.Kind is ScopeNodeKind.Branch or ScopeNodeKind.Division), grants.Concat(additionalGrants ?? []), memberships, session, token);
        await fence.FenceBeneficiaryAsync(beneficiary.UserId, session, token);
    }
}
