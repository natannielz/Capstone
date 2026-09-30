using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Billing;

public sealed record PaymentDiscrepancy(string Field, string? Declaration, string? Image, string? Bank);
public sealed record PaymentDiscrepancyResolution(string Field, string Reason);
public sealed record PaymentVerificationInput(string Binding, string PayerVerificationReference,
    bool ImageReviewed, bool BankReviewed, bool PayerVerified, ImmutableArray<PaymentDiscrepancyResolution> Resolutions);

public static class PaymentVerificationPolicy
{
    public static PaymentVerificationInput Copy(PaymentVerificationInput input) {
        if (input is null || input.Binding is not { Length: 64 } || input.Binding.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            !ReceivingAccountPolicy.Text(input.PayerVerificationReference, 256) || !input.ImageReviewed || !input.BankReviewed || !input.PayerVerified ||
            input.Resolutions.IsDefault || input.Resolutions.Length > 8 || input.Resolutions.Any(item => item is null ||
                !ReceivingAccountPolicy.Text(item.Field, 40) || !ReceivingAccountPolicy.Text(item.Reason, 1000)) ||
            input.Resolutions.Select(item => item.Field).Distinct(StringComparer.Ordinal).Count() != input.Resolutions.Length) throw Invalid();
        return input with { Resolutions = [.. input.Resolutions.OrderBy(item => item.Field, StringComparer.Ordinal)] };
    }
    public static void RequireExactFunds(PaymentDraftInput declaration, Money received, ImmutableArray<PaymentReviewField> fields, string account) {
        fields = PaymentReviewCorrectionPolicy.Copy(fields);
        string? Field(string key) => fields.Single(item => item.Field == key).Value;
        if (declaration.DeclaredAmount != received || Field("amount") != received.ToString() || Field("currency") != "IDR" || Field("account") != account)
            throw Invalid("payment_verification_facts_mismatch");
    }
    public static ImmutableArray<PaymentDiscrepancy> Discrepancies(PaymentDraftInput declaration,
        ImmutableArray<PaymentReviewField> fields, string bookedDate, string holder, string bankCode, string? bankReference) {
        fields = PaymentReviewCorrectionPolicy.Copy(fields);
        string? Field(string key) => fields.Single(item => item.Field == key).Value;
        var result = ImmutableArray.CreateBuilder<PaymentDiscrepancy>();
        void Compare(string key, string? declared, string? image, string? bank) {
            if (image is null || bank is null || image != bank || declared is not null && image != declared)
                result.Add(new(key, declared, image, bank));
        }
        Compare("transfer_date", declaration.TransferDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Field("transfer_date"), bookedDate);
        Compare("recipient", null, Field("recipient"), holder);
        Compare("bank", null, Field("bank"), bankCode);
        Compare("reference", declaration.BankReference, Field("reference"), bankReference);
        // The import records a booking date, not a verified transfer time or timezone.
        result.Add(new("transfer_time", null, Field("transfer_time"), null));
        return result.ToImmutable();
    }
    public static void RequireResolutions(ImmutableArray<PaymentDiscrepancy> discrepancies, PaymentVerificationInput input) {
        input = Copy(input);
        if (!discrepancies.Select(item => item.Field).Order(StringComparer.Ordinal)
            .SequenceEqual(input.Resolutions.Select(item => item.Field))) throw Invalid("payment_verification_resolutions_required");
    }
    public static DomainValidationException Invalid(string code = "invalid_payment_verification") => new(code,
        "Review the exact submitted image, confirmed transfer, payer authority and every discrepancy");
}
