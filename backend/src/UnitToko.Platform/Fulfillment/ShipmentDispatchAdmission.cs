using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Fulfillment;

internal sealed record ShipmentLotAuthority(long ReservationId, OrderPolicyReference SourceAuthority,
    OrderPolicyReference CostBasis, Money PackageCost);
internal sealed record ShipmentDispatchAdmission(OrderPartySnapshot Seller, OrderPolicyReference OperatingPolicy,
    ImmutableArray<ShipmentLotAuthority> Lots, DateTimeOffset ValidUntil) {
    internal BsonDocument? FiniteValue { get; init; }
    internal ShipmentDispatchService.ValueCapture? CapturedValue { get; init; }
}

/// <summary>Real stock-source, physical-lot/cost and operating-policy owners must approve and fence
/// dispatch in the caller's Mongo transaction. Check freshness/control/external holds, approved cost
/// basis, expiry and lot restrictions at the final instant. An absent reader is unavailable, never
/// zero cost or blanket authority. Courier application eligibility is checked separately by Fulfillment.</summary>
internal interface IShipmentDispatchAdmissionReader
{
    IMongoDatabase Database { get; }
    Task<ShipmentDispatchAdmission> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor,
        OrderDispatchSource source, ImmutableArray<UnitToko.Modules.Fulfillment.ShipmentLotSelection> selected, CancellationToken ct);
    Task CompleteAsync(IClientSessionHandle tx, ActorContext actor, ShipmentDispatchAdmission admission, CancellationToken ct);
}

internal static class ShipmentDispatchAdmissionPolicy
{
    internal static void RequireCurrent(ShipmentDispatchAdmission admission, DateTimeOffset now) {
        if (admission.ValidUntil == default || admission.ValidUntil.Offset != TimeSpan.Zero || admission.ValidUntil <= now) throw Invalid();
    }
    internal static void Require(OrderDispatchSource source, ImmutableArray<UnitToko.Modules.Fulfillment.ShipmentLotSelection> selected,
        ShipmentDispatchAdmission admission) {
        if (admission is null || admission.Seller is null || admission.Lots.IsDefaultOrEmpty || admission.Lots.Length != selected.Length ||
            admission.Lots.Any(lot => lot is null) || admission.Lots.Select(lot => lot.ReservationId).Distinct().Count() != admission.Lots.Length) throw Invalid();
        OrderAdmissionPolicy.Reference(admission.Seller.Version); OrderAdmissionPolicy.Reference(admission.OperatingPolicy);
        UnitToko.Platform.Inventory.InventoryLedger.RequireText(admission.Seller.Name, 200);
        UnitToko.Platform.Inventory.InventoryLedger.RequireText(admission.Seller.Address, 1000);
        var totalCost = new Money(0);
        foreach (var selectedLot in selected) {
            var reserved = source.Reservations.SingleOrDefault(row => row.Fact.ReservationId == selectedLot.ReservationId) ?? throw Invalid();
            var authority = admission.Lots.SingleOrDefault(lot => lot.ReservationId == selectedLot.ReservationId.Value) ?? throw Invalid();
            OrderAdmissionPolicy.Reference(authority.SourceAuthority); OrderAdmissionPolicy.Reference(authority.CostBasis);
            if (authority.SourceAuthority != reserved.Allocation.SourceAuthority) throw Invalid();
            if (admission.FiniteValue is null) totalCost = Money.Add(totalCost, Money.Multiply(authority.PackageCost, selectedLot.Packages));
        }
        if (admission.FiniteValue is { } finite) ShipmentDispatchValueRecords.RequireReview(finite, selected, source.Reservations);
    }
    internal static DomainValidationException Invalid() => new("shipment_dispatch_admission_unavailable", "Current approved source, lot, cost and dispatch policy are required");
}
