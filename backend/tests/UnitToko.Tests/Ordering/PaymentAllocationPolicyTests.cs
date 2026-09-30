using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Ordering;
public sealed class PaymentAllocationPolicyTests
{
    private static PaymentPartyKey Party => new(new(10), new(100), new(101), new(103));
    private static ReceiptFunds Receipt => new(new(1), 3, Party, "IDR", new(10000m), new(500m), new(1500m));
    private static ImmutableArray<InvoiceFunds> Invoices => [new(new(2), 7, Party, "IDR", new(6000m), new(500m), new(500m)), new(new(3), 0, Party, "IDR", new(2000m), new(0), new(0))];
    private static PaymentAllocationInput Input => new(3, [new(new(2), 7, new(3000.01m)), new(new(3), 0, new(999.99m))]);
    [Fact]
    public void Partial_multi_invoice_allocation_preserves_unapplied_funds_and_existing_credits_and_refund_reservations() {
        var result = PaymentAllocationPolicy.Review(Receipt, Invoices, Input);
        Assert.Equal(new Money(4000), result.Total); Assert.Equal(new Money(4000), result.RemainingReceipt);
        Assert.Equal(new[] { new Money(1999.99m), new Money(1000.01m) }, result.Invoices.Select(line => line.Remaining));
        Assert.Equal(new Money(8000), PaymentAllocationPolicy.Available(Receipt)); Assert.Equal(new Money(5000), PaymentAllocationPolicy.Outstanding(Invoices[0]));
    }
    [Fact]
    public void One_receipt_cannot_overspend_even_when_each_invoice_can_accept_the_requested_amount() {
        var invoices = Invoices.Select(invoice => invoice with { Issued = new(10000) }).ToImmutableArray();
        var input = Input with { Lines = [new(new(2), 7, new(5000)), new(new(3), 0, new(3000.01m))] };
        Assert.Equal("payment_allocation_exceeds_receipt", Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.Review(Receipt, invoices, input)).Code);
    }
    [Fact]
    public void Overpayment_stays_unapplied_and_cannot_overpay_a_single_invoice() {
        var receipt = Receipt with { Allocated = new(0), ReservedForRefund = new(0) };
        var full = Input with { Lines = [new(new(2), 7, new(5000)), new(new(3), 0, new(2000))] };
        Assert.Equal(new Money(3000), PaymentAllocationPolicy.Review(receipt, Invoices, full).RemainingReceipt);
        var excess = full with { Lines = [new(new(2), 7, new(5000.01m)), full.Lines[1]] };
        Assert.Equal("payment_allocation_exceeds_invoice", Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.Review(receipt, Invoices, excess)).Code);
    }
    [Fact]
    public void Old_receipt_or_invoice_version_is_rejected_before_applying_a_new_review() {
        Assert.Equal("payment_allocation_stale", Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.Review(Receipt with { Version = 4 }, Invoices, Input)).Code);
        Assert.Equal("payment_allocation_stale", Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.Review(Receipt, [Invoices[0] with { Version = 8 }, Invoices[1]], Input)).Code);
    }
    [Fact]
    public void Another_party_channel_currency_duplicate_or_missing_invoice_is_not_substituted() {
        foreach (var invoice in new[] { Invoices[0] with { Party = Party with { BuyingOrganizationId = new(11) } }, Invoices[0] with { Party = Party with { ChannelId = new(104) } }, Invoices[0] with { Currency = "USD" } })
            Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.Review(Receipt, [invoice, Invoices[1]], Input));
        Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.Review(Receipt, [Invoices[0]], Input));
        Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.Review(Receipt, [Invoices[0], Invoices[0]], Input));
        Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.Copy(Input with { Lines = [Input.Lines[0], Input.Lines[0]] }));
    }
    [Fact]
    public void Invalid_empty_zero_and_inconsistent_balances_cannot_be_silently_clamped() {
        Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.Copy(Input with { Lines = [] }));
        Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.Copy(Input with { ExpectedReceiptVersion = -1 }));
        Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.Copy(Input with { Lines = [new(new(2), 0, new(0))] }));
        Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.Available(Receipt with { Allocated = new(9000) }));
        Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.Outstanding(Invoices[0] with { Paid = new(6000) }));
    }
}
