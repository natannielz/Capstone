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
    /// <summary>The caller stores this result only in its durable, one-use OIDC challenge.
    /// A successful lookup does not consume the invitation or establish an identity.</summary>
    public async Task<InvitationRedemptionBinding> BindInvitationAsync(long invitationId, string secret, CancellationToken cancellationToken = default)
    {
        if (invitationId <= 0 || secret is null || secret.Length != 64 || secret.Any(character => !Uri.IsHexDigit(character)))
            throw new AuthorizationDeniedException("invitation_unavailable");
        var hash = SecretHash(secret);
        var available = await Collection("accountInvitations").Find(F.Eq("_id", invitationId) & F.Eq("secretHash", hash) &
            F.Eq("providerIssuer", policy.TrustedIssuer) & F.Eq("providerConnectionId", policy.TrustedConnectionId) &
            F.Eq("status", "approved") & F.Gt("expiresAt", clock.UtcNow.UtcDateTime)).AnyAsync(cancellationToken);
        return available ? new(invitationId, hash) : throw new AuthorizationDeniedException("invitation_unavailable");
    }

    /// <summary>Only validated provider proof and server challenge state may call this. The caller
    /// consumes the OIDC transaction and creates the session in this SAME Mongo transaction.</summary>
    public async Task<IdentityAdmission> AdmitAsync(IClientSessionHandle session, ProviderIdentityProof proof,
        InvitationRedemptionBinding? invitationBinding, CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction || !policy.Accepts(proof, clock.UtcNow)) throw new AuthorizationDeniedException("identity_admission_denied");
        await IdentityAdmissionHolds.RequireSubjectClearAsync(database, session, proof,
            clock, "identity_admission_denied", cancellationToken);
        var email = OnboardingPolicy.NormalizeEmail(proof.Email);
        var identity = await Collection("identities").Find(session, F.Eq("issuer", proof.Issuer) & F.Eq("subject", proof.Subject)).FirstOrDefaultAsync(cancellationToken);
        if (identity is not null)
        {
            if (invitationBinding is not null || identity["status"].AsString != "active") throw new AuthorizationDeniedException("identity_admission_denied");
            var membership = await ActiveMembershipAsync(identity["_id"].AsInt64, session, cancellationToken);
            if (membership is null) throw new AuthorizationDeniedException("approved_membership_required");
            if (!identity.Contains("securityVersion"))
            {
                var initialized = await Collection("identities").UpdateOneAsync(session,
                    F.Eq("_id", identity["_id"]) & F.Eq("status", "active") & F.Eq("grantVersion", identity["grantVersion"]) & F.Exists("securityVersion", false),
                    Builders<BsonDocument>.Update.Set("securityVersion", 1L).Inc("fence", 1L), cancellationToken: cancellationToken);
                if (initialized.ModifiedCount != 1) throw new AuthorizationDeniedException("identity_admission_denied");
                identity["securityVersion"] = 1L;
            }
            else
            {
                var currentSecurityVersion = SecurityVersion(identity);
                var fenced = await Collection("identities").UpdateOneAsync(session,
                    F.Eq("_id", identity["_id"]) & F.Eq("issuer", proof.Issuer) & F.Eq("subject", proof.Subject) &
                    F.Eq("status", "active") & F.Eq("grantVersion", identity["grantVersion"]) &
                    F.Eq("securityVersion", currentSecurityVersion),
                    Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: cancellationToken);
                if (fenced.ModifiedCount != 1) throw new AuthorizationDeniedException("identity_admission_denied");
            }
            await IdentityAdmissionHolds.RequireClearAsync(database, session, identity["_id"].AsInt64,
                "identity_admission_denied", cancellationToken);
            await UpdateProfileAsync(identity["_id"].AsInt64, proof.DisplayName, email, session, cancellationToken);
            return new(identity["_id"].AsInt64, SecurityVersion(identity), identity["grantVersion"].AsInt64, proof.Issuer, proof.Subject);
        }
        if (invitationBinding is null || string.IsNullOrWhiteSpace(proof.ChallengeBinding)) throw new AuthorizationDeniedException("approved_invitation_required");
        var invitation = await Collection("accountInvitations").Find(session, F.Eq("_id", invitationBinding.InvitationId) &
            F.Eq("secretHash", invitationBinding.SecretHash) & F.Eq("status", "approved") & F.Gt("expiresAt", clock.UtcNow.UtcDateTime)).FirstOrDefaultAsync(cancellationToken);
        if (invitation is null || invitation["intendedEmail"].AsString != email ||
            invitation["providerIssuer"] != proof.Issuer || invitation["providerConnectionId"] != proof.ConnectionId ||
            OptionalAt(invitation, "accessExpiresAt") is { } accessEnd && accessEnd <= clock.UtcNow)
            throw new AuthorizationDeniedException("invitation_unavailable");
        var userId = invitation["userId"].AsInt64;
        var scope = AuthorizationBson.Scope(invitation["scope"].AsBsonDocument);
        var role = Role(invitation);
        var requester = await AuthoritySnapshotAsync(invitation["requestedBy"].AsInt64, invitation["requesterGrantVersion"].AsInt64,
            invitation["requesterSecurityVersion"].AsInt64, session, cancellationToken);
        var approver = await AuthoritySnapshotAsync(invitation["approvedBy"].AsInt64, invitation["approverGrantVersion"].AsInt64,
            invitation["approverSecurityVersion"].AsInt64, session, cancellationToken);
        var approvedIds = invitation["authorityGrantIds"].AsBsonArray.Select(value => value.AsInt64).ToImmutableArray();
        var currentApproval = OnboardingPolicy.RevalidateApproval(approver, requester.UserId, userId, scope, role, approvedIds, clock.UtcNow);
        var currentRequester = OnboardingPolicy.RevalidateRequester(requester, role is not null, clock.UtcNow);
        if (currentApproval.IsEmpty || currentRequester.IsEmpty || requester.UserId == userId ||
            At(invitation, "approvedFactorAt") > At(invitation, "approvedAt") || At(invitation, "approvedAt") - At(invitation, "approvedFactorAt") > TimeSpan.FromMinutes(5))
            throw new AuthorizationDeniedException("invitation_approval_unavailable");
        await RejectKnownBeneficiaryAsync(approver.UserId, email, session, cancellationToken);
        await FenceAuthorityAsync(requester, invitation["requesterSecurityVersion"].AsInt64, currentRequester, [], session, cancellationToken);
        await FenceAuthorityAsync(approver, invitation["approverSecurityVersion"].AsInt64, currentApproval,
            ScopePolicy.RequiredNodes(scope, approver.Directory), session, cancellationToken);
        var consumed = await Collection("accountInvitations").UpdateOneAsync(session,
            F.Eq("_id", invitationBinding.InvitationId) & F.Eq("version", invitation["version"]) & F.Eq("status", "approved") & F.Eq("secretHash", invitationBinding.SecretHash),
            Builders<BsonDocument>.Update.Set("status", "redeemed").Set("redeemedAt", clock.UtcNow.UtcDateTime).Inc("version", 1L), cancellationToken: cancellationToken);
        if (consumed.ModifiedCount != 1) throw new AuthorizationDeniedException("invitation_unavailable");
        await Collection("identities").InsertOneAsync(session, new BsonDocument
        {
            { "_id", userId }, { "schemaVersion", 1 }, { "issuer", proof.Issuer }, { "subject", proof.Subject },
            { "status", "active" }, { "grantVersion", 1L }, { "securityVersion", 1L }, { "fence", 0L }
        }, cancellationToken: cancellationToken);
        var membershipId = invitation["membershipId"].AsInt64;
        await Collection("memberships").InsertOneAsync(session, AuthorizationBson.Membership(new Membership(membershipId, userId, scope,
            AuthorizationStatus.Active, approver.UserId, clock.UtcNow, OptionalAt(invitation, "accessExpiresAt"))), cancellationToken: cancellationToken);
        if (role is not null)
            await Collection("authorizationGrants").InsertOneAsync(session, AuthorizationBson.Grant(new PermissionGrant(invitation["grantId"].AsInt64, userId, membershipId,
                role.TemplateId, role.TemplateVersion, role.Permissions, scope, AuthorizationStatus.Active, requester.UserId, approver.UserId,
                invitation["reason"].AsString, clock.UtcNow, OptionalAt(invitation, "accessExpiresAt"), DelegablePermissions: role.DelegablePermissions)), cancellationToken: cancellationToken);
        await UpdateProfileAsync(userId, proof.DisplayName, email, session, cancellationToken);
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        await AuditAsync(historyId, userId, userId, "identity.invitation.redeemed", new("callback", "Approved invitation redeemed by its verified owner", "identity-" + SecretHash(proof.ChallengeBinding!)), scope,
            new BsonDocument { { "_id", invitationBinding.InvitationId }, { "version", invitation["version"] }, { "status", "approved" } },
            new BsonDocument { { "_id", invitationBinding.InvitationId }, { "version", invitation["version"].AsInt64 + 1 }, { "status", "redeemed" } }, session, cancellationToken);
        return new(userId, 1, 1, proof.Issuer, proof.Subject);
    }

    private async Task<AuthorizationSnapshot> AuthoritySnapshotAsync(long userId, long grantVersion, long securityVersion, IClientSessionHandle session, CancellationToken token)
    {
        if (!await Collection("identities").Find(session, F.Eq("_id", userId) & F.Eq("status", "active") & F.Eq("grantVersion", grantVersion) & F.Eq("securityVersion", securityVersion)).AnyAsync(token))
            throw new AuthorizationDeniedException("invitation_approval_unavailable");
        var grants = await Collection("authorizationGrants").Find(session, F.Eq("userId", userId)).ToListAsync(token);
        var memberships = await Collection("memberships").Find(session, F.Eq("userId", userId)).ToListAsync(token);
        var nodes = await Collection("authorizationScopes").Find(session, F.Empty).ToListAsync(token);
        return new(userId, grantVersion, AuthorizationStatus.Active, grants.Select(AuthorizationBson.Grant).ToImmutableArray(),
            memberships.Select(AuthorizationBson.Membership).ToImmutableArray(), new(nodes.Select(AuthorizationBson.Node).ToImmutableArray()));
    }
    private async Task FenceAuthorityAsync(AuthorizationSnapshot snapshot, long securityVersion, ImmutableArray<long> grantIds,
        IEnumerable<ScopeNode> extraNodes, IClientSessionHandle session, CancellationToken token)
    {
        var changed = await Collection("identities").UpdateOneAsync(session, F.Eq("_id", snapshot.UserId) & F.Eq("status", "active") &
            F.Eq("grantVersion", snapshot.GrantVersion) & F.Eq("securityVersion", securityVersion), Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token);
        if (changed.ModifiedCount != 1) throw new AuthorizationDeniedException("invitation_approval_unavailable");
        var nodes = snapshot.Grants.Where(grant => grantIds.Contains(grant.Id)).SelectMany(grant =>
            ScopePolicy.RequiredNodes(grant.Scope, snapshot.Directory).Concat(snapshot.Memberships.Where(membership => membership.Id == grant.MembershipId)
                .SelectMany(membership => ScopePolicy.RequiredNodes(membership.Scope, snapshot.Directory)))).Concat(extraNodes).Distinct().OrderBy(node => node.Id);
        foreach (var node in nodes)
            if ((await Collection("authorizationScopes").UpdateOneAsync(session, F.Eq("_id", node.Id) & F.Eq("version", node.Version) & F.Eq("status", "active"),
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token)).ModifiedCount != 1)
                throw new AuthorizationDeniedException("scope_unavailable");
    }
    private async Task<Membership?> ActiveMembershipAsync(long userId, IClientSessionHandle session, CancellationToken token)
    {
        var rows = await Collection("memberships").Find(session, F.Eq("userId", userId)).ToListAsync(token);
        var nodes = await Collection("authorizationScopes").Find(session, F.Empty).ToListAsync(token);
        var directory = new ScopeDirectory(nodes.Select(AuthorizationBson.Node).ToImmutableArray());
        var member = rows.Select(AuthorizationBson.Membership).FirstOrDefault(value => value.UserId == userId && value.ApprovedBy > 0 && value.ApprovedBy != userId &&
            ScopePolicy.IsEffective(value.Status, value.EffectiveAt, value.ExpiresAt, value.RevokedAt, clock.UtcNow) && ScopePolicy.IsScopeActive(value.Scope, directory));
        if (member is null) return null;
        foreach (var node in ScopePolicy.RequiredNodes(member.Scope, directory).OrderBy(node => node.Id))
            if ((await Collection("authorizationScopes").UpdateOneAsync(session, F.Eq("_id", node.Id) & F.Eq("version", node.Version) & F.Eq("status", "active"),
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token)).ModifiedCount != 1) throw new AuthorizationDeniedException("scope_unavailable");
        return member;
    }
    private Task UpdateProfileAsync(long userId, string? displayName, string email, IClientSessionHandle session, CancellationToken token) =>
        Collection("accountProfiles").UpdateOneAsync(session, F.Eq("_id", userId), Builders<BsonDocument>.Update
            .SetOnInsert("schemaVersion", 1).Set("displayName", displayName is null ? (BsonValue)BsonNull.Value : new BsonString(displayName.Trim()))
            .Set("verifiedEmail", email).Set("verifiedAt", clock.UtcNow.UtcDateTime).Set("updatedAt", clock.UtcNow.UtcDateTime).Inc("version", 1L), new() { IsUpsert = true }, token);
}
