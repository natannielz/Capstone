using System.Collections.Immutable;
using System.Security.Cryptography;
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
    public async Task<InvitationResult> ProposeAsync(ActorContext actor, InvitationRequest request, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        request = request with { Email = OnboardingPolicy.NormalizeEmail(request.Email) };
        var invitationId = (await ids.NextAsync("accountInvitations", cancellationToken)).Value;
        var userId = (await ids.NextAsync("identities", cancellationToken)).Value;
        var membershipId = (await ids.NextAsync("memberships", cancellationToken)).Value;
        long? grantId = request.Role is null ? null : (await ids.NextAsync("authorizationGrants", cancellationToken)).Value;
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        var result = await store.ExecuteCommandAsync(actor, "identity.invitation.propose", metadata, request,
            (snapshot, _, _) => Task.FromResult(new CommandAuthorization(Combine(
                AdministrativeDecision(actor, snapshot, Permissions.InviteManage), AdministrativeDecision(actor, snapshot, Permissions.MembershipPropose),
                request.Role is null ? new(true, "allowed", []) : AdministrativeDecision(actor, snapshot, Permissions.AccessPropose)), [])),
            async (session, token) =>
            {
                var valid = OnboardingPolicy.Validate(request, clock.UtcNow);
                var snapshot = await store.LoadAsync(actor, session, token);
                if (!ScopePolicy.IsScopeActive(valid.Scope, snapshot.Directory)) throw new AuthorizationDeniedException("scope_unavailable");
                await store.FenceAsync(actor, ScopePolicy.RequiredNodes(valid.Scope, snapshot.Directory), session, token);
                var identity = await FindAsync("identities", actor.UserId, session, token);
                var invitation = new BsonDocument
                {
                    { "_id", invitationId }, { "schemaVersion", 1 }, { "userId", userId }, { "membershipId", membershipId }, { "grantId", Number(grantId) },
                    { "intendedEmail", valid.Email }, { "scope", AuthorizationBson.Scope(valid.Scope) }, { "role", RoleDocument(OnboardingPolicy.Materialize(valid.Role, valid.Scope)) },
                    { "providerIssuer", policy.TrustedIssuer }, { "providerConnectionId", policy.TrustedConnectionId },
                    { "requestedBy", actor.UserId }, { "requesterGrantVersion", actor.GrantVersion }, { "requesterSecurityVersion", SecurityVersion(identity) },
                    { "reason", metadata.Reason }, { "createdAt", clock.UtcNow.UtcDateTime }, { "expiresAt", valid.ExpiresAt.UtcDateTime },
                    { "accessExpiresAt", Date(valid.AccessExpiresAt) }, { "status", "pending" }, { "version", 1L }
                };
                await Collection("accountInvitations").InsertOneAsync(session, invitation, cancellationToken: token);
                await AuditAsync(historyId, actor.UserId, userId, "identity.invitation.proposed", metadata, valid.Scope, new(), invitation, session, token);
                return Result(invitation);
            }, cancellationToken);
        return ParseResult(result);
    }

    public async Task<InvitationResult> ApproveAsync(ActorContext actor, long invitationId, long expectedVersion, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        Revision(invitationId, expectedVersion);
        var intentId = (await ids.NextAsync("identityProviderIntents", cancellationToken)).Value;
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        BsonDocument? invitation = null; AuthorizationDecision? authority = null;
        var result = await store.ExecuteCommandAsync(actor, "identity.invitation.approve", metadata, new { invitationId, expectedVersion },
            async (snapshot, session, token) =>
            {
                invitation = await FindAsync("accountInvitations", invitationId, session, token);
                if (invitation["providerIssuer"] != policy.TrustedIssuer || invitation["providerConnectionId"] != policy.TrustedConnectionId)
                    throw new AuthorizationDeniedException("invitation_provider_changed");
                authority = Approval(actor, snapshot, invitation);
                return new(authority, ScopePolicy.RequiredNodes(AuthorizationBson.Scope(invitation["scope"].AsBsonDocument), snapshot.Directory));
            }, async (session, token) =>
            {
                Pending(invitation!, expectedVersion, "pending");
                if (At(invitation!, "expiresAt") <= clock.UtcNow) throw new AuthorizationDeniedException("invitation_unavailable");
                await RejectKnownBeneficiaryAsync(actor.UserId, invitation!["intendedEmail"].AsString, session, token);
                var identity = await FindAsync("identities", actor.UserId, session, token);
                var payload = new BsonDocument { { "invitationId", invitationId }, { "secret", secret }, { "email", invitation["intendedEmail"] } };
                await Collection("identityProviderIntents").InsertOneAsync(session, new BsonDocument
                {
                    { "_id", intentId }, { "schemaVersion", 1 }, { "kind", "invitation" }, { "userId", invitation["userId"] }, { "invitationId", invitationId },
                    { "requestedBy", invitation["requestedBy"] }, { "approvedBy", actor.UserId }, { "securityVersion", 1L }, { "status", "blocked" },
                    { "reason", metadata.Reason }, { "createdAt", clock.UtcNow.UtcDateTime }, { "protectedPayload", protector.Protect("invitation-delivery", payload.ToJson()) }, { "version", 1L }
                }, cancellationToken: token);
                await MongoUnitOfWork.RequireVersionAsync(Collection("accountInvitations"), session, F.Eq("_id", invitationId) & F.Eq("status", "pending"), expectedVersion,
                    Builders<BsonDocument>.Update.Set("status", "approved").Set("approvedBy", actor.UserId).Set("approvedAt", clock.UtcNow.UtcDateTime)
                        .Set("approvedFactorAt", actor.AuthenticatedAt.UtcDateTime).Set("approverGrantVersion", actor.GrantVersion).Set("approverSecurityVersion", SecurityVersion(identity))
                        .Set("authorityGrantIds", new BsonArray(authority!.GrantIds)).Set("secretHash", SecretHash(secret)).Set("providerIntentId", intentId), token);
                var after = await FindAsync("accountInvitations", invitationId, session, token);
                // Hashes and encrypted delivery payloads are deliberately absent from audit snapshots.
                var safeAfter = after.DeepClone().AsBsonDocument; safeAfter.Remove("secretHash");
                await AuditAsync(historyId, actor.UserId, invitation["userId"].AsInt64, "identity.invitation.approved", metadata,
                    AuthorizationBson.Scope(invitation["scope"].AsBsonDocument), invitation, safeAfter, session, token);
                return Result(after);
            }, cancellationToken);
        return ParseResult(result);
    }

    private AuthorizationDecision Approval(ActorContext actor, AuthorizationSnapshot snapshot, BsonDocument invitation)
    {
        var scope = AuthorizationBson.Scope(invitation["scope"].AsBsonDocument);
        var membership = new Membership(invitation["membershipId"].AsInt64, invitation["userId"].AsInt64, scope, AuthorizationStatus.Pending,
            actor.UserId, clock.UtcNow, OptionalAt(invitation, "accessExpiresAt"));
        var membershipDecision = AccessGrantPolicy.CanApproveMembership(actor, snapshot, membership, invitation["requestedBy"].AsInt64, clock.UtcNow);
        var role = Role(invitation);
        if (role is null) return membershipDecision;
        var grant = new PermissionGrant(invitation["grantId"].AsInt64, membership.UserId, membership.Id, role.TemplateId, role.TemplateVersion, role.Permissions,
            scope, AuthorizationStatus.Pending, invitation["requestedBy"].AsInt64, actor.UserId, invitation["reason"].AsString, clock.UtcNow,
            OptionalAt(invitation, "accessExpiresAt"), DelegablePermissions: role.DelegablePermissions);
        return Combine(membershipDecision, AccessGrantPolicy.CanApprove(actor, snapshot, grant, clock.UtcNow));
    }
    private async Task RejectKnownBeneficiaryAsync(long actorId, string intendedEmail, IClientSessionHandle session, CancellationToken token)
    {
        var profile = await Collection("accountProfiles").Find(session, F.Eq("_id", actorId)).FirstOrDefaultAsync(token);
        if (profile is not null && profile["verifiedEmail"].AsString == intendedEmail) throw new AuthorizationDeniedException("independent_actor_required");
    }
}
