using System.Collections.Immutable;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Authorization;

public sealed record AuthorizationCommandMetadata(string IdempotencyKey, string Reason, string CorrelationId);
public sealed record GrantChangeRequest(long UserId, long MembershipId, string TemplateId,
    ImmutableArray<string> FinancePacks, ImmutableArray<string> AdditionalPermissions, GrantScope Scope,
    DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt = null, ImmutableArray<string> DelegablePermissions = default);
public sealed record HistoricalGrantChangeRequest(long UserId, long MembershipId, ImmutableArray<string> Permissions,
    GrantScope Scope, DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt = null);
public sealed record MembershipChangeRequest(long UserId, GrantScope Scope, DateTimeOffset EffectiveAt,
    DateTimeOffset? ExpiresAt = null);
public sealed record GrantPreview(long UserId, long MembershipId, string TemplateId, int TemplateVersion,
    ImmutableArray<string> Permissions, ImmutableArray<string> ExistingPermissions, GrantScope Scope,
    bool RequiresIndependentApproval, ImmutableArray<string> DelegablePermissions, DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt);
public sealed record AccessProposalResult(long Id, string Kind, long SubjectId, string Status, long Version);
public sealed record AccessProposalSummary(long Id, string Kind, long SubjectId, GrantScope Scope,
    long RequestedBy, string Reason, string Status, long Version, DateTimeOffset CreatedAt,
    string? TemplateId, ImmutableArray<string> Permissions, ImmutableArray<string> AllowedActions,
    ImmutableArray<string> DelegablePermissions, DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt, long? MembershipId, int? TemplateVersion);
public sealed record AccessChangeResult(long Id, long SubjectId, long Version, long GrantVersion, string Status);
public sealed record AccessSubjectSummary(long UserId, string Status, long GrantVersion,
    ImmutableArray<MembershipSummary> Memberships, ImmutableArray<GrantSummary> Grants);
public sealed record MembershipSummary(long Id, GrantScope Scope, string Status, long Version, ImmutableArray<string> AllowedActions,
    DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt);
public sealed record GrantSummary(long Id, long MembershipId, string TemplateId, int TemplateVersion,
    ImmutableArray<string> Permissions, GrantScope Scope, string Status, long Version, ImmutableArray<string> AllowedActions,
    ImmutableArray<string> DelegablePermissions, DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt);

public sealed record AuthorizationReadMetadata(string Purpose, string CorrelationId);
public sealed record AccessHistoryPage(long Total, ImmutableArray<AccessHistoryEntry> Items);
public sealed record AccessHistoryEntry(long Id, string Operation, long ActorId, long SubjectId, GrantScope? Scope,
    DateTimeOffset CreatedAt, string Reason, long? ResourceId, long? BeforeVersion, long? AfterVersion, string? Status);
public sealed record OperationReceipt(string Operation, string State, OperationReceiptResult? Result, DateTimeOffset? RecordedAt);
public sealed record OperationReceiptResult(long Id, long SubjectId, long Version, string Status, string? Kind, long? GrantVersion, long? SecurityVersion = null);
