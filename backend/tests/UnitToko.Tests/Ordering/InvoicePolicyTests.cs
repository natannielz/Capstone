using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Ordering;

public sealed class InvoicePolicyTests
{
    [Fact]
    public void Partial_deliveries_allocate_rounding_remainder_without_overbilling() {
        var first = InvoicePolicy.AcceptedDelta(new(10m), 3, 3, 0, new(0m), 1, "half_even");
        var second = InvoicePolicy.AcceptedDelta(new(10m), 3, 3, 1, first, 1, "half_even");
        var third = InvoicePolicy.AcceptedDelta(new(10m), 3, 3, 2, Money.Add(first, second), 1, "half_even");
        Assert.Equal(3.33m, first.Value); Assert.Equal(3.34m, second.Value); Assert.Equal(3.33m, third.Value);
        Assert.Equal(10m, Money.Add(Money.Add(first, second), third).Value);
    }
    [Theory]
    [InlineData(0, 0, 4)]
    [InlineData(2, 6.66, 1)]
    [InlineData(3, 10, 1)]
    [InlineData(-1, 0, 1)]
    public void Invalid_previous_allocation_or_excess_quantity_cannot_be_invoiced(long previous, decimal net, long accepted) =>
        Assert.Throws<DomainValidationException>(() => InvoicePolicy.AcceptedDelta(new(10m), 3, 3, previous, new(net), accepted, "half_even"));
    [Fact]
    public void Approved_half_even_and_half_up_rules_remain_distinct() {
        Assert.Equal(0.02m, InvoicePolicy.AcceptedNet(new(0.05m), 2, 1, "half_even").Value);
        Assert.Equal(0.03m, InvoicePolicy.AcceptedNet(new(0.05m), 2, 1, "half_up").Value);
    }
    [Fact]
    public void Partial_payment_and_approved_credit_do_not_hide_overdue_balance() {
        var result = ReceivablesPolicy.Calculate(new(100000m), new(40000m), new(10000m), new(2026, 9, 28), new(2026, 9, 30));
        Assert.Equal(50000m, result.Outstanding.Value); Assert.Equal("partial", result.PaymentState);
        Assert.Equal("overdue", result.Aging); Assert.Equal(2, result.DaysOverdue);
    }
    [Fact]
    public void Over_allocation_and_invalid_zero_invoice_are_refused() {
        Assert.Throws<DomainValidationException>(() => ReceivablesPolicy.Calculate(new(10m), new(8m), new(3m), new(2026, 9, 28), new(2026, 9, 30)));
        Assert.Throws<DomainValidationException>(() => ReceivablesPolicy.Calculate(new(0m), new(0m), new(0m), new(2026, 9, 28), new(2026, 9, 30)));
    }
    [Fact]
    public void Lossless_identifiers_and_unique_receipt_selection_are_required() {
        Assert.Equal(9007199254740993L, InvoicePolicy.Copy([new(new(9007199254740993L), new(9007199254740994L))])[0].ShipmentId.Value);
        Assert.Throws<DomainValidationException>(() => InvoicePolicy.Copy([new(new(1), new(2)), new(new(3), new(2))]));
    }
}
