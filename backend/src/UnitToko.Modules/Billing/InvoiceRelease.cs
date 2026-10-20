using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Billing;

public enum InvoiceReleaseDecision { Approve, Reject }
public sealed record InvoiceReleaseAllocation(LongId ReceiptId, LongId SkuId, long LineRevision, Money Tax, Money Charges);
public sealed record InvoiceReleaseMappings(string Receivable, string Revenue, string Tax, string Charges);
public sealed record InvoiceReleaseInput(ImmutableArray<InvoiceReceiptSelection> Receipts, long ExpectedReleaseRevision,
    DateOnly PeriodFrom, DateOnly PeriodTo, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil,
    string TemplateApprovalReference, string NumberingApprovalReference, string EligibilityReviewReference,
    InvoiceReleaseMappings Mappings, ImmutableArray<InvoiceReleaseAllocation> Allocations);

/// <summary>Explicit reviewed release facts. The owner supplies accepted quantities,
/// historical prices, commercial terms and accounting provenance. This input cannot
/// set net amounts, stock values, a tax rate, credit capacity or an invoice number.</summary>
public static class InvoiceReleaseRules
{
    public static InvoiceReleaseInput Copy(InvoiceReleaseInput value)
    {
        if (value is null || value.Receipts.IsDefault || value.ExpectedReleaseRevision is < 0 or >= long.MaxValue - 1 ||
            value.PeriodFrom == default || value.PeriodTo < value.PeriodFrom || value.PeriodTo.DayNumber - value.PeriodFrom.DayNumber > 366 ||
            value.ValidFrom <= DateTimeOffset.MinValue || value.ValidUntil <= value.ValidFrom ||
            value.ValidFrom.UtcTicks % TimeSpan.TicksPerMillisecond != 0 || value.ValidUntil.UtcTicks % TimeSpan.TicksPerMillisecond != 0 ||
            value.Mappings is null || value.Allocations.IsDefaultOrEmpty || value.Allocations.Length > InvoicePolicy.MaximumLines)
            throw Invalid();
        var receipts = InvoicePolicy.Copy(value.Receipts);
        foreach (var text in new[] { value.TemplateApprovalReference, value.NumberingApprovalReference, value.EligibilityReviewReference }) Text(text);
        var mappings = new[] { value.Mappings.Receivable, value.Mappings.Revenue, value.Mappings.Tax, value.Mappings.Charges };
        foreach (var mapping in mappings) AccountingValidation.Code(mapping);
        if (mappings.Distinct(StringComparer.Ordinal).Count() != mappings.Length) throw Invalid();
        var allocations = value.Allocations.Select(line => {
            if (line is null || line.ReceiptId.Value <= 0 || line.SkuId.Value <= 0 || line.LineRevision is < 0 or 1 or > 103 ||
                !receipts.Any(receipt => receipt.ReceiptId == line.ReceiptId)) throw Invalid();
            return line with { Tax = new Money(line.Tax.Value), Charges = new Money(line.Charges.Value) };
        }).OrderBy(line => line.ReceiptId.Value).ThenBy(line => line.SkuId.Value).ThenBy(line => line.LineRevision).ToImmutableArray();
        if (allocations.Select(line => (line.ReceiptId, line.SkuId, line.LineRevision)).Distinct().Count() != allocations.Length) throw Invalid();
        return value with { Receipts = receipts, Mappings = value.Mappings with { }, Allocations = allocations,
            ValidFrom = value.ValidFrom.ToUniversalTime(), ValidUntil = value.ValidUntil.ToUniversalTime() };
    }
    public static string Decision(InvoiceReleaseDecision value) => value switch {
        InvoiceReleaseDecision.Approve => "approve", InvoiceReleaseDecision.Reject => "reject", _ => throw Invalid() };
    public static void Text(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() != value || value.Length > 1000 || value.Any(char.IsControl)) throw Invalid();
    }
    public static DomainValidationException Invalid() => new("invoice_release_invalid", "Use explicit reviewed release facts for the exact accepted receipt lines");
}
