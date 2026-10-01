using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Billing;

public sealed record PaymentReviewActionInput(string Binding, string Action, string? PublicExplanation = null);
public static class PaymentReviewActionPolicy
{
    public static PaymentReviewActionInput Copy(PaymentReviewActionInput input) {
        if (input is null || input.Binding is null || input.Binding.Length != 64 || input.Binding.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            input.Action is not ("claim" or "release" or "reject" or "request_replacement") ||
            input.PublicExplanation is not null && (input.Action is not ("reject" or "request_replacement") || !ReceivingAccountPolicy.Text(input.PublicExplanation, 1000))) throw Invalid();
        return new(input.Binding, input.Action, input.PublicExplanation);
    }
    public static (string State, long? AssigneeId) Apply(string state, long? assigneeId, string action, long actorId) {
        if (actorId <= 0 || state is not ("pending" or "assigned") || (state == "assigned") != (assigneeId is > 0)) throw Invalid();
        return action switch {
            "claim" when assigneeId != actorId => ("assigned", actorId),
            "release" when state == "assigned" => ("pending", null),
            "reject" => ("rejected", actorId),
            "request_replacement" => ("replacement_requested", actorId),
            _ => throw Invalid()
        };
    }
    public static DomainValidationException Invalid() => new("invalid_payment_review_action", "The review action is incomplete or no longer available.");
}
