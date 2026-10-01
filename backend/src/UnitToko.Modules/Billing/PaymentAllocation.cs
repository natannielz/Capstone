using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Billing;

public sealed record PaymentPartyKey(LongId BuyingOrganizationId, LongId FulfillmentOrganizationId, LongId ShopId, LongId ChannelId);
public sealed record ReceiptFunds(LongId ReceiptId, long Version, PaymentPartyKey Party, string Currency, Money Received, Money Allocated, Money ReservedForRefund);
public sealed record InvoiceFunds(LongId InvoiceId, long Version, PaymentPartyKey Party, string Currency, Money Issued, Money Paid, Money Credited);
public sealed record PaymentAllocationLine(LongId InvoiceId, long ExpectedVersion, Money Amount);
public sealed record PaymentAllocationInput(long ExpectedReceiptVersion, ImmutableArray<PaymentAllocationLine> Lines);
public sealed record PaymentAllocationResult(Money Total, Money RemainingReceipt, ImmutableArray<InvoiceAllocationResult> Invoices);
public sealed record InvoiceAllocationResult(LongId InvoiceId, long PreviousVersion, Money Amount, Money Remaining);
public sealed record AllocationReversalCapacity(LongId InvoiceId, Money OriginallyAllocated, Money PreviouslyReversed);

/// <summary>Exact arithmetic only. The owning transaction must load and fence canonical
/// receipt/refund reservations and issued invoice balances; browser totals are never inputs.</summary>
public static class PaymentAllocationPolicy
{
    public const int MaximumInvoices = 100;
    public static PaymentAllocationInput Copy(PaymentAllocationInput input) {
        if (input is null || !Version(input.ExpectedReceiptVersion) || input.Lines.IsDefaultOrEmpty || input.Lines.Length > MaximumInvoices ||
            input.Lines.Any(line => line is null || line.InvoiceId.Value <= 0 || !Version(line.ExpectedVersion) || line.Amount.Value <= 0) ||
            input.Lines.Select(line => line.InvoiceId).Distinct().Count() != input.Lines.Length) throw Invalid();
        return input with { Lines = [.. input.Lines.OrderBy(line => line.InvoiceId.Value)] };
    }
    public static Money Available(ReceiptFunds receipt) {
        if (receipt is null || receipt.ReceiptId.Value <= 0 || !Version(receipt.Version) || !Party(receipt.Party) || receipt.Currency != "IDR" || receipt.Received.Value <= 0 ||
            receipt.Allocated.Value + receipt.ReservedForRefund.Value > receipt.Received.Value) throw Invalid("payment_balance_unavailable");
        return new(receipt.Received.Value - receipt.Allocated.Value - receipt.ReservedForRefund.Value);
    }
    public static Money Outstanding(InvoiceFunds invoice) {
        if (invoice is null || invoice.InvoiceId.Value <= 0 || !Version(invoice.Version) || !Party(invoice.Party) || invoice.Currency != "IDR" ||
            invoice.Paid.Value + invoice.Credited.Value > invoice.Issued.Value) throw Invalid("payment_balance_unavailable");
        return new(invoice.Issued.Value - invoice.Paid.Value - invoice.Credited.Value);
    }
    public static PaymentAllocationResult Review(ReceiptFunds receipt, ImmutableArray<InvoiceFunds> invoices, PaymentAllocationInput input) {
        input = Copy(input); var available = Available(receipt);
        if (input.ExpectedReceiptVersion != receipt.Version) throw Invalid("payment_allocation_stale");
        if (invoices.IsDefault || invoices.Length != input.Lines.Length || invoices.Any(invoice => invoice is null) ||
            invoices.Select(invoice => invoice.InvoiceId).Distinct().Count() != invoices.Length) throw Invalid("payment_balance_unavailable");
        var total = new Money(0); var results = ImmutableArray.CreateBuilder<InvoiceAllocationResult>();
        foreach (var line in input.Lines) {
            var invoice = invoices.SingleOrDefault(invoice => invoice.InvoiceId == line.InvoiceId) ?? throw Invalid("payment_balance_unavailable");
            var outstanding = Outstanding(invoice);
            if (invoice.Party != receipt.Party || invoice.Currency != receipt.Currency) throw Invalid("payment_allocation_party_mismatch");
            if (invoice.Version != line.ExpectedVersion) throw Invalid("payment_allocation_stale");
            if (line.Amount.Value > outstanding.Value) throw Invalid("payment_allocation_exceeds_invoice");
            total = Money.Add(total, line.Amount);
            results.Add(new(invoice.InvoiceId, invoice.Version, line.Amount, new(outstanding.Value - line.Amount.Value)));
        }
        if (total.Value > available.Value) throw Invalid("payment_allocation_exceeds_receipt");
        return new(total, new(available.Value - total.Value), results.ToImmutable());
    }
    public static PaymentAllocationResult ReviewReversal(ReceiptFunds receipt, ImmutableArray<InvoiceFunds> invoices,
        ImmutableArray<AllocationReversalCapacity> capacities, PaymentAllocationInput input) {
        input = Copy(input); var available = Available(receipt);
        if (receipt.Version != input.ExpectedReceiptVersion) throw Invalid("payment_allocation_stale");
        if (invoices.IsDefault || capacities.IsDefault || invoices.Length != input.Lines.Length || capacities.Length != input.Lines.Length ||
            invoices.Any(invoice => invoice is null) || capacities.Any(capacity => capacity is null) ||
            invoices.Select(invoice => invoice.InvoiceId).Distinct().Count() != invoices.Length || capacities.Select(capacity => capacity.InvoiceId).Distinct().Count() != capacities.Length) throw Invalid("payment_balance_unavailable");
        var total = new Money(0); var results = ImmutableArray.CreateBuilder<InvoiceAllocationResult>();
        foreach (var line in input.Lines) {
            var invoice = invoices.SingleOrDefault(value => value.InvoiceId == line.InvoiceId) ?? throw Invalid("payment_balance_unavailable");
            var capacity = capacities.SingleOrDefault(value => value.InvoiceId == line.InvoiceId) ?? throw Invalid("payment_balance_unavailable");
            var outstanding = Outstanding(invoice);
            if (invoice.Party != receipt.Party || invoice.Currency != receipt.Currency) throw Invalid("payment_allocation_party_mismatch");
            if (invoice.Version != line.ExpectedVersion) throw Invalid("payment_allocation_stale");
            if (capacity.OriginallyAllocated.Value <= 0 || capacity.PreviouslyReversed.Value > capacity.OriginallyAllocated.Value ||
                line.Amount.Value > capacity.OriginallyAllocated.Value - capacity.PreviouslyReversed.Value || line.Amount.Value > invoice.Paid.Value) throw Invalid("payment_reversal_exceeds_allocation");
            total = Money.Add(total, line.Amount); results.Add(new(invoice.InvoiceId, invoice.Version, line.Amount, Money.Add(outstanding, line.Amount)));
        }
        if (total.Value > receipt.Allocated.Value) throw Invalid("payment_reversal_exceeds_allocation");
        return new(total, Money.Add(available, total), results.ToImmutable());
    }
    private static bool Version(long value) => value is >= 0 and < long.MaxValue - 1;
    private static bool Party(PaymentPartyKey party) => party is not null && party.BuyingOrganizationId.Value > 0 && party.FulfillmentOrganizationId.Value > 0 && party.ShopId.Value > 0 && party.ChannelId.Value > 0;
    public static DomainValidationException Invalid(string code = "invalid_payment_allocation") => new(code, "Review the current receipt and invoice balances before allocation");
}
