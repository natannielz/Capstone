using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Billing;

// A manual observation is never an OCR result or approval of received funds.
public sealed record PaymentManualReviewInput(string Binding, bool ImageReviewed, ImmutableArray<PaymentReviewField> Fields);

public static class PaymentManualReviewPolicy
{
    public static bool IsReviewableFailure(string state, string code) => state is "retry" or "dead" &&
        code is "ocr_unavailable" or "ocr_timeout" or "ocr_invalid_output";

    public static PaymentManualReviewInput Copy(PaymentManualReviewInput input) {
        if (input is null || input.Binding is not { Length: 64 } || input.Binding.Any(c => !char.IsAsciiHexDigitLower(c)) || !input.ImageReviewed)
            throw Invalid();
        return input with { Fields = PaymentReviewCorrectionPolicy.Copy(input.Fields) };
    }

    public static DomainValidationException Invalid(string code = "invalid_payment_manual_review") => new(code,
        "Review the clean original under the approved OCR exception procedure; unknown fields must stay unknown");
}
