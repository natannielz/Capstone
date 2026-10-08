using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Procurement;

internal sealed class ProcurementReplenishmentSupplyReader(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
{
    internal sealed class Capture(PurchaseOrderRemainingSupplyCapture remaining, ImmutableArray<ReplenishmentSupply> supplies,
        ImmutableArray<PurchaseArrivalReadCapture> arrivals, BsonDocument provenance)
    {
        private readonly BsonDocument snapshot = provenance.DeepClone().AsBsonDocument;
        internal bool CoverageComplete => remaining.CoverageComplete;
        internal IReadOnlyList<ReplenishmentSupply> Supplies { get; } = supplies;
        internal BsonDocument Provenance => snapshot.DeepClone().AsBsonDocument;
        internal async Task CompleteAsync(CancellationToken ct) {
            remaining.RequireCurrent();
            foreach (var arrival in arrivals) await arrival.FenceAsync(ct);
            // Revalidate receipt/order membership after the arrival owner's awaits.
            // No physical pool fence is advanced; the stock capture owns that fence.
            await remaining.FenceAsync(ct);
            foreach (var arrival in arrivals) arrival.RequireCurrent();
            remaining.RequireCurrent(); ct.ThrowIfCancellationRequested();
        }
    }

    internal async Task<Capture> ReadAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        StockLocation location, long poolId, long skuId, long definitionId, CancellationToken ct)
    {
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var requests = new ProcurementRequestService(database, unit, ids, clock, catalog);
        var suppliers = new SupplierService(database, unit, ids, clock, catalog);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, requests, suppliers);
        var remaining = await orders.CaptureRemainingForReplenishmentAsync(actor, authority, tx, location, poolId, skuId, definitionId, ct);
        var supplies = ImmutableArray.CreateBuilder<ReplenishmentSupply>();
        var arrivals = ImmutableArray.CreateBuilder<PurchaseArrivalReadCapture>(); var arrivalProofs = new BsonArray();
        foreach (var line in remaining.Lines.Where(value => value.RemainingPackages > 0)) {
            var arrival = await orders.CaptureArrivalForReplenishmentAsync(actor, authority, tx, line.OrderId, skuId, definitionId, ct);
            arrival.RequireCurrent(); arrivals.Add(arrival); arrivalProofs.Add(arrival.Provenance.DeepClone());
            supplies.Add(new($"purchase-order:{line.OrderId}:pool:{poolId}:sku:{skuId}:definition:{definitionId}",
                line.RemainingPackages, arrival.ExpectedAt, arrival.Reviewed));
        }
        remaining.RequireCurrent();
        return new(remaining, supplies.ToImmutable(), arrivals.ToImmutable(), new BsonDocument {
            { "remainingSupply", remaining.Provenance }, { "arrivalSchedules", arrivalProofs } });
    }
}
