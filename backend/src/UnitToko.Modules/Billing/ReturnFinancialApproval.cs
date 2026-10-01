using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Billing;

public sealed record ReturnFinancialReleaseInput(LongId OriginalAllocationId, long ExpectedReceiptVersion, Money Amount);
public sealed record ReturnFinancialApprovalInput(long ExpectedInvoiceVersion, ImmutableArray<ReturnFinancialReleaseInput> Releases);
public sealed record ReturnFinancialAllocationCapacity(LongId OriginalAllocationId, LongId InvoiceId, ReceiptFunds Receipt, Money OriginallyAllocated, Money PreviouslyReleased);
public sealed record ReturnFinancialLinkDelta(LongId OriginalAllocationId, LongId ReceiptId, Money OriginallyAllocated, Money PreviouslyReleased, Money Amount, Money ReleasedAfter);
public sealed record ReturnFinancialReceiptReview(ReceiptFunds Before, ReceiptFunds After, Money Released, Money Available);
public sealed record ReturnFinancialApprovalResult(InvoiceFunds InvoiceBefore, InvoiceFunds InvoiceAfter, Money Credit, Money ReservedRefund, Money RemainingInvoice,
    ImmutableArray<ReturnFinancialReceiptReview> Receipts, ImmutableArray<ReturnFinancialLinkDelta> Releases);

