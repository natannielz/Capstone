using System.Collections.Immutable;
using System.Net.Mail;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Identity;

public sealed record InvitationRoleRequest(string TemplateId, ImmutableArray<string> FinancePacks,
    ImmutableArray<string> AdditionalPermissions, ImmutableArray<string> DelegablePermissions);
public sealed record InvitationRequest(string Email, GrantScope Scope, DateTimeOffset ExpiresAt,
    InvitationRoleRequest? Role = null, DateTimeOffset? AccessExpiresAt = null);
public sealed record InvitationRoleSnapshot(string TemplateId, int TemplateVersion,
    ImmutableArray<string> Permissions, ImmutableArray<string> DelegablePermissions);

public static class OnboardingPolicy
{
    // One documented comparison policy; no provider-specific plus/dot rewriting or email-based linking.
    public static string NormalizeEmail(string email)
    {
        var value = (email ?? "").Trim();
        if (value.Length is < 3 or > 254 || value.Any(character => character > 127 || char.IsControl(character) || char.IsWhiteSpace(character)) ||
            !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value || parsed.DisplayName.Length != 0 ||
            value.Count(character => character == '@') != 1)
            throw Invalid("invalid_invitation_email", "A plain email address is required");
        return value.ToLowerInvariant();
    }

    public static InvitationRequest Validate(InvitationRequest request, DateTimeOffset now)
    {
        if (request.Scope is null || request.ExpiresAt <= now || request.ExpiresAt > now.AddDays(7) ||
            request.AccessExpiresAt is { } end && end <= request.ExpiresAt)
            throw Invalid("invalid_invitation_period", "An invitation must expire within seven days and before any access expiry");
        Materialize(request.Role, request.Scope);
        return request with { Email = NormalizeEmail(request.Email) };
    }

    public static InvitationRoleSnapshot? Materialize(InvitationRoleRequest? request, GrantScope scope)
    {
        if (request is null) return null;
        if (request.FinancePacks.IsDefault || request.AdditionalPermissions.IsDefault || request.DelegablePermissions.IsDefault ||
            request.FinancePacks.Length > 4 || request.AdditionalPermissions.Length > 256 || request.DelegablePermissions.Length > 256)
            throw Invalid("invalid_invitation_role", "A bounded explicit role request is required");
        var template = RoleTemplates.Materialize(request.TemplateId, request.FinancePacks, request.AdditionalPermissions);
        if (template.Permissions.Any(permission => Permissions.Find(permission) is not { } definition || !definition.AllowedScopes.Contains(scope.Kind)) ||
            request.DelegablePermissions.Any(permission => Permissions.Find(permission) is null))
            throw Invalid("invalid_invitation_role", "Requested permissions are incompatible with the invitation scope");
        var business = template.Permissions.Any(permission => permission is not (Permissions.AccessApprove or Permissions.AccessRevoke or Permissions.MembershipApprove));
        if (business && (scope.Kind is GrantScopeKind.BuyingBranch or GrantScopeKind.BuyingDivision && !template.Permissions.Contains(Permissions.BranchPic) ||
            scope.Kind == GrantScopeKind.BuyingOrganization && !template.Permissions.Contains(Permissions.OrganizationWide)))
            throw Invalid("explicit_scope_designation_required", "Broad business access needs its explicit reviewed designation");
        return new(template.Id, template.Version, template.Permissions, request.DelegablePermissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray());
    }

