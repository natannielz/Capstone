using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Billing;

public sealed record InvoiceReceiptSelection(LongId ShipmentId, LongId ReceiptId);
public sealed record InvoiceLineAmounts(Money Net, Money Tax, Money Charges) {
    public Money Total => Money.Add(Money.Add(Net, Tax), Charges);
}
public static class InvoicePolicy
{
    public const int MaximumReceipts = 20, MaximumLines = 100;
    public static ImmutableArray<InvoiceReceiptSelection> Copy(IEnumerable<InvoiceReceiptSelection> values) {
        if (values is null) throw Invalid(); var copy = values.Take(MaximumReceipts + 1).ToImmutableArray();
        if (copy.Length is < 1 or > MaximumReceipts || copy.Any(value => value is null || value.ShipmentId.Value <= 0 || value.ReceiptId.Value <= 0) ||
            copy.Select(value => value.ShipmentId).Distinct().Count() != copy.Length || copy.Select(value => value.ReceiptId).Distinct().Count() != copy.Length) throw Invalid();
        return [.. copy.OrderBy(value => value.ShipmentId.Value)];
    }
    // Approved invoice rules choose the rounding mode. Packaging and price always come from the accepted order.
    public static Money AcceptedNet(Money packagePrice, int baseUnitsPerPackage, long acceptedBaseUnits, string rounding) {
        _ = new Money(packagePrice.Value);
        if (baseUnitsPerPackage is < 1 or > DomainValues.MaximumLineQuantity || acceptedBaseUnits is < 1 ||
            rounding is not ("half_even" or "half_up")) throw Invalid();
        try {
            var value = checked(packagePrice.Value * acceptedBaseUnits / baseUnitsPerPackage);
            return new(decimal.Round(value, 2, rounding == "half_even" ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero));
        } catch (OverflowException) { throw Invalid(); }
    }
    public static Money AcceptedDelta(Money packagePrice, int baseUnitsPerPackage, long orderedBaseUnits, long previouslyBilledBaseUnits,
        Money previouslyBilledNet, long acceptedBaseUnits, string rounding) {
        if (orderedBaseUnits < 1 || previouslyBilledBaseUnits < 0 || acceptedBaseUnits < 1 || previouslyBilledBaseUnits > orderedBaseUnits ||
            acceptedBaseUnits > orderedBaseUnits - previouslyBilledBaseUnits) throw Invalid("invoice_quantity_exceeded");
        var previous = previouslyBilledBaseUnits == 0 ? new Money(0m) : AcceptedNet(packagePrice, baseUnitsPerPackage, previouslyBilledBaseUnits, rounding);
        if (previous != previouslyBilledNet) throw Invalid("invoice_allocation_history_invalid");
        return new(AcceptedNet(packagePrice, baseUnitsPerPackage, checked(previouslyBilledBaseUnits + acceptedBaseUnits), rounding).Value - previous.Value);
    }
    public static DomainValidationException Invalid(string code = "invoice_policy_unavailable") => new(code, "Current approved invoice rules and eligible accepted sources are required");
}

public sealed record InvoiceBalance(Money Total, Money AllocatedPayment, Money ApprovedCredit, Money Outstanding, string PaymentState, string Aging, int DaysOverdue);
public static class ReceivablesPolicy
{
    public static InvoiceBalance Calculate(Money total, Money allocatedPayment, Money approvedCredit, DateOnly dueDate, DateOnly businessDate) {
        _ = new Money(total.Value); _ = new Money(allocatedPayment.Value); _ = new Money(approvedCredit.Value);
        AccountingCalendar.Validate(dueDate); AccountingCalendar.Validate(businessDate);
        var settled = Money.Add(allocatedPayment, approvedCredit);
        if (total.Value <= 0 || settled.Value > total.Value) throw InvoicePolicy.Invalid("invoice_balance_invalid");
        var outstanding = new Money(total.Value - settled.Value);
        var payment = outstanding.Value == 0 ? approvedCredit == total ? "credited" : "paid" : settled.Value > 0 ? "partial" : "unpaid";
        var aging = outstanding.Value == 0 ? "settled" : businessDate > dueDate ? "overdue" : businessDate == dueDate ? "due" : "not_due";
        return new(total, allocatedPayment, approvedCredit, outstanding, payment, aging, aging == "overdue" ? businessDate.DayNumber - dueDate.DayNumber : 0);
    }
}
