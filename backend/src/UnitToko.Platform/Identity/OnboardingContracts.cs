using System.Collections.Immutable;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Identity;

public sealed record InvitationResult(long Id, long UserId, long Version, string Status);
public sealed record InvitationSummary(long Id, long UserId, string IntendedEmail, GrantScope Scope,
    InvitationRoleSnapshot? Role, long RequestedBy, long? ApprovedBy, string Reason, DateTimeOffset ExpiresAt,
    DateTimeOffset? AccessExpiresAt, long Version, string Status, ImmutableArray<string> AllowedActions);
public sealed record InvitationPage(long Total, ImmutableArray<InvitationSummary> Items);
/// <summary>Only server-held OIDC transaction state supplies this binding; never callback query claims.</summary>
public sealed record InvitationRedemptionBinding(long InvitationId, string SecretHash);
public sealed record AccountProfile(long UserId, string? DisplayName, string VerifiedEmail, DateTimeOffset VerifiedAt,
    string Status, long SecurityVersion, long GrantVersion);
public sealed record AccountDirectoryPage(long Total, ImmutableArray<AccountProfile> Items);
public enum AccountRecoveryKind { Password, Factor }
public sealed record RecoveryResult(long Id, long UserId, long SecurityVersion, string Status);