/// <summary>The owner supplies the exact immutable maker request, approved rules and verified original allocations.
/// A single transaction must apply credit and every release/reservation together; this review never authorizes payout.</summary>
public static class ReturnFinancialApprovalPolicy
{
    public const int MaximumReleases = PaymentAllocationPolicy.MaximumInvoices;
    public static ReturnFinancialApprovalInput Copy(ReturnFinancialApprovalInput input) {
        if (input is null || input.ExpectedInvoiceVersion is < 0 or >= long.MaxValue - 1 || input.Releases.IsDefault || input.Releases.Length > MaximumReleases ||
            input.Releases.Any(line => line is null || line.OriginalAllocationId.Value <= 0 || line.ExpectedReceiptVersion is < 0 or >= long.MaxValue - 1 || line.Amount.Value <= 0) ||
            input.Releases.Select(line => line.OriginalAllocationId).Distinct().Count() != input.Releases.Length) throw Invalid();
        foreach (var line in input.Releases) _ = new Money(line.Amount.Value);
        return input with { Releases = [.. input.Releases.OrderBy(line => line.OriginalAllocationId.Value)
            .Select(line => line with { Amount = Money.Parse(line.Amount.ToString()) })] };
    }
    public static ReturnFinancialApprovalResult Review(InvoiceFunds invoice, Money requestedCredit, Money approvedCredit, string action,
        ImmutableArray<ReturnFinancialAllocationCapacity> capacities, ReturnFinancialApprovalInput input, string creditRule, string reservationRule) {
        input = Copy(input); _ = new Money(requestedCredit.Value); _ = new Money(approvedCredit.Value);
        if (requestedCredit.Value <= 0 || approvedCredit != requestedCredit) throw Invalid("return_financial_amount_invalid");
        if (action is not ("credit" or "refund") || creditRule != "credit_original_components" || reservationRule != "reserve_released_allocations") throw Invalid("return_financial_policy_unavailable");
        if (invoice is null) throw Invalid("return_financial_balance_unavailable");
        RequireAdvance(invoice.Version); _ = PaymentAllocationPolicy.Outstanding(invoice);
        if (invoice.Version != input.ExpectedInvoiceVersion) throw Invalid("return_financial_stale");
        if (action == "credit" && input.Releases.Length != 0 || action == "refund" && input.Releases.Length == 0) throw Invalid("return_financial_reservation_invalid");
        if (capacities.IsDefault || capacities.Length != input.Releases.Length || capacities.Any(capacity => capacity is null || capacity.OriginalAllocationId.Value <= 0 || capacity.Receipt is null) ||
            capacities.Select(capacity => capacity.OriginalAllocationId).Distinct().Count() != capacities.Length) throw Invalid("return_financial_balance_unavailable");
        var releases = ImmutableArray.CreateBuilder<ReturnFinancialLinkDelta>(); var released = new Money(0m);
        foreach (var selected in input.Releases) {
            var capacity = capacities.SingleOrDefault(value => value.OriginalAllocationId == selected.OriginalAllocationId) ?? throw Invalid("return_financial_balance_unavailable");
            var receipt = capacity.Receipt; RequireAdvance(receipt.Version); _ = PaymentAllocationPolicy.Available(receipt);
            if (capacity.InvoiceId != invoice.InvoiceId || receipt.Party != invoice.Party || receipt.Currency != invoice.Currency) throw Invalid("return_financial_party_mismatch");
            if (selected.ExpectedReceiptVersion != receipt.Version) throw Invalid("return_financial_stale");
            _ = new Money(capacity.OriginallyAllocated.Value); _ = new Money(capacity.PreviouslyReleased.Value);
            if (capacity.OriginallyAllocated.Value <= 0 || capacity.OriginallyAllocated.Value > invoice.Issued.Value || capacity.OriginallyAllocated.Value > receipt.Received.Value ||
                capacity.PreviouslyReleased.Value > capacity.OriginallyAllocated.Value || selected.Amount.Value > capacity.OriginallyAllocated.Value - capacity.PreviouslyReleased.Value) throw Invalid("return_financial_release_exceeds_allocation");
            released = Money.Add(released, selected.Amount);
            releases.Add(new(capacity.OriginalAllocationId, receipt.ReceiptId, capacity.OriginallyAllocated, capacity.PreviouslyReleased, selected.Amount, Money.Add(capacity.PreviouslyReleased, selected.Amount)));
        }
        if (released.Value > approvedCredit.Value || released.Value > invoice.Paid.Value) throw Invalid("return_financial_release_exceeds_credit");
        if (approvedCredit.Value > invoice.Issued.Value - invoice.Credited.Value || invoice.Paid.Value - released.Value + invoice.Credited.Value + approvedCredit.Value > invoice.Issued.Value) throw Invalid("return_financial_credit_exceeds_invoice");
        var receipts = ImmutableArray.CreateBuilder<ReturnFinancialReceiptReview>();
        foreach (var group in capacities.GroupBy(capacity => capacity.Receipt.ReceiptId).OrderBy(group => group.Key.Value)) {
            var before = group.First().Receipt;
            if (group.Any(capacity => capacity.Receipt != before)) throw Invalid("return_financial_balance_unavailable");
            var amount = releases.Where(line => line.ReceiptId == before.ReceiptId).Aggregate(new Money(0m), (total, line) => Money.Add(total, line.Amount));
            if (amount.Value > before.Allocated.Value) throw Invalid("return_financial_release_exceeds_receipt");
            var after = before with { Version = before.Version + 1, Allocated = new(before.Allocated.Value - amount.Value), ReservedForRefund = Money.Add(before.ReservedForRefund, amount) };
            var available = PaymentAllocationPolicy.Available(before);
            if (PaymentAllocationPolicy.Available(after) != available) throw Invalid("return_financial_balance_unavailable");
            receipts.Add(new(before, after, amount, available));
        }
        var invoiceAfter = invoice with { Version = invoice.Version + 1, Paid = new(invoice.Paid.Value - released.Value), Credited = Money.Add(invoice.Credited, approvedCredit) };
        return new(invoice, invoiceAfter, approvedCredit, released, PaymentAllocationPolicy.Outstanding(invoiceAfter), receipts.ToImmutable(), releases.ToImmutable());
    }
    private static void RequireAdvance(long version) {
        if (version is < 0 or >= long.MaxValue - 2) throw Invalid("return_financial_counter_exhausted");
    }
    public static DomainValidationException Invalid(string code = "return_financial_approval_invalid") =>
        new(code, "Review the exact requested credit, original allocation links and current invoice and receipt balances");
}
