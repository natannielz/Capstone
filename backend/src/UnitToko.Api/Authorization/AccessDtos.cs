using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Api.Authorization;

public sealed record AccessScopeDto(
    [property: JsonRequired] GrantScopeKind Kind,
    LongId? OrganizationId = null, LongId? BranchId = null, LongId? DivisionId = null,
    LongId? ShopId = null, LongId? WarehouseId = null, LongId? ChannelId = null)
{
    public GrantScope ToDomain() => new(Kind, OrganizationId?.Value, BranchId?.Value,
        DivisionId?.Value, ShopId?.Value, WarehouseId?.Value, ChannelId?.Value);
    public static AccessScopeDto From(GrantScope scope) => new(scope.Kind, Id(scope.OrganizationId),
        Id(scope.BranchId), Id(scope.DivisionId), Id(scope.ShopId), Id(scope.WarehouseId), Id(scope.ChannelId));
    private static LongId? Id(long? id) => id.HasValue ? new LongId(id.Value) : null;
}

public sealed record GrantRequestDto(
    [property: JsonRequired] LongId UserId,
    [property: JsonRequired] LongId MembershipId,
    [property: JsonRequired] string TemplateId,
    [property: JsonRequired] string[] FinancePacks,
    [property: JsonRequired] string[] AdditionalPermissions,
    [property: JsonRequired] AccessScopeDto Scope,
    [property: JsonRequired] DateTimeOffset EffectiveAt,
    DateTimeOffset? ExpiresAt = null, string[]? DelegablePermissions = null)
{
    public GrantChangeRequest ToDomain()
    {
        if (UserId.Value <= 0 || MembershipId.Value <= 0 || string.IsNullOrWhiteSpace(TemplateId) ||
            Scope is null || FinancePacks is null || AdditionalPermissions is null || EffectiveAt == default ||
            FinancePacks.Length > 4 || AdditionalPermissions.Length > Permissions.Catalog.Count ||
            (DelegablePermissions?.Length ?? 0) > Permissions.Catalog.Count ||
            FinancePacks.Concat(AdditionalPermissions).Concat(DelegablePermissions ?? []).Any(string.IsNullOrWhiteSpace))
            throw new DomainValidationException("invalid_grant", "A complete grant proposal is required");
        return new(UserId.Value, MembershipId.Value, TemplateId, [.. FinancePacks], [.. AdditionalPermissions],
            Scope.ToDomain(), EffectiveAt, ExpiresAt, [.. DelegablePermissions ?? []]);
    }
}

public sealed record MembershipRequestDto(
    [property: JsonRequired] LongId UserId,
    [property: JsonRequired] AccessScopeDto Scope,
    [property: JsonRequired] DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt = null)
{
    public MembershipChangeRequest ToDomain()
    {
        if (UserId.Value <= 0 || Scope is null || EffectiveAt == default)
            throw new DomainValidationException("invalid_membership", "A complete membership proposal is required");
        return new(UserId.Value, Scope.ToDomain(), EffectiveAt, ExpiresAt);
    }
}

public sealed record GrantProposalDto([property: JsonRequired] GrantRequestDto Grant,
    [property: JsonRequired] string Reason);
public sealed record MembershipProposalDto([property: JsonRequired] MembershipRequestDto Membership,
    [property: JsonRequired] string Reason);
public sealed record AccessReasonDto([property: JsonRequired] string Reason);
public sealed record AccessRoleDto(string Id, string Label, int Version, string[] Permissions, string[]? SuggestedPermissions = null);
public sealed record AccessPackDto(string Id, string Label, string[] Permissions);
public sealed record AccessPermissionDto(string Id, string Label, GrantScopeKind[] AllowedScopes, bool RequiresFreshMfa);
public sealed record AccessCatalogDto(AccessRoleDto[] Roles, AccessPackDto[] FinancePacks,
    AccessPermissionDto[] Permissions, string[] AllowedActions);
