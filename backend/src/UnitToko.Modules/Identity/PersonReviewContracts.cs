using System.Collections.Immutable;

namespace UnitToko.Modules.Identity;

public sealed record PersonReviewAccount(long UserId, string? DisplayName, string VerifiedEmail, string Status);
public sealed record PersonReviewTarget(long GroupId, long Version, string Status, ImmutableArray<PersonReviewAccount> Accounts);
public sealed record PersonReviewTargetPage(long Total, ImmutableArray<PersonReviewTarget> Items);
public sealed record PersonReviewSummary(PersonWithdrawalResult Result, PersonReviewAccount Requester, string Reason,
    long AffectedAccountCount, ImmutableArray<string> AllowedActions);
public sealed record PersonReviewPage(long Total, ImmutableArray<PersonReviewSummary> Items);
public sealed record PersonReviewDecision(PersonReviewAccount Actor, string Reason, DateTimeOffset DecidedAt);
public sealed record PersonReviewDetail(PersonReviewSummary Summary, ImmutableArray<PersonReviewAccount> Accounts,
    PersonReviewDecision? Decision);
public sealed record PersonReviewPreview(string Reference, string Kind, DateTimeOffset ExpiresAt,
    PersonReviewTarget Target, PersonReviewSummary? Proposal);
