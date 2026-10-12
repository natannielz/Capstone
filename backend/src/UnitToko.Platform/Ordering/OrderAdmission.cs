using System.Collections.Immutable;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Ordering;

internal sealed record OrderPolicyReference(long Id, long Revision);
internal sealed record OrderPartySnapshot(OrderPolicyReference Version, string Name, string Address);
internal sealed record OrderDeliverySnapshot(OrderPolicyReference Version, OrderDeliveryRequest Address);
internal sealed record OrderCommercialTerms(OrderPolicyReference Policy, OrderPolicyReference TaxPolicy,
    OrderPolicyReference CreditDecision, OrderPolicyReference SpendingPolicy, OrderSpendingRule Spending,
    OrderPartySnapshot Buyer, OrderDeliverySnapshot Delivery, Money Tax, Money Charges, Money Total);
internal sealed record OrderStockAllocation(long SkuId, OrderPolicyReference SourceAuthority,
    InventoryPoolExpectation Pool, InventoryBalanceChange Change, long LineRevision = 0);
internal sealed record OrderAdmission(OrderBuyingContext Context, OrderCommercialTerms Terms,
    ImmutableArray<OrderStockAllocation> Stock);

/// <summary>Trusted, transaction-bound owner integration. Implementations must load and conditionally
/// write the actual approved scoped party/address/commercial/spending/credit and stock-source facts.
/// They must enforce budget/credit obligations, eligible lot/expiry/cost, source freshness/control and
/// any actual external hold before returning. Absence is unavailable, never an unlimited/zero policy.
/// No production adapter is registered until those owning workflows and business inputs are ready.</summary>
internal interface IOrderAdmissionReader
{
    IMongoDatabase Database { get; }
    Task<OrderAdmission> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor,
        OrderDraftInput input, OrderCatalogQuote quote, CancellationToken ct);
    // Recheck time-sensitive approvals/holds after all command awaits. Must not create another
    // credit/stock effect. Throwing here rolls back the enclosing order transaction.
    Task CompleteAsync(IClientSessionHandle tx, ActorContext actor, OrderAdmission admission, CancellationToken ct);
}

internal static class OrderAdmissionPolicy
{
    internal static void Validate(OrderDraftInput input, OrderCatalogQuote quote, OrderAdmission value)
        => ValidateCore(input, quote, quote, value);
    internal static void ValidateSubstitution(OrderDraftInput input, OrderCatalogQuote quote, OrderCatalogQuote replacement, OrderAdmission value)
    {
        if (replacement is null || replacement.Lines.Length != 1 || replacement.Currency != quote.Currency ||
            !quote.Lines.Contains(replacement.Lines[0])) throw Invalid();
        ValidateCore(input, quote, replacement, value);
    }
    private static void ValidateCore(OrderDraftInput input, OrderCatalogQuote quote, OrderCatalogQuote reservedQuote, OrderAdmission value)
    {
        if (value is null || value.Context != input.Context || value.Terms is not { } terms ||
            terms.Buyer is null || terms.Delivery is null || terms.Delivery.Address is null ||
            value.Stock.IsDefaultOrEmpty || value.Stock.Length > 100) throw Invalid();
        foreach (var reference in new[] { terms.Policy, terms.TaxPolicy, terms.CreditDecision,
            terms.SpendingPolicy, terms.Buyer.Version, terms.Delivery.Version }) Reference(reference);
        InventoryLedger.RequireText(terms.Buyer.Name, 200); InventoryLedger.RequireText(terms.Buyer.Address, 1000);
        var delivery = OrderDraftPolicy.Copy(input with { Delivery = terms.Delivery.Address }).Delivery;
        // A trusted branch address must still be the address the buyer reviewed; never silently replace it.
        if (input.Delivery is null || delivery != input.Delivery) throw Invalid("order_delivery_changed");
        if (Money.Add(Money.Add(quote.CatalogSubtotal, terms.Tax), terms.Charges) != terms.Total) throw Invalid();
        _ = OrderSpendingPolicy.Initial(terms.Spending, terms.Total);
        if (value.Stock.Any(item => item is null || item.Change is null) || value.Stock.Select(item => item.Change.Id).Distinct().Count() != value.Stock.Length) throw Invalid();
        foreach (var allocation in value.Stock) {
            if (allocation is null || allocation.Pool is null || allocation.Change is null) throw Invalid();
            Reference(allocation.SourceAuthority);
            var line = reservedQuote.Lines.SingleOrDefault(item => item.Definition.SkuId.Value == allocation.SkuId && item.LineRevision == allocation.LineRevision) ?? throw Invalid();
            var pool = allocation.Pool; var change = allocation.Change; var owner = line.FulfillmentOwner;
            if (pool.Id <= 0 || pool.Revision <= 0 || pool.Location is null || change.Id <= 0 || change.ExpectedRevision <= 0 ||
                change.Key is null || change.PhysicalPoolId != pool.Id || change.Key.Location != pool.Location ||
                change.Key.SkuId.Value != allocation.SkuId || change.Key.DefinitionId != line.Definition.Id ||
                change.Action != InventoryBalanceAction.Reserve || change.Packages <= 0 || change.Packages > line.Packages ||
                owner.OrganizationId != pool.Location.OrganizationId.Value || owner.ShopId != pool.Location.ShopId.Value ||
                owner.ChannelId != pool.Location.ChannelId.Value || owner.WarehouseId is { } warehouse && warehouse != pool.Location.WarehouseId.Value)
                throw Invalid();
            pool.Location.Validate();
        }
        foreach (var line in reservedQuote.Lines)
            if (value.Stock.Where(item => item.SkuId == line.Definition.SkuId.Value && item.LineRevision == line.LineRevision).Sum(item => item.Change.Packages) != line.Packages) throw Invalid();
        foreach (var group in value.Stock.GroupBy(item => item.Pool.Id))
            if (group.Select(item => item.Pool).Distinct().Count() != 1) throw Invalid();
    }
    internal static void Reference(OrderPolicyReference? value) {
        if (value is null || value.Id <= 0 || value.Revision <= 0) throw Invalid();
    }
    internal static DomainValidationException Invalid(string code = "order_admission_unavailable") =>
        new(code, "Current approved branch, commercial and stock authority is required");
}
