using System.Collections.Immutable;
using System.Text.Json.Serialization;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;

namespace UnitToko.Modules.Fulfillment;

/// <summary>Exact accepted package meaning and outstanding reserved demand supplied by Ordering.
/// These operational facts deliberately exclude prices, margins and other stores' demand.</summary>
public sealed record FulfillmentOrderLine(LongId SkuId, Guid PublicId, string SkuCode, string ProductName,
    VariantDefinitionVersion Package, int OrderedPackages, int RemainingPackages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long LineRevision = 0);
public sealed record FulfillmentOrderView(LongId OrderId, LongId SuborderId, long Revision, string Review,
    string BuyerName, OrderDeliveryRequest Delivery, ImmutableArray<FulfillmentOrderLine> Lines,
    bool CanPrepare, DateTimeOffset OrderedAt);
public sealed record ShipmentPackageSelection(LongId SkuId, int Packages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long LineRevision = 0);

/// <summary>A preparation selects complete packages, possibly fewer than the outstanding order.
/// It does not dispatch stock, prove delivery or grant permission to invoice.</summary>
public static class ShipmentPreparationPolicy
{
    public static ImmutableArray<ShipmentPackageSelection> Copy(ImmutableArray<ShipmentPackageSelection> lines)
    {
        if (lines.IsDefaultOrEmpty || lines.Length > 100 || lines.Any(line => line is null || line.SkuId.Value <= 0) ||
            lines.Select(line => line.SkuId).Distinct().Count() != lines.Length) throw Invalid();
        // Keep the existing one-SKU-per-shipment contract used by receipt and invoice owners.
        // A second accepted version is explicit in the source and can use another preparation.
        foreach (var line in lines) { new OrderLineIdentity(line.SkuId, line.LineRevision).Validate(); DomainValues.ParseQuantity(line.Packages); }
        return lines.ToArray().ToImmutableArray();
    }
    public static ImmutableArray<ShipmentPackageSelection> Require(FulfillmentOrderView source, string review,
        ImmutableArray<ShipmentPackageSelection> lines)
    {
        var copy = Copy(lines);
        if (source is null || !source.CanPrepare || source.Review != review || source.Lines.IsDefaultOrEmpty ||
            source.Lines.Select(line => new OrderLineIdentity(line.SkuId, line.LineRevision)).Distinct().Count() != source.Lines.Length)
            throw Invalid("shipment_order_changed");
        foreach (var selected in copy) {
            var line = source.Lines.SingleOrDefault(value => value.SkuId == selected.SkuId && value.LineRevision == selected.LineRevision);
            if (line is null || selected.Packages > line.RemainingPackages || line.RemainingPackages > line.OrderedPackages ||
                line.Package.SkuId != selected.SkuId) throw Invalid("shipment_quantity_exceeds_remainder");
            // Fulfillment may be partial even when the original purchase had a larger minimum.
            _ = CatalogPolicy.BaseUnitsForPackages(line.Package, selected.Packages);
        }
        return copy;
    }
    private static DomainValidationException Invalid(string code = "invalid_shipment_preparation") =>
        new(code, "Choose explicit package quantities from the currently accepted outstanding order");
}
