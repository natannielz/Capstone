using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Identity;

public sealed record TemporaryDelegationRequest(long SourceGrantId, long SourceVersion,
    long BeneficiaryUserId, long BeneficiaryMembershipId, GrantScope Scope,
    ImmutableArray<string> Permissions, DateTimeOffset EffectiveAt, DateTimeOffset ExpiresAt);

/// <summary>Transient evidence populated only by the runtime owner after authenticating and
/// fencing the original source. It is never read from a grant document or a client request.</summary>
public sealed record TemporaryDelegationAuthority(long DelegationId, long SourceGrantId,
    long SourceUserId, long SourceVersion, DateTimeOffset CapturedAt, DateTimeOffset Deadline);

public static class TemporaryDelegationPolicy
{
    public const string TemplateId = "temporary-delegation";
    public static void Validate(TemporaryDelegationRequest request)
    {
        if (request is null || request.SourceGrantId <= 0 || request.SourceVersion <= 0 ||
            request.BeneficiaryUserId <= 0 || request.BeneficiaryMembershipId <= 0 || request.Scope is null ||
            request.Permissions.IsDefaultOrEmpty || request.Permissions.Length > 256 ||
            request.Permissions.Distinct(StringComparer.Ordinal).Count() != request.Permissions.Length ||
            request.Permissions.Any(p => Permissions.Find(p) is not { } definition || !definition.AllowedScopes.Contains(request.Scope.Kind)) ||
            request.EffectiveAt == default || request.EffectiveAt.Offset != TimeSpan.Zero || request.ExpiresAt.Offset != TimeSpan.Zero ||
            request.ExpiresAt <= request.EffectiveAt || request.EffectiveAt.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            request.ExpiresAt.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new DomainValidationException("invalid_delegation", "Select an explicit bounded delegation and finite validity");
        // These scopes bind authority to the person's ownership/assignment. Moving that ownership
        // requires a real task owner; changing the actor ID in a scope cannot prove non-expansion.
        if (request.Scope.Kind is GrantScopeKind.BuyerOwned or GrantScopeKind.CourierAssignment)
            throw new DomainValidationException("delegation_handover_unavailable", "This scope requires a governed task handover owner");
        if (request.Scope.Kind is GrantScopeKind.BuyingBranch or GrantScopeKind.BuyingDivision &&
            !request.Permissions.Contains(Permissions.BranchPic) || request.Scope.Kind == GrantScopeKind.BuyingOrganization &&
            !request.Permissions.Contains(Permissions.OrganizationWide))
            throw new DomainValidationException("invalid_delegation", "The explicit organizational marker is required");
    }

    public static bool SourceAllows(TemporaryDelegationRequest request, PermissionGrant source,
        Membership sourceMembership, Membership beneficiary, ScopeDirectory directory, long sourceUserId, DateTimeOffset now)
    {
        Validate(request);
        return source.UserId == sourceUserId && sourceUserId != request.BeneficiaryUserId &&
            source.Id == request.SourceGrantId && source.Version == request.SourceVersion &&
            source.TemplateId != TemplateId && source.TemporaryAuthority is null &&
            ScopePolicy.IsGrantActive(source, sourceUserId, now) &&
            ScopePolicy.IsScopeActive(source.Scope, directory) && ScopePolicy.IsScopeActive(request.Scope, directory) &&
            ScopePolicy.ContainsScope(source.Scope, request.Scope) &&
            source.Permissions.Intersect(source.DelegablePermissions.IsDefault ? [] : source.DelegablePermissions, StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal).IsSupersetOf(request.Permissions) &&
            Eligible(sourceMembership, sourceUserId, source.MembershipId, source.Scope, directory, now) &&
            Eligible(beneficiary, request.BeneficiaryUserId, request.BeneficiaryMembershipId, request.Scope, directory, now) &&
            request.EffectiveAt >= source.EffectiveAt && request.EffectiveAt >= sourceMembership.EffectiveAt && request.EffectiveAt >= beneficiary.EffectiveAt &&
            request.ExpiresAt > now && (source.ExpiresAt is null || request.ExpiresAt <= source.ExpiresAt) &&
            (sourceMembership.ExpiresAt is null || request.ExpiresAt <= sourceMembership.ExpiresAt) &&
            (beneficiary.ExpiresAt is null || request.ExpiresAt <= beneficiary.ExpiresAt);
    }

    private static bool Eligible(Membership value, long user, long id, GrantScope scope, ScopeDirectory directory, DateTimeOffset now) =>
        value.Id == id && value.UserId == user && value.Version > 0 && value.ApprovedBy > 0 && value.ApprovedBy != user &&
        ScopePolicy.IsEffective(value.Status, value.EffectiveAt, value.ExpiresAt, value.RevokedAt, now) &&
        ScopePolicy.IsScopeActive(value.Scope, directory) && ScopePolicy.ContainsScope(value.Scope, scope);

    public static bool HasCurrentProof(PermissionGrant grant, DateTimeOffset now) =>
        grant.TemplateId == TemplateId && grant.TemplateVersion == 1 && grant.DelegablePermissions.IsEmpty &&
        grant.ExpiresAt is { } expiry && grant.TemporaryAuthority is { } proof && proof.DelegationId == grant.Id &&
        proof.SourceGrantId > 0 && proof.SourceVersion > 0 && proof.SourceUserId == grant.RequestedBy &&
        proof.SourceUserId != grant.UserId && proof.CapturedAt < proof.Deadline && proof.Deadline <= expiry &&
        now >= proof.CapturedAt && now < proof.Deadline;
}