public sealed record AccessNodeDto(LongId Id, ScopeNodeKind Kind, LongId? ParentId, string Status, string Version);
public sealed record CurrentAccessDto(LongId UserId, string GrantVersion, string Status,
    AccessMembershipDto[] Memberships, AccessGrantDto[] Grants, AccessNodeDto[] Scopes, string[] AllowedActions);
public sealed record AccessMembershipDto(LongId Id, AccessScopeDto Scope, string Status, string Version, string[] AllowedActions,
    DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt);
public sealed record AccessGrantDto(LongId Id, LongId MembershipId, string TemplateId, int TemplateVersion,
    string[] Permissions, AccessScopeDto Scope, string Status, string Version, string[] AllowedActions,
    string[] DelegablePermissions, DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt);
public sealed record AccessSubjectDto(LongId UserId, string Status, string GrantVersion,
    AccessMembershipDto[] Memberships, AccessGrantDto[] Grants);
public sealed record AccessPreviewDto(LongId UserId, LongId MembershipId, string TemplateId, int TemplateVersion,
    string[] Permissions, string[] ExistingPermissions, AccessScopeDto Scope, bool RequiresIndependentApproval,
    string[] DelegablePermissions, DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt);
public sealed record AccessProposalResultDto(LongId Id, string Kind, LongId SubjectId, string Status, string Version);
public sealed record AccessProposalDto(LongId Id, string Kind, LongId SubjectId, AccessScopeDto Scope,
    LongId RequestedBy, string Reason, string Status, string Version, DateTimeOffset CreatedAt,
    string? TemplateId, string[] Permissions, string[] AllowedActions, string[] DelegablePermissions,
    DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt, LongId? MembershipId, int? TemplateVersion);
public sealed record AccessChangeDto(LongId Id, LongId SubjectId, string Version, string GrantVersion, string Status);

internal static class AccessResponses
{
    internal static string Version(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static AccessPreviewDto From(GrantPreview value) => new(new(value.UserId), new(value.MembershipId),
        value.TemplateId, value.TemplateVersion, [.. value.Permissions], [.. value.ExistingPermissions],
        AccessScopeDto.From(value.Scope), value.RequiresIndependentApproval,
        [.. value.DelegablePermissions], value.EffectiveAt, value.ExpiresAt);
    internal static AccessProposalResultDto From(AccessProposalResult value) => new(new(value.Id), value.Kind,
        new(value.SubjectId), value.Status, Version(value.Version));
    internal static AccessChangeDto From(AccessChangeResult value) => new(new(value.Id), new(value.SubjectId),
        Version(value.Version), Version(value.GrantVersion), value.Status);
    internal static AccessProposalDto From(AccessProposalSummary value) => new(new(value.Id), value.Kind,
        new(value.SubjectId), AccessScopeDto.From(value.Scope), new(value.RequestedBy), value.Reason,
        value.Status, Version(value.Version), value.CreatedAt, value.TemplateId, [.. value.Permissions], [.. value.AllowedActions],
        [.. value.DelegablePermissions], value.EffectiveAt, value.ExpiresAt,
        value.MembershipId.HasValue ? new LongId(value.MembershipId.Value) : null, value.TemplateVersion);
    internal static AccessSubjectDto From(AccessSubjectSummary value) => new(new(value.UserId), value.Status,
        Version(value.GrantVersion), value.Memberships.Select(From).ToArray(), value.Grants.Select(From).ToArray());
    internal static AccessMembershipDto From(MembershipSummary value) => new(new(value.Id),
        AccessScopeDto.From(value.Scope), value.Status, Version(value.Version), [.. value.AllowedActions], value.EffectiveAt, value.ExpiresAt);
    internal static AccessGrantDto From(GrantSummary value) => new(new(value.Id), new(value.MembershipId),
        value.TemplateId, value.TemplateVersion, [.. value.Permissions], AccessScopeDto.From(value.Scope),
        value.Status, Version(value.Version), [.. value.AllowedActions], [.. value.DelegablePermissions], value.EffectiveAt, value.ExpiresAt);
}
