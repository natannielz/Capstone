using System.Globalization;
using System.Text.Json.Serialization;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Identity;

namespace UnitToko.Api.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IdentityDirectoryQueryDto([property: JsonRequired] string Purpose,
    int Offset = 0, int Limit = 25, LongId? UserId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IdentityDirectorySearchDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Term, int Offset = 0, int Limit = 25);
public sealed record IdentityDirectoryPageDto(string Total, IdentityProfileDto[] Items);
public sealed record IdentityProfileDto(LongId UserId, string? DisplayName, string VerifiedEmail,
    DateTimeOffset VerifiedAt, string Status, string SecurityVersion, string GrantVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IdentityInvitationQueryDto([property: JsonRequired] string Purpose,
    int Offset = 0, int Limit = 25);
public sealed record IdentityInvitationPageDto(string Total, IdentityInvitationDto[] Items);
public sealed record IdentityInvitationDto(LongId Id, LongId UserId, string IntendedEmail,
    AccessScopeDto Scope, IdentityInvitationRoleDto? Role, LongId RequestedBy, LongId? ApprovedBy,
    string Reason, DateTimeOffset ExpiresAt, DateTimeOffset? AccessExpiresAt, string Version,
    string Status, string[] AllowedActions);
public sealed record IdentityInvitationRoleDto(string TemplateId, int TemplateVersion,
    string[] Permissions, string[] DelegablePermissions);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IdentityInvitationRoleRequestDto([property: JsonRequired] string TemplateId,
    [property: JsonRequired] string[] FinancePacks,
    [property: JsonRequired] string[] AdditionalPermissions,
    [property: JsonRequired] string[] DelegablePermissions)
{
    public InvitationRoleRequest ToDomain()
    {
        if (string.IsNullOrWhiteSpace(TemplateId) || FinancePacks is null || AdditionalPermissions is null || DelegablePermissions is null ||
            FinancePacks.Length > 4 || AdditionalPermissions.Length > 256 || DelegablePermissions.Length > 256 ||
            FinancePacks.Concat(AdditionalPermissions).Concat(DelegablePermissions).Any(string.IsNullOrWhiteSpace))
            throw new DomainValidationException("invalid_invitation_role", "A complete role request is required");
        return new(TemplateId, [.. FinancePacks], [.. AdditionalPermissions], [.. DelegablePermissions]);
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IdentityInvitationProposalDto([property: JsonRequired] string Email,
    [property: JsonRequired] AccessScopeDto Scope, [property: JsonRequired] DateTimeOffset ExpiresAt,
    [property: JsonRequired] string Reason, DateTimeOffset? AccessExpiresAt = null,
    IdentityInvitationRoleRequestDto? Role = null)
{
    public InvitationRequest ToDomain()
    {
        if (Scope is null || ExpiresAt == default)
            throw new DomainValidationException("invalid_invitation", "A complete invitation is required");
        return new(Email, Scope.ToDomain(), ExpiresAt, Role?.ToDomain(), AccessExpiresAt);
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IdentityDecisionDto([property: JsonRequired] string Reason);
public sealed record IdentityInvitationResultDto(LongId Id, LongId UserId, string Version, string Status);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IdentityRecoveryRequestDto([property: JsonRequired] LongId UserId,
    [property: JsonRequired] string Kind, [property: JsonRequired] string Reason);
public sealed record IdentityRecoveryResultDto(LongId Id, LongId UserId, string SecurityVersion, string Status);

internal static class IdentityAdministrationResponses
{
    private static string Version(long value) => value.ToString(CultureInfo.InvariantCulture);
    public static IdentityDirectoryPageDto From(AccountDirectoryPage page) => new(Version(page.Total),
        page.Items.Select(item => new IdentityProfileDto(new(item.UserId), item.DisplayName,
            item.VerifiedEmail, item.VerifiedAt, item.Status, Version(item.SecurityVersion), Version(item.GrantVersion))).ToArray());
    public static IdentityInvitationPageDto From(InvitationPage page) => new(Version(page.Total),
        page.Items.Select(item => new IdentityInvitationDto(new(item.Id), new(item.UserId), item.IntendedEmail,
            AccessScopeDto.From(item.Scope), item.Role is null ? null : new(item.Role.TemplateId,
                item.Role.TemplateVersion, [.. item.Role.Permissions], [.. item.Role.DelegablePermissions]),
            new(item.RequestedBy), item.ApprovedBy.HasValue ? new LongId(item.ApprovedBy.Value) : null,
            item.Reason, item.ExpiresAt, item.AccessExpiresAt, Version(item.Version), item.Status,
            [.. item.AllowedActions])).ToArray());
    public static IdentityInvitationResultDto From(InvitationResult result) =>
        new(new(result.Id), new(result.UserId), Version(result.Version), result.Status);
    public static IdentityRecoveryResultDto From(RecoveryResult result) =>
        new(new(result.Id), new(result.UserId), Version(result.SecurityVersion), result.Status);
}
