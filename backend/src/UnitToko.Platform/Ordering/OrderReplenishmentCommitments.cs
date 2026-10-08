using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Ordering;

/// <summary>Same-transaction Ordering coverage for an already StockRead-authorized source.
/// Every accepted order is reserved at submission; drafts are not commitments. The producer
/// history proves that complete contract before zero additional unreserved demand is reported.</summary>
internal sealed class OrderReplenishmentCommitments(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
{
    internal async Task<(long Reserved, long AdditionalUnreserved, BsonDocument Provenance)> ReadAndFenceAsync(
        IClientSessionHandle tx, HeldReceiptPoolCapture source, long skuId, long definitionId,
        IReadOnlyList<InventoryBalanceDocument> balances, CancellationToken ct)
    {
        source.RequireCurrent();
        var pool = source.Pool;
        var expected = new StockSourceObligationExpectation(pool["_id"].AsInt64, InventoryGraph.Location(pool["location"].AsBsonDocument),
            pool["revision"].AsInt64, pool["fence"].AsInt64);
        await OrderStockSourceStorage.RequireAsync(database, ct);
        var facts = await new OrderStockSourceRecords(database).ReadAsync(tx, new(database, unit, ids, clock), expected, ct);
        var selected = facts.Reservations.Where(row => row["allocation"]["skuId"] == skuId && row["allocation"]["definitionId"] == definitionId).ToArray();
        var byBalance = selected.GroupBy(row => row["allocation"]["balanceId"].AsInt64)
            .ToDictionary(group => group.Key, group => group.Aggregate(0L, (sum, row) => checked(sum + row["remaining"].AsInt64)));
        if (byBalance.Any(pair => pair.Value > 0 && !balances.Any(balance => balance.Id == pair.Key)) ||
            balances.Any(balance => balance.Balance.Reserved != byBalance.GetValueOrDefault(balance.Id))) throw OrderStockSourceRecords.Unavailable();
        async Task Fence(string name, BsonDocument row) {
            if (row["fence"].AsInt64 >= long.MaxValue - 2 ||
                (await database.GetCollection<BsonDocument>(name).UpdateOneAsync(tx, row,
                    Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
                throw new PersistenceConflictException("replenishment_source_changed", "Order commitments changed; recalculate");
        }
        foreach (var row in facts.Orders) await Fence("orders", row);
        foreach (var row in facts.Reservations) await Fence("reservations", row);
        await Fence(OrderStockSourceRecords.Heads, facts.Head);
        source.RequireCurrent();
        var provenance = new BsonDocument { { "owner", "ordering.reserved-at-submission" }, { "poolId", expected.PoolId },
            { "revision", facts.Head["revision"] }, { "headHash", CatalogRepository.Digest(facts.Head) },
            { "reservationIds", new BsonArray(selected.Select(row => row["_id"])) } };
        return (byBalance.Values.Aggregate(0L, (sum, quantity) => checked(sum + quantity)), 0L, provenance);
    }
}
