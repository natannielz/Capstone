using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Ordering;

public enum OrderSpendingMode { EveryOrder, AboveThreshold, NotRequired }
/// <summary>Values from an approved, scoped business-policy version, never browser authority.
/// The application owner must load, verify and fence that policy and its approval evidence.</summary>
public sealed record OrderSpendingRule(OrderSpendingMode Mode, Money? Threshold);
public enum OrderSpendingState { Pending, NotRequired, Approved, Rejected }
public enum OrderReviewStage { Spending, Store, Cancellation }
public enum OrderReviewAction { Approve, Reject, CancelRemainder }

public sealed record OrderApprovalProgress(long Revision, OrderSpendingState SpendingState, ImmutableArray<LongId> AcceptedSuborders,
    ImmutableArray<LongId> RejectedSuborders, bool Cancelled)
{
    public long DispatchSequence { get; init; }
    public bool HasRemainingReservation { get; init; } = true;
}

public static class OrderSpendingPolicy
{
    public static OrderSpendingState Initial(OrderSpendingRule? rule, Money total)
    {
        if (rule is null) throw Invalid("order_spending_policy_unavailable");
        if (!Enum.IsDefined(rule.Mode) || (rule.Mode == OrderSpendingMode.AboveThreshold) != rule.Threshold.HasValue)
            throw Invalid("invalid_order_spending_policy");
        return rule.Mode == OrderSpendingMode.EveryOrder || rule.Mode == OrderSpendingMode.AboveThreshold && total.Value > rule.Threshold!.Value.Value
            ? OrderSpendingState.Pending : OrderSpendingState.NotRequired;
    }
    public static bool CanAcceptAtStore(OrderSpendingState state) => state is OrderSpendingState.NotRequired or OrderSpendingState.Approved;
    private static DomainValidationException Invalid(string code) => new(code, "A current approved spending rule is required");
}

/// <summary>Current reserved count, cumulative dispatched count and cumulative cancelled remainder.
/// Receipt/after-sales quantities belong to Fulfillment. An order never reduces demand by editing history.</summary>
public sealed record OrderLineProgress(int Requested, int Reserved, int Dispatched, int Cancelled)
{
    public int Unallocated => OrderLinePolicy.Unallocated(this);
}
public sealed record OrderRemainderCancellation(OrderLineProgress Next, int ReleaseReserved);

public static class OrderLinePolicy
{
    public static int Unallocated(OrderLineProgress line)
    {
        if (line is null || line.Requested is < 1 or > DomainValues.MaximumLineQuantity ||
            line.Reserved < 0 || line.Dispatched < 0 || line.Cancelled < 0 ||
            (long)line.Reserved + line.Dispatched + line.Cancelled > line.Requested)
            throw Invalid("invalid_order_line_progress");
        return line.Requested - line.Reserved - line.Dispatched - line.Cancelled;
    }
    public static OrderLineProgress Reserve(OrderLineProgress line, int packages)
    {
        DomainValues.ParseQuantity(packages);
        if (packages > Unallocated(line)) throw Invalid("order_reservation_exceeds_remainder");
        return line with { Reserved = line.Reserved + packages };
    }
    public static OrderLineProgress Dispatch(OrderLineProgress line, int packages)
    {
        DomainValues.ParseQuantity(packages); _ = Unallocated(line);
        if (packages > line.Reserved) throw Invalid("order_dispatch_exceeds_reservation");
        return line with { Reserved = line.Reserved - packages, Dispatched = line.Dispatched + packages };
    }
    public static OrderRemainderCancellation CancelRemainder(OrderLineProgress line)
    {
        _ = Unallocated(line);
        var remainder = line.Requested - line.Dispatched - line.Cancelled;
        if (remainder <= 0) throw Invalid("order_remainder_already_resolved");
        return new(line with { Reserved = 0, Cancelled = line.Cancelled + remainder }, line.Reserved);
    }
    private static DomainValidationException Invalid(string code) => new(code, "The requested order-line transition exceeds its unresolved quantity");
}

public sealed record OrderFulfillmentGroup(GrantScope Owner, ImmutableArray<OrderQuotedLine> Lines, Money CatalogSubtotal);

/// <summary>Separate operational demand by exact organization/shop/channel, never a product label.
/// This pure split is not submission, a reservation or acceptance of tax/credit/commercial terms.</summary>
public static class OrderFulfillmentPolicy
{
    public static ImmutableArray<OrderFulfillmentGroup> Split(OrderCatalogQuote quote)
    {
        if (quote is null || quote.Lines.IsDefaultOrEmpty || quote.Lines.Length > 100 ||
            quote.Lines.Any(line => line is null || line.Definition is null || line.Price is null) ||
            quote.Lines.Select(line => line.Definition.SkuId).Distinct().Count() != quote.Lines.Length)
            throw Invalid();
        var sum = new Money(0);
        foreach (var line in quote.Lines) {
            CatalogAuthoringPolicy.Owner(line.FulfillmentOwner); CatalogPricePolicy.Validate(line.Price);
            DomainValues.ParseQuantity(line.Packages);
            if (line.Definition.SkuId != line.Price.SkuId || line.Price.Currency != quote.Currency ||
                line.BaseUnits != CatalogPolicy.BaseUnitsForPackages(line.Definition, line.Packages) ||
                line.Packages < line.Definition.MinimumOrderPackages || line.Packages % line.Definition.OrderIncrementPackages != 0 ||
                line.CatalogSubtotal != Money.Multiply(line.Price.UnitPrice, line.Packages)) throw Invalid();
            sum = Money.Add(sum, line.CatalogSubtotal);
        }
        if (sum != quote.CatalogSubtotal) throw Invalid();
        return quote.Lines.GroupBy(line => line.FulfillmentOwner).Select(group => new OrderFulfillmentGroup(group.Key,
            group.Select(line => line with { Definition = line.Definition with {
                Attributes = line.Definition.Attributes.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                ImageAssetIds = line.Definition.ImageAssetIds.ToArray().ToImmutableArray() } }).ToImmutableArray(),
            group.Aggregate(new Money(0), (value, line) => Money.Add(value, line.CatalogSubtotal)))).ToImmutableArray();
    }
    private static DomainValidationException Invalid() => new("invalid_order_fulfillment_split", "Split only exact authoritative package demand and prices");
}
