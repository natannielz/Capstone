using System.Collections.Immutable;
using System.Text.Json.Serialization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;

namespace UnitToko.Modules.Fulfillment;

/// <summary>Exact outstanding reservation provenance supplied by Ordering, never browser stock authority.</summary>
public sealed record ShipmentReservationFact(LongId ReservationId, LongId SkuId, LongId DefinitionId, LongId LotId, int RemainingPackages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long LineRevision = 0);
/// <summary>The picker explicitly confirms which reserved lots are physically leaving the warehouse.</summary>
public sealed record ShipmentLotSelection(LongId ReservationId, int Packages);

public static class ShipmentDispatchPolicy
{
    public static ImmutableArray<ShipmentLotSelection> Copy(ImmutableArray<ShipmentLotSelection> selected) {
        if (selected.IsDefaultOrEmpty || selected.Length > 100 || selected.Any(line => line is null || line.ReservationId.Value <= 0) ||
            selected.Select(line => line.ReservationId).Distinct().Count() != selected.Length) throw Invalid();
        foreach (var line in selected) DomainValues.ParseQuantity(line.Packages);
        return selected.ToArray().ToImmutableArray();
    }
    public static ImmutableArray<ShipmentLotSelection> Require(FulfillmentOrderView source, string review,
        ImmutableArray<ShipmentPackageSelection> preparation, ImmutableArray<ShipmentReservationFact> reservations, ImmutableArray<ShipmentLotSelection> selected) {
        var packages = ShipmentPreparationPolicy.Require(source, review, preparation); var picked = Copy(selected);
        if (reservations.IsDefaultOrEmpty || reservations.Length > 100 || reservations.Any(row => row is null || row.ReservationId.Value <= 0 || row.LotId.Value <= 0) ||
            reservations.Select(row => row.ReservationId).Distinct().Count() != reservations.Length) throw Invalid();
        foreach (var row in reservations) {
            new OrderLineIdentity(row.SkuId, row.LineRevision).Validate();
            var line = source.Lines.SingleOrDefault(line => line.SkuId == row.SkuId && line.LineRevision == row.LineRevision);
            if (line is null || line.Package.Id != row.DefinitionId || row.RemainingPackages is < 0 or > DomainValues.MaximumLineQuantity) throw Invalid();
        }
        foreach (var line in source.Lines)
            if (reservations.Where(row => row.SkuId == line.SkuId && row.LineRevision == line.LineRevision).Sum(row => (long)row.RemainingPackages) != line.RemainingPackages) throw Invalid();
        foreach (var selectedLot in picked) {
            var lot = reservations.SingleOrDefault(row => row.ReservationId == selectedLot.ReservationId);
            if (lot is null || selectedLot.Packages > lot.RemainingPackages || !packages.Any(line => line.SkuId == lot.SkuId && line.LineRevision == lot.LineRevision)) throw Invalid();
        }
        foreach (var line in packages)
            if (picked.Where(row => { var lot = reservations.Single(lot => lot.ReservationId == row.ReservationId); return lot.SkuId == line.SkuId && lot.LineRevision == line.LineRevision; }).Sum(row => (long)row.Packages) != line.Packages) throw Invalid();
        return picked;
    }
    private static DomainValidationException Invalid() => new("shipment_picked_lots_changed", "Confirm the exact reserved lots and complete package quantities in the current preparation");
}
