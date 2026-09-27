using System.Collections.Immutable;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Organizations;

public sealed record OrganizationSummary(long Id, ScopeNodeKind Kind, long? ParentId, string Status, long ScopeVersion,
    string? Code, string? Name, long? ProfileVersion, ImmutableArray<string> AllowedActions);
public sealed record OrganizationPage(long Total, ImmutableArray<OrganizationSummary> Items, ImmutableArray<string> AllowedActions);
public sealed record OrganizationBefore(string? Code, string? Name, string? Status, long ScopeVersion, long ProfileVersion);
public sealed record OrganizationProposalSummary(long Id, OrganizationChangeRequest Request, OrganizationImpact Impact,
    OrganizationBefore Before, long RequestedBy, string Reason, string Status, long Version, DateTimeOffset CreatedAt,
    long? ApprovedBy, DateTimeOffset? ApprovedAt, long? DecisionBy, DateTimeOffset? DecisionAt, string? DecisionReason,
    long? ResultScopeId, ImmutableArray<string> AllowedActions);
public sealed record OrganizationProposalPage(long Total, ImmutableArray<OrganizationProposalSummary> Items, ImmutableArray<string> AllowedActions);
public sealed record OrganizationProposalResult(long Id, string Status, long Version);
public sealed record OrganizationChangeResult(long ProposalId, long? ScopeId, string Status, long ProposalVersion,
    long? ScopeVersion, long? ProfileVersion, long AffectedAccounts);
