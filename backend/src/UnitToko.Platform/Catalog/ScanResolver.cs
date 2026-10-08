using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Catalog;

public sealed class ScanResolver(CatalogAuthoringService catalog, OrderSubmissionService orders,
    ShipmentDispatchService dispatch, ProcurementStockReceiptService receiving)
{
    public async Task<ScanResolution> ResolveAsync(ActorContext actor, ScanRequest request, string correlationId, CancellationToken ct = default)
    {
        ScanRules.Validate(request);
        if (request.Purpose == "stocktake") return ScanRules.Resolve(request, await catalog.StocktakeScanChoicesAsync(actor, request, correlationId, ct));
        if (request.Purpose == "receiving") return ScanRules.Resolve(request, await receiving.ScanChoicesAsync(actor, request, ct));
        if (request.Purpose == "picking") return ScanRules.Resolve(request, await orders.ScanChoicesAsync(actor, request, ct));
        return ScanRules.Resolve(request, await dispatch.ScanChoicesAsync(actor, request, ct));
    }
    internal static ScanSourceCandidate Choice(UnitToko.Modules.Fulfillment.FulfillmentOrderLine line) => new(new(line.SkuId.ToString(), line.SkuId,
        line.Package.Id, line.Package.Version, line.SkuCode, line.Package.PackageLabel, line.Package.PackageKind, line.Package.BaseUnitCode, line.Package.BaseUnitsPerPackage));
    private static PersistenceConflictException Changed() => new("operational_scan_context_changed", "Reload the current document and inspect the package again");
}