    /// <summary>Rechecks a previously authenticated approval, not a new interactive action. Its recorded
    /// factor evidence is immutable; the caller must compare both current approver versions and fence
    /// that identity. This never creates an ActorContext or invents recent factor evidence.</summary>
    public static ImmutableArray<long> RevalidateApproval(AuthorizationSnapshot snapshot, long requestedBy, long beneficiaryId,
        GrantScope scope, InvitationRoleSnapshot? role, ImmutableArray<long> approvedAuthorityIds, DateTimeOffset now)
    {
        if (snapshot.Status != AuthorizationStatus.Active || snapshot.UserId == requestedBy || snapshot.UserId == beneficiaryId ||
            approvedAuthorityIds.IsDefaultOrEmpty || !ScopePolicy.IsScopeActive(scope, snapshot.Directory) ||
            snapshot.Grants.Select(grant => grant.Id).Distinct().Count() != snapshot.Grants.Length)
            return [];
        var eligible = snapshot.Grants.Where(grant => approvedAuthorityIds.Contains(grant.Id) &&
            ScopePolicy.IsGrantActive(grant, snapshot.UserId, now) && ScopePolicy.IsScopeActive(grant.Scope, snapshot.Directory) &&
            ScopePolicy.ContainsScope(grant.Scope, scope) &&
            snapshot.Memberships.Count(membership => membership.Id == grant.MembershipId) == 1 &&
            snapshot.Memberships.Any(membership => membership.Id == grant.MembershipId && membership.UserId == snapshot.UserId &&
                membership.ApprovedBy > 0 && membership.ApprovedBy != snapshot.UserId && membership.Version > 0 &&
                ScopePolicy.IsEffective(membership.Status, membership.EffectiveAt, membership.ExpiresAt, membership.RevokedAt, now) &&
                ScopePolicy.IsScopeActive(membership.Scope, snapshot.Directory) && ScopePolicy.ContainsScope(membership.Scope, grant.Scope))).ToArray();
        var membershipIds = eligible.Where(grant => grant.Permissions.Contains(Permissions.MembershipApprove)).Select(grant => grant.Id).ToArray();
        if (membershipIds.Length == 0) return [];
        if (role is null) return membershipIds.ToImmutableArray();
        if (role.TemplateVersion != RoleTemplates.CurrentVersion || !RoleTemplates.Templates.ContainsKey(role.TemplateId) ||
            role.Permissions.Any(permission => Permissions.Find(permission) is not { } definition || !definition.AllowedScopes.Contains(scope.Kind))) return [];
        var powers = role.Permissions.Concat(role.DelegablePermissions).Distinct(StringComparer.Ordinal).ToArray();
        var grantIds = eligible.Where(grant => grant.Permissions.Contains(Permissions.AccessApprove) &&
            !grant.DelegablePermissions.IsDefault && powers.All(permission => grant.DelegablePermissions.Contains(permission))).Select(grant => grant.Id).ToArray();
        return grantIds.Length == 0 ? [] : membershipIds.Concat(grantIds).Distinct().ToImmutableArray();
    }

    public static ImmutableArray<long> RevalidateRequester(AuthorizationSnapshot snapshot, bool includesRole, DateTimeOffset now)
    {
        var required = includesRole ? new[] { Permissions.InviteManage, Permissions.MembershipPropose, Permissions.AccessPropose }
            : new[] { Permissions.InviteManage, Permissions.MembershipPropose };
        var eligible = snapshot.Grants.Where(grant => grant.Scope.Kind == GrantScopeKind.System &&
            ScopePolicy.IsGrantActive(grant, snapshot.UserId, now) && ScopePolicy.IsScopeActive(grant.Scope, snapshot.Directory) &&
            snapshot.Memberships.Count(membership => membership.Id == grant.MembershipId) == 1 &&
            snapshot.Memberships.Any(membership => membership.Id == grant.MembershipId && membership.UserId == snapshot.UserId &&
                membership.ApprovedBy > 0 && membership.ApprovedBy != snapshot.UserId && membership.Version > 0 &&
                ScopePolicy.IsEffective(membership.Status, membership.EffectiveAt, membership.ExpiresAt, membership.RevokedAt, now) &&
                ScopePolicy.IsScopeActive(membership.Scope, snapshot.Directory) && ScopePolicy.ContainsScope(membership.Scope, grant.Scope))).ToArray();
        return snapshot.Status != AuthorizationStatus.Active || snapshot.Grants.Select(grant => grant.Id).Distinct().Count() != snapshot.Grants.Length ||
            required.Any(permission => !eligible.Any(grant => grant.Permissions.Contains(permission))) ? [] :
            eligible.Where(grant => required.Any(permission => grant.Permissions.Contains(permission))).Select(grant => grant.Id).ToImmutableArray();
    }
    private static DomainValidationException Invalid(string code, string title) => new(code, title);
}
