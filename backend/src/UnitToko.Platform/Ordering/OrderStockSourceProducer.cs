using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Ordering;

/// <summary>Only fresh owning callbacks call this writer. The genuine source audit/receipt and
/// effects join its transaction. Read/recovery never manufacture missing heads/transitions.</summary>
internal sealed class OrderStockSourceProducer(IMongoDatabase database, IdAllocator ids, IClock clock,
    ShipmentDispatchValueRecords.HistoricalRead? history = null, ShipmentDispatchService.LiveValueRead? liveRead = null)
{
    private readonly OrderStockSourceRecords records = new(database);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private void Require(IClientSessionHandle tx) {
        new InventoryRepository(database).RequireSession(tx);
        history?.Require(database, tx); liveRead?.Require(database, clock, tx);
    }
    internal async Task InitializeAsync(IClientSessionHandle tx, long setupDecisionId, CancellationToken ct)
    {
        await OrderStockSourceStorage.RequireAsync(database, ct); new InventoryRepository(database).RequireSession(tx);
        var source = await records.SetupAsync(tx, setupDecisionId, false, ct); var poolId = source.PoolIds.Single();
        var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", poolId)).SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
        if (pool!["active"].AsBoolean || pool["revision"] != 1L || pool["fence"] != 0L ||
            await Rows(OrderStockSourceRecords.Heads).Find(tx, new BsonDocument("_id", poolId)).AnyAsync(ct) ||
            await Rows(OrderStockSourceRecords.Transitions).Find(tx, new BsonDocument("poolId", poolId)).AnyAsync(ct) ||
            await Rows("orders").Find(tx, new BsonDocument("allocations.poolId", poolId)).AnyAsync(ct) ||
            await Rows("reservations").Find(tx, new BsonDocument("allocation.poolId", poolId)).AnyAsync(ct)) throw OrderStockSourceRecords.Unavailable();
        var transition = await TransitionAsync(source, poolId, null, ct);
        var head = new BsonDocument { { "_id", poolId }, { "schemaVersion", 1 }, { "kind", "ordering.stocksource.head" },
            { "location", pool["location"].DeepClone() }, { "setupDecisionId", setupDecisionId }, { "revision", 1L },
            { "lastTransitionId", transition["_id"] }, { "lastHash", transition["transitionHash"] }, { "updatedAt", source.Time }, { "fence", 0L } };
        await Rows(OrderStockSourceRecords.Transitions).InsertOneAsync(tx, transition, cancellationToken: ct);
        await Rows(OrderStockSourceRecords.Heads).InsertOneAsync(tx, head, cancellationToken: ct);
        // No pool.active/revision/fence mutation and no inferred operational acceptance.
    }
    internal async Task RecordAsync(IClientSessionHandle tx, string sourceKind, long sourceId, CancellationToken ct)
    {
        Require(tx); await OrderStockSourceStorage.RequireAsync(database, ct); Require(tx);
        var source = await OrderSubmissionService.ReadStockSourceEventAsync(database, tx, sourceKind, sourceId, false, ct, history, clock, liveRead);
        liveRead?.RequirePublicationSource(database, clock, tx, source);
        var transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        foreach (var poolId in source.PoolIds.Order()) {
            var (head, prior, _) = await records.HistoryAsync(tx, poolId, ct, history, clock, liveRead);
            if (head["revision"].AsInt64 >= OrderStockSourceRecords.MaximumRows || head["fence"].AsInt64 >= long.MaxValue - 2 ||
                prior.Any(row => row["sourceKind"] == sourceKind && row["sourceId"] == sourceId) ||
                StockSourceSetupBson.Time(source.Time) < StockSourceSetupBson.Time(head["updatedAt"]) || clock.UtcNow < StockSourceSetupBson.Time(source.Time))
                throw OrderStockSourceRecords.Unavailable();
            var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", poolId)).SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
            if (pool!["location"] != head["location"] || pool["fence"].AsInt64 >= long.MaxValue - 2) throw OrderStockSourceRecords.Unavailable();
            var transition = await TransitionAsync(source, poolId, prior[^1], ct);
            await Rows(OrderStockSourceRecords.Transitions).InsertOneAsync(tx, transition, cancellationToken: ct);
            var update = Builders<BsonDocument>.Update.Inc("revision", 1L).Inc("fence", 1L).Set("lastTransitionId", transition["_id"])
                .Set("lastHash", transition["transitionHash"]).Set("updatedAt", source.Time);
            if ((await Rows(OrderStockSourceRecords.Heads).UpdateOneAsync(tx, head, update, cancellationToken: ct)).ModifiedCount != 1 ||
                (await Rows("inventoryPools").UpdateOneAsync(tx, pool, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
                throw new PersistenceConflictException("order_source_obligations_changed", "Ordering source state changed; retry only the original operation key");
            Require(tx); liveRead?.RequirePublicationSource(database, clock, tx, source);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber) throw OrderStockSourceRecords.Unavailable();
        }
        Require(tx);
    }
    private async Task<BsonDocument> TransitionAsync(OrderStockSourceEvent source, long poolId, BsonDocument? previous, CancellationToken ct)
    {
        var row = new BsonDocument { { "_id", (await ids.NextAsync(OrderStockSourceRecords.Transitions, ct)).Value },
            { "schemaVersion", 1 }, { "kind", "ordering.stocksource.transition" }, { "poolId", poolId },
            { "revision", previous is null ? 1L : checked(previous["revision"].AsInt64 + 1) },
            { "previousId", previous is null ? BsonNull.Value : previous["_id"] }, { "previousHash", previous is null ? BsonNull.Value : previous["transitionHash"] },
            { "sourceKind", source.Kind }, { "sourceId", source.Id }, { "sourceHash", source.Hash }, { "sourceAuditId", source.Audit["_id"] },
            { "actorId", source.Audit["actorId"] }, { "actorGrantVersion", source.Audit["actorGrantVersion"] }, { "recordedAt", source.Time },
            { "effects", OrderStockSourceRecords.Effects(source, poolId) } };
        row.Add("transitionHash", OrderStockSourceRecords.Hash(row)); return row;
    }
}
