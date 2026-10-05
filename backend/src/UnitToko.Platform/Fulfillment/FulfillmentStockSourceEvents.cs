using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Fulfillment;

internal sealed record FulfillmentStockSourceEvent(string Kind, long Id, BsonDocument Record, long[] PoolIds,
    BsonDocument Audit, BsonValue Time, long? ShipmentId, bool? Open)
{
    internal string Hash => CatalogRepository.Digest(new BsonDocument { { "kind", Kind }, { "record", Record } });
}

/// <summary>Internal owning projections. No fabricated actor or disclosure grant is used.
/// Pool membership comes only from dispatched stock effects, never the complete order allocation.</summary>
internal sealed class FulfillmentStockSourceEvents(IMongoDatabase database, IdAllocator ids, IClock clock)
{
    private ShipmentReceiptService Receipts()
    {
        if (database.Client is not MongoClient client) throw FulfillmentStockSourceRecords.Unavailable();
        var unit = new MongoUnitOfWork(client);
        var orders = new OrderSubmissionService(database, unit, ids, clock);
        var preparation = new ShipmentPreparationService(database, unit, ids, clock, orders);
        var shipments = new ShipmentQueryService(database, unit, clock, orders, preparation);
        return new(database, unit, ids, clock, shipments, new(database, unit, ids, clock, shipments));
    }
    internal async Task<FulfillmentStockSourceEvent> ReadAsync(IClientSessionHandle tx, string kind, long id,
        bool requireReceipt, CancellationToken ct)
    {
        if (id <= 0) throw FulfillmentStockSourceRecords.Unavailable();
        if (kind == "setup") {
            var setup = await new OrderStockSourceRecords(database).SetupAsync(tx, id, requireReceipt, ct);
            return new(kind, id, setup.Record, setup.PoolIds, setup.Audit, setup.Time, null, null);
        }
        if (kind == "dispatch") {
            // Fresh custody runs after command-receipt persistence in the same transaction.
            // Ordering still verifies its exact new reservation state before custody is appended.
            var dispatched = await OrderSubmissionService.ReadStockSourceEventAsync(database, tx, "shipment", id, requireReceipt, ct);
            if (requireReceipt) {
                if (database.Client is not MongoClient client) throw FulfillmentStockSourceRecords.Unavailable();
                var order = await new OrderSubmissionService(database, new(client), ids, clock)
                    .ReadStockSourceGraphAsync(tx, dispatched.Record["orderId"].AsInt64, ct);
                if (!order["suborderIds"].AsBsonArray.Contains(dispatched.Record["suborderId"])) throw FulfillmentStockSourceRecords.Unavailable();
            }
            var pools = PoolIds(dispatched.Record);
            return new(kind, id, dispatched.Record, pools, dispatched.Audit, dispatched.Time, id, true);
        }
        if (kind == "receipt") return await Receipts().ReadFulfillmentStockSourceReceiptAsync(tx, id, ct);
        throw FulfillmentStockSourceRecords.Unavailable();
    }
    internal static long[] PoolIds(BsonDocument shipment)
    {
        var effects = shipment["stockEffects"].AsBsonArray;
        if (effects.Count is < 1 or > 100) throw FulfillmentStockSourceRecords.Unavailable();
        var pools = effects.Select(value => value["allocation"]["poolId"].AsInt64).Distinct().Order().ToArray();
        if (pools.Any(id => id <= 0 || id >= long.MaxValue - 1)) throw FulfillmentStockSourceRecords.Unavailable();
        return pools;
    }
}
