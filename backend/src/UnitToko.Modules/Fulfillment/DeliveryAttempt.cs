using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Fulfillment;

public static class DeliveryAttemptPolicy
{
    public static readonly string[] Outcomes = ["recipient_unavailable", "address_unreachable", "delivery_refused", "transport_issue", "other"];
    public const int MaximumAttempts = 1000;
    public static void Require(long expectedSequence, string outcome, DateTimeOffset occurredAt, DateTimeOffset dispatchedAt, DateTimeOffset now, string reason) {
        if (expectedSequence is < 0 or >= MaximumAttempts || !Outcomes.Contains(outcome, StringComparer.Ordinal) ||
            occurredAt < dispatchedAt || occurredAt > now || occurredAt.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            reason is null || reason.Length is < 1 or > 1000 || reason != reason.Trim() || reason.Any(char.IsControl))
            throw new DomainValidationException("invalid_delivery_attempt", "Record the actual failed visit time, outcome and explanation against the current attempt history");
    }
}
