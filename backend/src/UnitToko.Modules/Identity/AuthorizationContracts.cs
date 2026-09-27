using System.Collections.Immutable;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Identity;

public sealed record PermissionGrant(
    long Id, long UserId, long MembershipId, string TemplateId, int TemplateVersion,
    ImmutableArray<string> Permissions, GrantScope Scope, AuthorizationStatus Status,
    long RequestedBy, long ApprovedBy, string Reason, DateTimeOffset EffectiveAt,
    DateTimeOffset? ExpiresAt = null, DateTimeOffset? RevokedAt = null, long Version = 1,
    ImmutableArray<string> DelegablePermissions = default);

public sealed record AuthorizationSnapshot(
    long UserId, long GrantVersion, AuthorizationStatus Status,
    ImmutableArray<PermissionGrant> Grants, ImmutableArray<Membership> Memberships,
    ScopeDirectory Directory);

public sealed record AssuranceRequirement(bool RequiresMfa, TimeSpan? MaximumAge = null);

/// <summary>T03 supplies provider-verified factor/authentication time, never application-cookie issue time.</summary>
public static class AssurancePolicy
{
    public static bool Satisfies(ActorContext actor, AssuranceRequirement requirement, DateTimeOffset now) =>
        (!requirement.RequiresMfa || actor.Assurance is FactorAssurance.MultiFactor or FactorAssurance.PhishingResistant) &&
        (requirement.MaximumAge is null || actor.AuthenticatedAt <= now && now - actor.AuthenticatedAt <= requirement.MaximumAge);
}

public sealed record ScopeClause(long GrantId, long MembershipId, long ActorId, GrantScope Scope,
    ImmutableArray<ScopeNode> RequiredNodes)
{
    public bool Matches(ResourceScope resource) => ScopePolicy.Matches(this, resource);
}

/// <summary>OR clauses; every clause remains intact. Filter before counts and pagination.</summary>
public sealed record AuthorizationQuery(
    string Permission, long ActorId, long GrantVersion, ImmutableArray<ScopeClause> Clauses,
    AssuranceRequirement Assurance, ScopeDirectory Directory, string? DenialCode = null)
{
    public bool IsAllowed => DenialCode is null && !Clauses.IsDefaultOrEmpty;
    public bool Matches(ResourceScope resource) => IsAllowed && ScopePolicy.IsResourceActive(resource, Directory) &&
        Clauses.Any(clause => clause.Matches(resource));
}

public sealed record AuthorizationDecision(bool Allowed, string Code, ImmutableArray<long> GrantIds)
{
    public static AuthorizationDecision Deny(string code) => new(false, code, []);
}
