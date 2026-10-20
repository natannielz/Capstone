using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Fulfillment;

public sealed partial class ShipmentQueryService
{
    internal async Task<System.Collections.Immutable.ImmutableArray<long>> InvoiceReleaseShipmentsAsync(ActorContext actor,
        AuthorizationSnapshot authority, IClientSessionHandle tx, long? beforeId, CancellationToken ct) {
        if (beforeId is <= 0) throw ShipmentDispatchRecords.Invalid();
        var releaseQuery = UnitToko.Modules.Organizations.ScopePolicy.Compile(actor, authority, "invoice.release.read", clock.UtcNow);
        UnitToko.Platform.Authorization.AuthorizationStore.Require(releaseQuery);
        var query = UnitToko.Platform.Authorization.ScopeQuery.Create(Query(actor, authority, UnitToko.Modules.Identity.Permissions.InvoiceRead)) &
            UnitToko.Platform.Authorization.ScopeQuery.Create(releaseQuery);
        if (beforeId.HasValue) query &= F.Lt("_id", beforeId.Value);
        var rows = await Rows("shipments").Find(tx, query).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(ct);
        await FenceAsync(actor, authority, tx, rows.Select(Resource).ToArray(), ct, UnitToko.Modules.Identity.Permissions.InvoiceRead);
        return [.. rows.Select(row => row["_id"].AsInt64)];
    }
    // The caller already authorized the exact receipt through InvoiceSourceAsync.
    // Recover the actual accepted commercial revision through the same owner graph;
    // the immutable commercial snapshot itself is not extended or rewritten.
    internal async Task<DateTimeOffset> InvoiceCommercialApprovalTimeAsync(ActorContext actor, IClientSessionHandle tx, long shipmentId, CancellationToken ct)
    {
        var history = orders.StockSourceHistory ?? new ShipmentDispatchValueRecords.HistoricalRead(database, clock, tx);
        var sourceOrders = orders.WithDispatchHistory(tx, history);
        var shipment = await new ShipmentDispatchRecords(database, clock, history, sourceOrders.StockSourceLiveRead).ReadAsync(tx, shipmentId, ct);
        return await sourceOrders.InvoiceCommercialApprovalTimeAsync(tx, actor, shipment, ct);
    }
}

public sealed partial class ShipmentReceiptService
{
    internal Task<System.Collections.Immutable.ImmutableArray<long>> InvoiceReleaseShipmentsAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long? beforeId, CancellationToken ct) => shipments.InvoiceReleaseShipmentsAsync(actor, authority, tx, beforeId, ct);
    internal Task<DateTimeOffset> InvoiceCommercialApprovalTimeAsync(ActorContext actor, IClientSessionHandle tx, long shipmentId, CancellationToken ct) =>
        shipments.InvoiceCommercialApprovalTimeAsync(actor, tx, shipmentId, ct);
}
