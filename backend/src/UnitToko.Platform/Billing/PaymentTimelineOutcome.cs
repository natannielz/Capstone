using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Platform.Billing;

// These are recorded invoice events, never an order-level settlement calculation.
public sealed record PaymentTimelineInvoiceEvent(LongId InvoiceId, LongId EventId, string Kind,
    DateTimeOffset RecordedAt, LongId? OriginalAllocationId, LongId ActorAccountId);

public sealed record PaymentTimelineOutcome(LongId OrderId, LongId DeclarationId, string DeclarationRevision,
    DateTimeOffset ObservedAt, string DeclarationState, LongId? SubmissionId, DateTimeOffset? SubmittedAt,
    LongId? ReviewId, string ReviewRevision, string ReviewState, DateTimeOffset? ReviewRecordedAt,
    string? PublicExplanation, LongId? CurrentAssigneeAccountId, LongId? ReviewActorAccountId,
    LongId? VerificationId, DateTimeOffset? VerifiedAt, LongId? VerificationActorAccountId,
    ImmutableArray<PaymentTimelineInvoiceEvent> InvoiceEvents);
