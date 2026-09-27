using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Identity;

public sealed partial class IdentityOnboardingService
{
    public Task<InvitationResult> RejectAsync(ActorContext actor, long id, long expectedVersion, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default) =>
        ResolveInvitationAsync(actor, id, expectedVersion, "rejected", metadata, cancellationToken);
    public Task<InvitationResult> WithdrawAsync(ActorContext actor, long id, long expectedVersion, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default) =>
        ResolveInvitationAsync(actor, id, expectedVersion, "withdrawn", metadata, cancellationToken);
    public Task<InvitationResult> RevokeAsync(ActorContext actor, long id, long expectedVersion, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default) =>
        ResolveInvitationAsync(actor, id, expectedVersion, "revoked", metadata, cancellationToken);

    private async Task<InvitationResult> ResolveInvitationAsync(ActorContext actor, long id, long expectedVersion, string status,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken)
    {
        Revision(id, expectedVersion);
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        BsonDocument? invitation = null;
        var result = await store.ExecuteCommandAsync(actor, "identity.invitation." + status, metadata, new { id, expectedVersion },
            async (snapshot, session, token) =>
            {
                invitation = await FindAsync("accountInvitations", id, session, token);
                return new(ResolutionAuthority(actor, snapshot, invitation, status), []);
            }, async (session, token) =>
            {
                Pending(invitation!, expectedVersion, status == "revoked" ? "approved" : "pending");
                await MongoUnitOfWork.RequireVersionAsync(Collection("accountInvitations"), session, F.Eq("_id", id), expectedVersion,
                    Builders<BsonDocument>.Update.Set("status", status).Set("resolvedBy", actor.UserId)
                        .Set("resolvedAt", clock.UtcNow.UtcDateTime).Set("resolutionReason", metadata.Reason), token);
                var after = await FindAsync("accountInvitations", id, session, token);
                var beforeAudit = invitation!.DeepClone().AsBsonDocument; beforeAudit.Remove("secretHash");
                var afterAudit = after.DeepClone().AsBsonDocument; afterAudit.Remove("secretHash");
                await AuditAsync(historyId, actor.UserId, invitation["userId"].AsInt64, "identity.invitation." + status, metadata,
                    AuthorizationBson.Scope(invitation["scope"].AsBsonDocument), beforeAudit, afterAudit, session, token);
                return Result(after);
            }, cancellationToken);
        return ParseResult(result);
    }
    private AuthorizationDecision ResolutionAuthority(ActorContext actor, AuthorizationSnapshot snapshot, BsonDocument invitation, string status)
    {
        if (status == "withdrawn")
            return invitation["requestedBy"].AsInt64 == actor.UserId ? AdministrativeDecision(actor, snapshot, Permissions.InviteManage)
                : AuthorizationDecision.Deny("original_requester_required");
        if (status == "revoked")
        {
            var admin = AdministrativeDecision(actor, snapshot, Permissions.InviteManage);
            if (admin.Allowed) return admin;
        }
        if (status == "rejected" && (invitation["requestedBy"].AsInt64 == actor.UserId || invitation["userId"].AsInt64 == actor.UserId))
            return AuthorizationDecision.Deny("independent_actor_required");
        var query = ScopePolicy.Compile(actor, snapshot, status == "rejected" ? Permissions.MembershipApprove : Permissions.AccessRevoke, clock.UtcNow);
        var scope = AuthorizationBson.Scope(invitation["scope"].AsBsonDocument);
        var matches = query.Clauses.Where(clause => ScopePolicy.ContainsScope(clause.Scope, scope)).Select(clause => clause.GrantId).ToImmutableArray();
        return !query.IsAllowed || matches.IsEmpty ? AuthorizationDecision.Deny(query.DenialCode ?? "scope_denied") : new(true, "allowed", matches);
    }
}
