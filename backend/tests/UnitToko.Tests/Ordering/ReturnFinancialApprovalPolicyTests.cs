using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;

namespace UnitToko.Tests.Ordering;

public sealed class ReturnFinancialApprovalPolicyTests
{
    private static PaymentPartyKey Party => new(new(10), new(100), new(101), new(103));
    private static InvoiceFunds Invoice => new(new(2), 7, Party, "IDR", new(1000m), new(600m), new(100m));
    private static ReceiptFunds Receipt => new(new(1), 3, Party, "IDR", new(1000m), new(500m), new(70m));
    private static ReturnFinancialAllocationCapacity Capacity(long allocationId = 11, decimal original = 300m, decimal previous = 0m, ReceiptFunds? receipt = null) =>
        new(new(allocationId), Invoice.InvoiceId, receipt ?? Receipt, new(original), new(previous));
    private static ReturnFinancialReleaseInput Release(long allocationId = 11, decimal amount = 100m, long version = 3) => new(new(allocationId), version, new(amount));
    private static ReturnFinancialApprovalResult Review(InvoiceFunds invoice, decimal credit, ImmutableArray<ReturnFinancialAllocationCapacity> capacities, ImmutableArray<ReturnFinancialReleaseInput> releases, string action = "refund") =>
        ReturnFinancialApprovalPolicy.Review(invoice, new(credit), new(credit), action, capacities, new(invoice.Version, releases), "credit_original_components", "reserve_released_allocations");

    [Fact]
    public void Approval_binding_normalizes_equivalent_HTTP_money_scale_but_preserves_a_changed_cent() {
        const long allocationId = 9007199254740993L;
        var request = new BsonDocument { { "_id", 21L }, { "invoiceId", 2L }, { "credit", new BsonDecimal128(1000m) } };
        var policy = new BsonDocument { { "_id", 31L }, { "revision", 1L }, { "creditRule", "credit_original_components" }, { "reservationRule", "reserve_released_allocations" }, { "fence", 0L } };
        var invoiceBefore = new BsonDocument { { "version", 7L }, { "paid", new BsonDecimal128(1000m) }, { "credited", new BsonDecimal128(0m) } };
        var receipts = new BsonArray { new BsonDocument { { "receiptId", 1L }, { "before", new BsonDocument("version", 3L) } } };
        var links = new BsonArray { new BsonDocument { { "originalAllocationId", allocationId }, { "receiptId", 1L } } };
        string Binding(Money amount) => ReturnFinancialApprovalBson.Binding(request, policy, invoiceBefore, receipts, links, new(7, [new(new(allocationId), 3, amount)]));
        var direct = Binding(new Money(1000m));
        Assert.Equal(direct, Binding(Money.Parse("1000.00")));
        Assert.NotEqual(direct, Binding(Money.Parse("1000.01")));
    }

