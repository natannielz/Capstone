using System.Collections.Immutable;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Identity;

public static class AccessGrantPolicy
{
    public static AuthorizationDecision CanApprove(ActorContext actor, AuthorizationSnapshot snapshot,
        PermissionGrant proposed, DateTimeOffset now)
    {
        if (proposed.UserId <= 0 || proposed.RequestedBy <= 0 || proposed.RequestedBy == actor.UserId || proposed.UserId == actor.UserId)
            return AuthorizationDecision.Deny("independent_actor_required");
        if (proposed.Permissions.IsDefault || proposed.Permissions.Any(permission => Permissions.Find(permission) is not { } definition ||
            !definition.AllowedScopes.Contains(proposed.Scope.Kind)) || !ScopePolicy.IsScopeActive(proposed.Scope, snapshot.Directory))
            return AuthorizationDecision.Deny("invalid_grant_scope");
        var query = ScopePolicy.Compile(actor, snapshot, Permissions.AccessApprove, now);
        if (!query.IsAllowed) return AuthorizationDecision.Deny(query.DenialCode ?? "permission_denied");
        var requested = proposed.Permissions.Concat(proposed.DelegablePermissions.IsDefault ? [] : proposed.DelegablePermissions).Distinct(StringComparer.Ordinal).ToArray();
        var permitted = query.Clauses.Where(clause => ScopePolicy.ContainsScope(clause.Scope, proposed.Scope) &&
            snapshot.Grants.Any(grant => grant.Id == clause.GrantId && !grant.DelegablePermissions.IsDefault &&
                requested.All(permission => grant.DelegablePermissions.Contains(permission, StringComparer.Ordinal))))
            .Select(clause => clause.GrantId).ToImmutableArray();
        return permitted.IsEmpty ? AuthorizationDecision.Deny("delegation_denied") : new(true, "allowed", permitted);
    }

    public static AuthorizationDecision CanApproveMembership(ActorContext actor, AuthorizationSnapshot snapshot,
        Membership proposed, long requestedBy, DateTimeOffset now)
    {
        if (proposed.UserId <= 0 || requestedBy <= 0 || requestedBy == actor.UserId || proposed.UserId == actor.UserId)
            return AuthorizationDecision.Deny("independent_actor_required");
        if (!ScopePolicy.IsScopeActive(proposed.Scope, snapshot.Directory)) return AuthorizationDecision.Deny("invalid_membership_scope");
        var query = ScopePolicy.Compile(actor, snapshot, Permissions.MembershipApprove, now);
        if (!query.IsAllowed) return AuthorizationDecision.Deny(query.DenialCode ?? "permission_denied");
        var permitted = query.Clauses.Where(clause => ScopePolicy.ContainsScope(clause.Scope, proposed.Scope)).Select(clause => clause.GrantId).ToImmutableArray();
        return permitted.IsEmpty ? AuthorizationDecision.Deny("delegation_denied") : new(true, "allowed", permitted);
    }
}
