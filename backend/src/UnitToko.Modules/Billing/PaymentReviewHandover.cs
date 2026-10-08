using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Billing;

/// <summary>A source reference, never authority to claim a task.</summary>
public sealed record PaymentReviewHandoverInput(long DelegationId, long DelegationRevision, long DelegationEventId,
    long OriginalAssigneeId, long DeclarationRevision);

public static class PaymentReviewHandoverPolicy
{
    public static PaymentReviewHandoverInput Copy(PaymentReviewHandoverInput value) {
        if (value is null || value.DelegationId <= 0 || value.DelegationRevision <= 0 || value.DelegationEventId <= 0 ||
            value.OriginalAssigneeId <= 0 || value.DeclarationRevision <= 0) throw Invalid();
        return value with { };
    }
    public static DomainValidationException Invalid() => new("invalid_payment_handover", "Refresh the current assignment and delegation before claiming the task.");
}