    [Fact]
    public void Exact_unfunded_credit_captures_before_and_preserves_cash_and_paid_balance() {
        var result = Review(Invoice, 200m, [], [], "credit");
        Assert.Equal(Invoice, result.InvoiceBefore); Assert.Equal(8, result.InvoiceAfter.Version);
        Assert.Equal(new Money(600m), result.InvoiceAfter.Paid); Assert.Equal(new Money(300m), result.InvoiceAfter.Credited);
        Assert.Equal(new Money(100m), result.RemainingInvoice); Assert.Equal(new Money(200m), result.Credit);
        Assert.Equal(new Money(0m), result.ReservedRefund); Assert.Empty(result.Receipts); Assert.Empty(result.Releases);
        Assert.Equal(new Money(100m), Invoice.Credited);
    }
    [Fact]
    public void Several_original_links_group_receipts_once_and_release_into_reservation_without_exposing_cash() {
        var second = Receipt with { ReceiptId = new(9007199254740993L), Version = 5, Received = new(700m), Allocated = new(200m), ReservedForRefund = new(10m) };
        var capacities = ImmutableArray.Create(Capacity(13, 100m, 20m, second), Capacity(12, 200m, 50m), Capacity());
        var inputs = ImmutableArray.Create(Release(13, 30m, 5), Release(12, 50m), Release(11, 100m));
        var result = Review(Invoice, 200m, capacities, inputs);
        Assert.Equal(new Money(180m), result.ReservedRefund); Assert.Equal(new Money(420m), result.InvoiceAfter.Paid);
        Assert.Equal(new Money(300m), result.InvoiceAfter.Credited); Assert.Equal(new Money(280m), result.RemainingInvoice);
        Assert.Equal(new long[] { 11, 12, 13 }, result.Releases.Select(line => line.OriginalAllocationId.Value));
        Assert.Equal(new long[] { 1, 9007199254740993L }, result.Receipts.Select(row => row.Before.ReceiptId.Value));
        Assert.Equal(new Money(350m), result.Receipts[0].After.Allocated); Assert.Equal(new Money(220m), result.Receipts[0].After.ReservedForRefund);
        Assert.Equal(4, result.Receipts[0].After.Version); Assert.Equal(new Money(430m), result.Receipts[0].Available);
        Assert.Equal(new Money(170m), result.Receipts[1].After.Allocated); Assert.Equal(new Money(40m), result.Receipts[1].After.ReservedForRefund); Assert.Equal(6, result.Receipts[1].After.Version);
        foreach (var row in result.Receipts) { Assert.Equal(row.Before.Received, row.After.Received); Assert.Equal(PaymentAllocationPolicy.Available(row.Before), PaymentAllocationPolicy.Available(row.After)); }
        Assert.Equal(new Money(100m), result.Releases[1].ReleasedAfter); Assert.Equal(new Money(50m), result.Releases[2].ReleasedAfter);
        var shuffled = Review(Invoice, 200m, [.. capacities.Reverse()], [.. inputs.Reverse()]);
        Assert.Equal(result.InvoiceAfter, shuffled.InvoiceAfter); Assert.Equal(result.Receipts.ToArray(), shuffled.Receipts.ToArray()); Assert.Equal(result.Releases.ToArray(), shuffled.Releases.ToArray());
    }
    [Fact]
    public void Exact_cents_and_long_identifiers_survive_maximum_money_and_full_refund_conservation() {
        var receipt = Receipt with { ReceiptId = new(long.MaxValue), Received = new(DomainValues.MaximumMoney), Allocated = new(DomainValues.MaximumMoney), ReservedForRefund = new(0m) };
        var invoice = Invoice with { InvoiceId = new(9007199254740993L), Issued = new(DomainValues.MaximumMoney), Paid = new(DomainValues.MaximumMoney), Credited = new(0m) };
        var capacity = Capacity(long.MaxValue, DomainValues.MaximumMoney, 0m, receipt) with { InvoiceId = invoice.InvoiceId };
        var tiny = Review(invoice, .01m, [capacity], [Release(long.MaxValue, .01m)]);
        Assert.Equal(new Money(DomainValues.MaximumMoney - .01m), tiny.InvoiceAfter.Paid); Assert.Equal(new Money(.01m), tiny.InvoiceAfter.Credited);
        Assert.Equal(new Money(.01m), tiny.Receipts[0].After.ReservedForRefund); Assert.Equal(new Money(0m), tiny.Receipts[0].Available); Assert.Equal(long.MaxValue, tiny.Releases[0].OriginalAllocationId.Value);
        var full = Review(invoice, DomainValues.MaximumMoney, [capacity], [Release(long.MaxValue, DomainValues.MaximumMoney)]);
        Assert.Equal(new Money(0m), full.InvoiceAfter.Paid); Assert.Equal(new Money(DomainValues.MaximumMoney), full.InvoiceAfter.Credited); Assert.Equal(new Money(0m), full.RemainingInvoice);
        Assert.Equal(new Money(0m), full.Receipts[0].After.Allocated); Assert.Equal(new Money(DomainValues.MaximumMoney), full.Receipts[0].After.ReservedForRefund); Assert.Equal(new Money(0m), full.Receipts[0].Available);
    }
    [Fact]
    public void Exact_maker_amount_and_explicit_supported_rules_are_required() {
        foreach (var (requested, approved) in new[] { (0m, 0m), (100m, 99.99m), (100m, 100.01m) })
            Assert.Equal("return_financial_amount_invalid", Assert.Throws<DomainValidationException>(() => ReturnFinancialApprovalPolicy.Review(Invoice, new(requested), new(approved), "credit", [], new(7, []), "credit_original_components", "reserve_released_allocations")).Code);
        foreach (var (action, creditRule, reserveRule) in new[] { ("pay", "credit_original_components", "reserve_released_allocations"), ("credit", "", "reserve_released_allocations"), ("credit", "automatic_ratio", "reserve_released_allocations"), ("credit", "credit_original_components", ""), ("refund", "credit_original_components", "payout") })
            Assert.Equal("return_financial_policy_unavailable", Assert.Throws<DomainValidationException>(() => ReturnFinancialApprovalPolicy.Review(Invoice, new(100m), new(100m), action, [], new(7, []), creditRule, reserveRule)).Code);
        Assert.Equal("return_financial_reservation_invalid", Assert.Throws<DomainValidationException>(() => Review(Invoice, 100m, [], [])).Code);
        Assert.Equal("return_financial_reservation_invalid", Assert.Throws<DomainValidationException>(() => Review(Invoice, 100m, [Capacity()], [Release()], "credit")).Code);
    }
    [Fact]
    public void Duplicate_missing_zero_or_default_selection_and_stale_versions_are_refused() {
        Assert.Throws<DomainValidationException>(() => ReturnFinancialApprovalPolicy.Copy(new(7, default)));
        Assert.Throws<DomainValidationException>(() => ReturnFinancialApprovalPolicy.Copy(new(7, [Release(), Release()])));
        Assert.Throws<DomainValidationException>(() => ReturnFinancialApprovalPolicy.Copy(new(7, [Release(amount: 0m)])));
        Assert.Throws<DomainValidationException>(() => ReturnFinancialApprovalPolicy.Copy(new(-1, [])));
        Assert.Throws<DomainValidationException>(() => Review(Invoice, 100m, [], [Release()]));
        Assert.Throws<DomainValidationException>(() => Review(Invoice, 100m, [Capacity(12)], [Release()]));
        Assert.Throws<DomainValidationException>(() => Review(Invoice, 100m, [Capacity(), Capacity()], [Release(), Release(12)]));
        Assert.Equal("return_financial_stale", Assert.Throws<DomainValidationException>(() => Review(Invoice, 100m, [Capacity()], [Release(version: 2)])).Code);
        Assert.Equal("return_financial_stale", Assert.Throws<DomainValidationException>(() => ReturnFinancialApprovalPolicy.Review(Invoice, new(100m), new(100m), "credit", [], new(6, []), "credit_original_components", "reserve_released_allocations")).Code);
    }
    [Fact]
    public void Per_link_release_cannot_use_another_invoice_party_currency_or_inconsistent_receipt_capture() {
        foreach (var capacity in new[] { Capacity() with { InvoiceId = new(3) }, Capacity(receipt: Receipt with { Party = Party with { BuyingOrganizationId = new(11) } }),
            Capacity(receipt: Receipt with { Party = Party with { ChannelId = new(104) } }), Capacity(receipt: Receipt with { Currency = "USD" }) })
            Assert.Throws<DomainValidationException>(() => Review(Invoice, 100m, [capacity], [Release()]));
        foreach (var receipt in new[] { Receipt with { Version = 4 }, Receipt with { Allocated = new(499m) }, Receipt with { ReservedForRefund = new(69m) }, Receipt with { Received = new(999m) } })
            Assert.Equal("return_financial_balance_unavailable", Assert.Throws<DomainValidationException>(() => Review(Invoice, 200m,
                [Capacity(), Capacity(12, receipt: receipt)], [Release(), Release(12, version: receipt.Version)])).Code);
    }
    [Fact]
    public void Per_original_and_aggregate_credit_receipt_and_invoice_bounds_are_never_clamped() {
        Assert.Equal("return_financial_release_exceeds_allocation", Assert.Throws<DomainValidationException>(() => Review(Invoice, 100m, [Capacity(original: 100m, previous: 50.01m)], [Release(amount: 50m)])).Code);
        Assert.Equal("return_financial_release_exceeds_allocation", Assert.Throws<DomainValidationException>(() => Review(Invoice, 100m, [Capacity(original: 100m, previous: 100.01m)], [Release()])).Code);
        Assert.Equal("return_financial_release_exceeds_credit", Assert.Throws<DomainValidationException>(() => Review(Invoice, 100m, [Capacity(), Capacity(12)], [Release(), Release(12, .01m)])).Code);
        Assert.Equal("return_financial_release_exceeds_credit", Assert.Throws<DomainValidationException>(() => Review(Invoice with { Paid = new(99.99m) }, 100m, [Capacity()], [Release()])).Code);
        var receipt = Receipt with { Allocated = new(99.99m) };
        Assert.Equal("return_financial_release_exceeds_receipt", Assert.Throws<DomainValidationException>(() => Review(Invoice, 100m, [Capacity(receipt: receipt)], [Release()])).Code);
        Assert.Equal("return_financial_credit_exceeds_invoice", Assert.Throws<DomainValidationException>(() => Review(Invoice, 300.01m, [], [], "credit")).Code);
        Assert.Equal("return_financial_credit_exceeds_invoice", Assert.Throws<DomainValidationException>(() => Review(Invoice, 401m, [Capacity()], [Release()])).Code);
        Assert.Equal("return_financial_credit_exceeds_invoice", Assert.Throws<DomainValidationException>(() => Review(Invoice, 900.01m, [Capacity()], [Release()])).Code);
    }
    [Fact]
    public void Counter_exhaustion_is_refused_before_returning_an_unreadable_balance() {
        foreach (var version in new[] { -1L, long.MaxValue - 2, long.MaxValue - 1, long.MaxValue }) {
            Assert.Equal("return_financial_counter_exhausted", Assert.Throws<DomainValidationException>(() => ReturnFinancialApprovalPolicy.Review(Invoice with { Version = version }, new(100m), new(100m), "credit", [], new(7, []), "credit_original_components", "reserve_released_allocations")).Code);
            Assert.Equal("return_financial_counter_exhausted", Assert.Throws<DomainValidationException>(() => Review(Invoice, 100m, [Capacity(receipt: Receipt with { Version = version })], [Release()])).Code);
        }
        var receipt = Receipt with { Version = long.MaxValue - 3 }; var invoice = Invoice with { Version = long.MaxValue - 3 };
        var result = Review(invoice, 100m, [Capacity(receipt: receipt)], [Release(version: receipt.Version)]);
        Assert.Equal(long.MaxValue - 2, result.InvoiceAfter.Version); Assert.Equal(long.MaxValue - 2, result.Receipts[0].After.Version);
        Assert.Equal(new Money(300m), PaymentAllocationPolicy.Outstanding(result.InvoiceAfter)); Assert.Equal(new Money(430m), PaymentAllocationPolicy.Available(result.Receipts[0].After));
    }
}
