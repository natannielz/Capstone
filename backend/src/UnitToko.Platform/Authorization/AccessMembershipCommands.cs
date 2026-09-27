using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Authorization;

public sealed partial class AccessAdministrationService
{
    public async Task<AccessProposalResult> ProposeMembershipAsync(ActorContext actor, MembershipChangeRequest request, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ValidateEffective(request.UserId, request.EffectiveAt, request.ExpiresAt);
        var proposalId = (await ids.NextAsync("authorizationProposals", cancellationToken)).Value;
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        var payload = new BsonDocument { { "userId", request.UserId }, { "scope", AuthorizationBson.Scope(request.Scope) }, { "effectiveAt", request.EffectiveAt.UtcDateTime } };
        if (request.ExpiresAt is { } expires) payload.Add("expiresAt", expires.UtcDateTime);
        var result = await store.ExecuteCommandAsync(actor, "access.membership.propose", metadata, request,
            async (snapshot, session, token) =>
            {
                var decision = DemandAdministrative(actor, snapshot, Permissions.MembershipPropose);
                await RequireActiveIdentityAsync(request.UserId, session, token);
                if (!ScopePolicy.IsScopeActive(request.Scope, snapshot.Directory)) throw new AuthorizationDeniedException("scope_unavailable");
                return new(decision, ScopePolicy.RequiredNodes(request.Scope, snapshot.Directory));
            }, async (session, token) =>
            {
                var proposal = Proposal(proposalId, "membership", request.UserId, request.Scope, actor.UserId, metadata.Reason, payload);
                await Collection("authorizationProposals").InsertOneAsync(session, proposal, cancellationToken: token);
                await AuditAsync(historyId, actor, request.UserId, "access.membership.proposed", metadata, new(), proposal, session, token);
                return ProposalResult(proposal);
            }, cancellationToken);
        return ParseProposalResult(result);
    }

    public async Task<AccessChangeResult> ApproveMembershipAsync(ActorContext actor, long proposalId, long expectedVersion, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ValidateVersion(proposalId, expectedVersion);
        var membershipId = (await ids.NextAsync("memberships", cancellationToken)).Value;
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        BsonDocument? proposal = null;
        Membership? membership = null;
        var result = await store.ExecuteCommandAsync(actor, "access.membership.approve", metadata, new { proposalId, expectedVersion },
            async (snapshot, session, token) =>
            {
                proposal = await FindProposalAsync(proposalId, "membership", session, token);
                membership = DraftMembership(proposal, membershipId, actor.UserId);
                await RequireActiveIdentityAsync(membership.UserId, session, token);
                ValidateEffective(membership.UserId, membership.EffectiveAt, membership.ExpiresAt);
                return new(AccessGrantPolicy.CanApproveMembership(actor, snapshot, membership, proposal["requestedBy"].AsInt64, clock.UtcNow),
                    ScopePolicy.RequiredNodes(membership.Scope, snapshot.Directory));
            }, async (session, token) =>
            {
                EnsurePending(proposal!, expectedVersion);
                var document = AuthorizationBson.Membership(membership!);
                await Collection("memberships").InsertOneAsync(session, document, cancellationToken: token);
                var grantVersion = await BumpIdentityAsync(membership!.UserId, session, token);
                await ApproveProposalAsync(proposal!, expectedVersion, actor.UserId, membershipId, session, token);
                await AuditAsync(historyId, actor, membership.UserId, "access.membership.approved", metadata, proposal!, document, session, token);
                return ChangeResult(membershipId, membership.UserId, 1, grantVersion, "active");
            }, cancellationToken);
        return ParseChangeResult(result);
    }

    public async Task<AccessChangeResult> SuspendMembershipAsync(ActorContext actor, long membershipId, long expectedVersion, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ValidateVersion(membershipId, expectedVersion);
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        BsonDocument? existing = null;
        Membership? membership = null;
        var result = await store.ExecuteCommandAsync(actor, "access.membership.suspend", metadata, new { membershipId, expectedVersion },
            async (snapshot, session, token) =>
            {
                existing = await FindAsync("memberships", membershipId, session, token); membership = AuthorizationBson.Membership(existing);
                return AuthorizeScope(actor, snapshot, Permissions.AccessRevoke, membership.Scope);
            }, async (session, token) =>
            {
                if (membership!.Status != AuthorizationStatus.Active) throw new PersistenceConflictException("membership_inactive", "Membership is not active");
                await MongoUnitOfWork.RequireVersionAsync(Collection("memberships"), session, F.Eq("_id", membershipId), expectedVersion,
                    Builders<BsonDocument>.Update.Set("status", "suspended"), token);
                var grantVersion = await BumpIdentityAsync(membership.UserId, session, token, requireActive: false);
                var after = AuthorizationBson.Membership(membership with { Status = AuthorizationStatus.Suspended, Version = checked(expectedVersion + 1) });
                await AuditAsync(historyId, actor, membership.UserId, "access.membership.suspended", metadata, existing!, after, session, token);
                return ChangeResult(membershipId, membership.UserId, checked(expectedVersion + 1), grantVersion, "suspended");
            }, cancellationToken);
        return ParseChangeResult(result);
    }
    private static Membership DraftMembership(BsonDocument proposal, long membershipId, long approver)
    {
        var request = proposal["request"].AsBsonDocument;
        return new(membershipId, request["userId"].AsInt64, AuthorizationBson.Scope(request["scope"].AsBsonDocument), AuthorizationStatus.Active,
            approver, new(request["effectiveAt"].ToUniversalTime(), TimeSpan.Zero),
            request.TryGetValue("expiresAt", out var expiry) ? new DateTimeOffset(expiry.ToUniversalTime(), TimeSpan.Zero) : null);
    }
}
