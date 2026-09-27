using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Authorization;

public sealed partial class AccessAdministrationService
{
    private static bool IsIdentityReceiptOperation(string operation) => operation is
        "identity.invitation.propose" or "identity.invitation.approve" or "identity.invitation.rejected" or
        "identity.invitation.withdrawn" or "identity.invitation.revoked" or "identity.recovery.request";

    private ImmutableArray<AuthorizationQuery> IdentityReceiptAuthority(ActorContext actor, AuthorizationSnapshot snapshot, string operation)
    {
        var queries = ReceiptPermissions[operation].Select(permission => ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow))
            .Where(query => query.IsAllowed).ToImmutableArray();
        if (operation == "identity.recovery.request")
        {
            // Recovery invalidates the initiating owner's session. A new verified session may
            // reconcile its own key without administrative permission, but never with stale MFA.
            if (!AssurancePolicy.Satisfies(actor, new(true, TimeSpan.FromMinutes(5)), clock.UtcNow))
                throw new AuthorizationDeniedException("fresh_factor_required");
            return queries.Where(query => query.Matches(Administrative)).ToImmutableArray();
        }
        bool System(string permission) => queries.Any(query => query.Permission == permission && query.Matches(Administrative));
        var allowed = operation switch
        {
            "identity.invitation.propose" => System(Permissions.InviteManage) && System(Permissions.MembershipPropose),
            "identity.invitation.withdrawn" => System(Permissions.InviteManage),
            "identity.invitation.approve" or "identity.invitation.rejected" => queries.Any(query => query.Permission == Permissions.MembershipApprove),
            "identity.invitation.revoked" => System(Permissions.InviteManage) || queries.Any(query => query.Permission == Permissions.AccessRevoke),
            _ => false
        };
        if (!allowed) throw new AuthorizationDeniedException("permission_denied");
        return queries;
    }

    private async Task<ImmutableArray<GrantScope>> IdentityReceiptScopesAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        string operation, BsonDocument? record, ImmutableArray<AuthorizationQuery> queries, IClientSessionHandle session, CancellationToken token)
    {
        if (record is null || IsClosedReceipt(record))
            return operation == "identity.recovery.request" ? [new(GrantScopeKind.System)] : ReceiptQueryScopes(queries);
        var result = SafeIdentityReceiptResult(operation, record["result"].AsBsonDocument);
        if (operation == "identity.recovery.request")
        {
            var intent = await FindAsync("identityProviderIntents", result.Id, session, token);
            if (OptionalLong(intent, "requestedBy") != actor.UserId || OptionalLong(intent, "userId") != result.SubjectId ||
                OptionalLong(intent, "securityVersion") != result.SecurityVersion ||
                !intent.TryGetValue("kind", out var kind) || !kind.IsString || kind.AsString is not ("passwordRecovery" or "factorRecovery") ||
                result.SubjectId != actor.UserId && !queries.Any(query => query.Permission == Permissions.RecoveryInitiate && query.Matches(Administrative)))
                throw new AuthorizationDeniedException("resource_unavailable");
            return [new(GrantScopeKind.System)];
        }
        var invitation = await FindAsync("accountInvitations", result.Id, session, token);
        if (OptionalLong(invitation, "userId") != result.SubjectId) throw new AuthorizationDeniedException("resource_unavailable");
        var scope = AuthorizationBson.Scope(invitation["scope"].AsBsonDocument);
        bool System(string permission) => queries.Any(query => query.Permission == permission && query.Matches(Administrative));
        if (operation is "identity.invitation.propose" or "identity.invitation.withdrawn")
        {
            if (OptionalLong(invitation, "requestedBy") != actor.UserId ||
                operation == "identity.invitation.propose" && !invitation["role"].IsBsonNull && !System(Permissions.AccessPropose))
                throw new AuthorizationDeniedException("resource_unavailable");
            return [new(GrantScopeKind.System)];
        }
        if (operation == "identity.invitation.revoked" && System(Permissions.InviteManage)) return [new(GrantScopeKind.System)];
        var permission = operation == "identity.invitation.revoked" ? Permissions.AccessRevoke : Permissions.MembershipApprove;
        var clauses = queries.Where(query => query.Permission == permission).SelectMany(query => query.Clauses)
            .Where(clause => ScopePolicy.ContainsScope(clause.Scope, scope)).ToImmutableArray();
        if (clauses.IsEmpty || operation is "identity.invitation.approve" or "identity.invitation.rejected" &&
            (OptionalLong(invitation, "requestedBy") == actor.UserId || result.SubjectId == actor.UserId))
            throw new AuthorizationDeniedException("resource_unavailable");
        if (operation == "identity.invitation.approve" && !invitation["role"].IsBsonNull)
        {
            var role = invitation["role"].AsBsonDocument;
            var requested = role["permissions"].AsBsonArray.Concat(role["delegablePermissions"].AsBsonArray).Select(value => value.AsString).Distinct().ToArray();
            // A single complete current delegation grant must cover the reviewed role and scope.
            // Receipt lookup does not repeat a now-completed invitation status/date transition.
            var delegation = queries.Where(query => query.Permission == Permissions.AccessApprove).SelectMany(query => query.Clauses)
                .Where(clause => ScopePolicy.ContainsScope(clause.Scope, scope) && snapshot.Grants.Any(grant => grant.Id == clause.GrantId &&
                    !grant.DelegablePermissions.IsDefault && requested.All(grant.DelegablePermissions.Contains))).ToImmutableArray();
            if (delegation.IsEmpty) throw new AuthorizationDeniedException("resource_unavailable");
            clauses = clauses.AddRange(delegation);
        }
        return clauses.Select(clause => clause.Scope).Distinct().ToImmutableArray();
    }

    private static ImmutableArray<GrantScope> ReceiptQueryScopes(ImmutableArray<AuthorizationQuery> queries) =>
        queries.SelectMany(query => query.Clauses).Select(clause => clause.Scope).Distinct().ToImmutableArray();

    private static OperationReceiptResult SafeIdentityReceiptResult(string operation, BsonDocument result)
    {
        var id = OptionalLong(result, "id");
        var userId = OptionalLong(result, "userId");
        var subjectId = OptionalLong(result, "subjectId");
        var recovery = operation == "identity.recovery.request";
        var kind = recovery ? "recovery" : "invitation";
        var expectedStatus = operation switch
        {
            "identity.invitation.propose" => "pending", "identity.invitation.approve" => "approved",
            "identity.invitation.rejected" => "rejected", "identity.invitation.withdrawn" => "withdrawn",
            "identity.invitation.revoked" => "revoked", _ => "blocked"
        };
        // Existing durable records predate canonical subjectId/kind/version fields. Accept only
        // their exact known shape; never infer successful provider delivery from a local commit.
        var version = OptionalLong(result, "version") ?? (recovery && !result.Contains("version") ? 1L : null);
        var securityVersion = OptionalLong(result, "securityVersion");
        if (id is not > 0 || userId is not > 0 || version is not > 0 ||
            result.Contains("subjectId") && subjectId != userId ||
            !result.TryGetValue("status", out var status) || status != expectedStatus ||
            result.TryGetValue("kind", out var storedKind) && storedKind != kind || recovery && securityVersion is not > 0)
            throw new PersistenceConflictException("receipt_unavailable", "The recorded outcome cannot be confirmed safely");
        return new(id.Value, userId.Value, version.Value, expectedStatus, kind, null, recovery ? securityVersion : null);
    }
}
