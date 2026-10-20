using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Platform.Fulfillment;

public sealed record CaseTimelineEvent(LongId EventId, string Kind, DateTimeOffset RecordedAt, LongId ActorAccountId);
public sealed record CaseTimelineReturn(LongId ReturnId, ImmutableArray<CaseTimelineEvent> Events);
public sealed record CaseTimelineOutcome(LongId OrderId, LongId CaseId, LongId InvoiceId,
    DateTimeOffset ObservedAt, string CaseState, LongId? DecisionId, DateTimeOffset? DecidedAt, LongId? DecisionActorAccountId,
    string? PublicExplanation, ImmutableArray<CaseTimelineReturn> Returns);
