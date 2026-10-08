using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Authorization;

public sealed partial class AccessAdministrationService
{
    public Task<GrantPreview> PreviewGrantAsync(ActorContext actor, GrantChangeRequest request, CancellationToken cancellationToken = default, AuthorizationReadMetadata? audit = null) =>
        AuditedReadAsync(actor, "access.grant.preview", audit, new("subjectId", request.UserId), async (snapshot, session, token) =>
        {
            DemandAdministrative(actor, snapshot, Permissions.AccessPropose);
            var candidate = Materialize(actor, request, 1, "Preview");
            await ValidateBeneficiaryAsync(candidate, snapshot.Directory, session, token);
            var current = await Collection("authorizationGrants").Find(session, F.Eq("userId", request.UserId)).ToListAsync(token);
            var existing = current.Select(AuthorizationBson.Grant).Where(grant => ScopePolicy.IsGrantActive(grant, request.UserId, clock.UtcNow) &&
                grant.MembershipId == request.MembershipId && grant.Scope == request.Scope).SelectMany(grant => grant.Permissions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            return new AuditedValue<GrantPreview>(new(request.UserId, request.MembershipId, candidate.TemplateId, candidate.TemplateVersion, candidate.Permissions, existing, request.Scope, true,
                candidate.DelegablePermissions.IsDefault ? [] : candidate.DelegablePermissions, candidate.EffectiveAt, candidate.ExpiresAt), 1, [new(GrantScopeKind.System)]);
        }, cancellationToken);

    public async Task<AccessProposalResult> ProposeGrantAsync(ActorContext actor, GrantChangeRequest request, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        AuthorizationStore.ValidateMetadata(metadata);
        var proposalId = (await ids.NextAsync("authorizationProposals", cancellationToken)).Value;
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        var candidate = Materialize(actor, request, proposalId, metadata.Reason);
        var result = await store.ExecuteCommandAsync(actor, "access.grant.propose", metadata,
            request with { DelegablePermissions = request.DelegablePermissions.IsDefault ? [] : request.DelegablePermissions },
            async (snapshot, session, token) =>
            {
                var decision = DemandAdministrative(actor, snapshot, Permissions.AccessPropose);
                await ValidateBeneficiaryAsync(candidate, snapshot.Directory, session, token);
                return new(decision, ScopePolicy.RequiredNodes(candidate.Scope, snapshot.Directory));
            }, async (session, token) =>
            {
                var proposal = Proposal(proposalId, "grant", request.UserId, request.Scope, actor.UserId, metadata.Reason, AuthorizationBson.Grant(candidate));
                await Collection("authorizationProposals").InsertOneAsync(session, proposal, cancellationToken: token);
                await AuditAsync(historyId, actor, request.UserId, "access.grant.proposed", metadata, new(), proposal, session, token);
                return ProposalResult(proposal);
            }, cancellationToken);
        return ParseProposalResult(result);
    }

    public async Task<AccessChangeResult> ApproveGrantAsync(ActorContext actor, long proposalId, long expectedVersion, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ValidateVersion(proposalId, expectedVersion);
        var grantId = (await ids.NextAsync("authorizationGrants", cancellationToken)).Value;
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        BsonDocument? proposal = null;
        PermissionGrant? candidate = null;
        var result = await store.ExecuteCommandAsync(actor, "access.grant.approve", metadata, new { proposalId, expectedVersion },
            async (snapshot, session, token) =>
            {
                proposal = await FindProposalAsync(proposalId, "grant", session, token);
                candidate = AuthorizationBson.Grant(proposal["request"].AsBsonDocument, true);
                if (candidate.TemplateId == HistoricalReadPolicy.TemplateId)
                {
                    ValidateHistoricalProposal(proposal, candidate);
                    return await AuthorizeHistoricalGrantApprovalAsync(actor, snapshot, candidate, session, token);
                }
                await ValidateBeneficiaryAsync(candidate, snapshot.Directory, session, token);
                return new(AccessGrantPolicy.CanApprove(actor, snapshot, candidate, clock.UtcNow), ScopePolicy.RequiredNodes(candidate.Scope, snapshot.Directory));
            }, async (session, token) =>
            {
                EnsurePending(proposal!, expectedVersion);
                var approved = candidate! with { Id = grantId, ApprovedBy = actor.UserId, Status = AuthorizationStatus.Active, Version = 1 };
                var document = AuthorizationBson.Grant(approved);
                await Collection("authorizationGrants").InsertOneAsync(session, document, cancellationToken: token);
                var grantVersion = await BumpIdentityAsync(approved.UserId, session, token);
                await ApproveProposalAsync(proposal!, expectedVersion, actor.UserId, grantId, session, token);
                await AuditAsync(historyId, actor, approved.UserId, "access.grant.approved", metadata, proposal!, document, session, token);
                if (candidate!.TemplateId == HistoricalReadPolicy.TemplateId)
                    await AuthorizeHistoricalGrantApprovalAsync(actor, await store.LoadAsync(actor, session, token), candidate, session, token);
                return ChangeResult(grantId, approved.UserId, 1, grantVersion, "active");
            }, cancellationToken);
        return ParseChangeResult(result);
    }

    public async Task<AccessChangeResult> RevokeGrantAsync(ActorContext actor, long grantId, long expectedVersion, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ValidateVersion(grantId, expectedVersion);
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        BsonDocument? existing = null;
        PermissionGrant? grant = null;
        var result = await store.ExecuteCommandAsync(actor, "access.grant.revoke", metadata, new { grantId, expectedVersion },
            async (snapshot, session, token) =>
            {
                existing = await FindAsync("authorizationGrants", grantId, session, token); grant = AuthorizationBson.Grant(existing);
                if (grant.TemplateId == TemporaryDelegationPolicy.TemplateId)
                    throw new DomainValidationException("delegation_owner_required", "Revoke temporary access through its delegation record");
                return AuthorizeScope(actor, snapshot, Permissions.AccessRevoke, grant.Scope);
            }, async (session, token) =>
            {
                if (grant!.Status == AuthorizationStatus.Revoked) throw new PersistenceConflictException("already_revoked", "Grant was already revoked");
                var changed = grant with { Status = AuthorizationStatus.Revoked, RevokedAt = clock.UtcNow, Version = checked(expectedVersion + 1) };
                await MongoUnitOfWork.RequireVersionAsync(Collection("authorizationGrants"), session, F.Eq("_id", grantId), expectedVersion,
                    Builders<BsonDocument>.Update.Set("status", "revoked").Set("revokedAt", clock.UtcNow.UtcDateTime), token);
                var grantVersion = await BumpIdentityAsync(grant.UserId, session, token, requireActive: false);
                await AuditAsync(historyId, actor, grant.UserId, "access.grant.revoked", metadata, existing!, AuthorizationBson.Grant(changed), session, token);
                return ChangeResult(grantId, grant.UserId, changed.Version, grantVersion, "revoked");
            }, cancellationToken);
        return ParseChangeResult(result);
    }

    private PermissionGrant Materialize(ActorContext actor, GrantChangeRequest request, long id, string reason)
    {
        ValidateEffective(request.UserId, request.EffectiveAt, request.ExpiresAt);
        if (request.MembershipId <= 0 || request.FinancePacks.IsDefault || request.AdditionalPermissions.IsDefault || request.Scope is null)
            throw new DomainValidationException("invalid_grant", "A membership, scope and explicit permission selection are required");
        var template = RoleTemplates.Materialize(request.TemplateId, request.FinancePacks, request.AdditionalPermissions);
        var delegated = (request.DelegablePermissions.IsDefault ? [] : request.DelegablePermissions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        if (template.Permissions.Length > 256 || delegated.Length > 256 || delegated.Any(permission => Permissions.Find(permission) is null) ||
            template.Permissions.Any(permission => Permissions.Find(permission) is not { } definition || !definition.AllowedScopes.Contains(request.Scope.Kind)))
            throw new DomainValidationException("invalid_grant_scope", "Requested permissions are not valid for this scope");
        var businessPermissions = template.Permissions.Any(permission => permission is not (Permissions.AccessApprove or Permissions.AccessRevoke or Permissions.MembershipApprove));
        if (businessPermissions && (request.Scope.Kind is GrantScopeKind.BuyingBranch or GrantScopeKind.BuyingDivision && !template.Permissions.Contains(Permissions.BranchPic) ||
            request.Scope.Kind == GrantScopeKind.BuyingOrganization && !template.Permissions.Contains(Permissions.OrganizationWide)))
            throw new DomainValidationException("explicit_scope_grant_required", "Branch PIC or organization-wide designation must be explicitly selected");
        return new(id, request.UserId, request.MembershipId, template.Id, template.Version, template.Permissions, request.Scope,
            AuthorizationStatus.Pending, actor.UserId, 0, reason, request.EffectiveAt, request.ExpiresAt, DelegablePermissions: delegated);
    }
    private async Task ValidateBeneficiaryAsync(PermissionGrant candidate, ScopeDirectory directory, IClientSessionHandle session, CancellationToken token)
    {
        await RequireActiveIdentityAsync(candidate.UserId, session, token);
        if (!ScopePolicy.IsScopeActive(candidate.Scope, directory)) throw new AuthorizationDeniedException("scope_unavailable");
        var membership = AuthorizationBson.Membership(await FindAsync("memberships", candidate.MembershipId, session, token));
        if (membership.UserId != candidate.UserId || membership.ApprovedBy == membership.UserId ||
            !ScopePolicy.IsEffective(membership.Status, membership.EffectiveAt, membership.ExpiresAt, membership.RevokedAt, clock.UtcNow) ||
            !ScopePolicy.IsScopeActive(membership.Scope, directory) || !ScopePolicy.ContainsScope(membership.Scope, candidate.Scope) ||
            candidate.EffectiveAt < membership.EffectiveAt || membership.ExpiresAt is { } end && (candidate.ExpiresAt is null || candidate.ExpiresAt > end))
            throw new AuthorizationDeniedException("approved_membership_required");
        ValidateEffective(candidate.UserId, candidate.EffectiveAt, candidate.ExpiresAt);
    }
}
