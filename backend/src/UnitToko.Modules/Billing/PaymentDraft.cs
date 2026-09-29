using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Billing;

// These are the recorder's declarations, never bank-confirmed facts or allocatable money.
// Invoice references express payment intent; they neither reserve nor allocate funds.
public sealed record PaymentDraftInput(ImmutableArray<LongId> InvoiceIds, Money DeclaredAmount,
    DateOnly TransferDate, string PayerName, string? BankReference, string? Note);
public static class PaymentDraftPolicy
{
    public const int MaximumInvoices = 20;
    public static PaymentDraftInput Copy(PaymentDraftInput input, DateOnly businessDate) {
        if (input is null || input.InvoiceIds.IsDefaultOrEmpty || input.InvoiceIds.Length > MaximumInvoices ||
            input.InvoiceIds.Any(id => id.Value <= 0) || input.InvoiceIds.Distinct().Count() != input.InvoiceIds.Length ||
            input.DeclaredAmount.Value <= 0 || input.TransferDate == default || input.TransferDate > businessDate) throw Invalid();
        _ = new Money(input.DeclaredAmount.Value); AccountingCalendar.Validate(businessDate); AccountingCalendar.Validate(input.TransferDate);
        Text(input.PayerName, 160); if (input.BankReference is not null) Text(input.BankReference, 160); if (input.Note is not null) Text(input.Note, 1000);
        return input with { InvoiceIds = [.. input.InvoiceIds.OrderBy(id => id.Value)] };
    }
    private static void Text(string value, int maximum) {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > maximum || value.Any(char.IsControl)) throw Invalid();
    }
    public static DomainValidationException Invalid(string code = "invalid_payment_draft") => new(code, "Use the current payment declaration workflow");
}
