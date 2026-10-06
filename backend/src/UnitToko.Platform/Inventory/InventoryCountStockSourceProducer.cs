using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

/// <summary>Fresh owning writes only. A count receipt is already complete before
/// appending; neither recovery nor read may initialize, append or repair history.</summary>
internal sealed class InventoryCountStockSourceProducer(IMongoDatabase database, IdAllocator ids, IClock clock)
{
    private readonly InventoryCountStockSourceRecords records = new(database);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task InitializeAsync(IClientSessionHandle tx, long setupDecisionId, CancellationToken ct) {
        await InventoryCountStockSourceStorage.RequireAsync(database, ct); new InventoryRepository(database).RequireSession(tx);
        var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; var at = clock.UtcNow;
        var counts = new StockCountService(database, new(database.Client as MongoClient ?? throw InventoryCountStockSourceRecords.Unavailable()), ids, clock);
        var sources = new StockSourceSetupService(database, new(database.Client as MongoClient ?? throw InventoryCountStockSourceRecords.Unavailable()), ids, clock);
        // Only the real fresh setup callback may use its authentic pending approval receipt.
        var source = await records.EventAsync(tx, counts, sources, "setup", setupDecisionId, false, ct);
        var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", source.PoolId)).SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
        await new StockSourceTopologyFence(database).FenceAsync(tx, pool!["location"]["organizationId"].AsInt64, ct);
        await new InventoryCountCoverage(database).InitializeFreshAsync(tx, counts, sources, pool["location"]["organizationId"].AsInt64, ct);
        if (pool!["active"] != false || pool["revision"] != 1L || pool["fence"] != 0L || pool["location"] != source.Record["proposal"]["input"]["location"] ||
            await Rows(InventoryCountStockSourceRecords.Heads).Find(tx, new BsonDocument("_id", source.PoolId)).AnyAsync(ct) ||
            await Rows(InventoryCountStockSourceRecords.Transitions).Find(tx, new BsonDocument("poolId", source.PoolId)).AnyAsync(ct) ||
            await Rows(InventoryCountStockSourceRecords.Associations).Find(tx, new BsonDocument("poolId", source.PoolId)).AnyAsync(ct) ||
            await Rows(StockCountBson.Collection).Find(tx, new BsonDocument("poolBinding.poolId", source.PoolId)).AnyAsync(ct)) throw InventoryCountStockSourceRecords.Unavailable();
        var transition = await TransitionAsync(source, null, 1L, 0L, 0L, ct);
        await Rows(InventoryCountStockSourceRecords.Transitions).InsertOneAsync(tx, transition, cancellationToken: ct);
        await Rows(InventoryCountStockSourceRecords.Heads).InsertOneAsync(tx, new BsonDocument { { "_id", source.PoolId }, { "schemaVersion", 1 },
            { "kind", "inventory.countsource.head" }, { "location", pool["location"].DeepClone() }, { "setupDecisionId", setupDecisionId },
            { "revision", 1L }, { "lastTransitionId", transition["_id"] }, { "lastHash", transition["transitionHash"] }, { "updatedAt", source.Time }, { "fence", 0L } }, cancellationToken: ct);
        new InventoryRepository(database).RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || clock.UtcNow < at || StockSourceSetupBson.Time(source.Time) > clock.UtcNow)
            throw InventoryCountStockSourceRecords.Unavailable();
    }
    internal async Task<InventoryCountCanonicalWrite> RecordFreshAsync(IClientSessionHandle tx, StockCountService counts,
        StockSourceSetupService sources, BsonDocument observation, InventoryCountPoolCapture capture, CancellationToken ct) {
        await InventoryCountStockSourceStorage.RequireAsync(database, ct); capture.Require(database, tx);
        // Existing historical events are authenticated before admitting this one
        // fresh candidate. Its custom full-row receipt is already persisted.
        var (head, history, _) = await records.HistoryAsync(tx, counts, sources, capture.Binding.PoolId, ct);
        if (head["revision"].AsInt64 >= InventoryCountStockSourceRecords.MaximumRows || head["fence"].AsInt64 >= long.MaxValue - 2 ||
            StockSourceSetupBson.Time(observation["recordedAt"]) < StockSourceSetupBson.Time(head["updatedAt"])) throw InventoryCountStockSourceRecords.Unavailable();
        var association = InventoryCountStockSourceRecords.Association(observation, capture);
        await Rows(InventoryCountStockSourceRecords.Associations).InsertOneAsync(tx, association, cancellationToken: ct);
        var source = await records.EventAsync(tx, counts, sources, "count", observation["_id"].AsInt64, true, ct);
        if (source.Record["observation"] != observation || source.Record["association"] != association || source.PoolId != capture.Binding.PoolId)
            throw InventoryCountStockSourceRecords.Unavailable();
        await records.RequireSourcesAsync(tx, counts, sources, source.PoolId, history, source, ct);
        var proof = await InventoryCountCanonicalWrite.FenceAsync(database, tx, capture, observation, ct);
        if (proof.Revision < history[^1]["poolRevision"].AsInt64 || proof.BeforeFence < history[^1]["afterPoolFence"].AsInt64)
            throw InventoryCountStockSourceRecords.Unavailable();
        var transition = await TransitionAsync(source, history[^1], proof.Revision, proof.BeforeFence, proof.AfterFence, ct);
        await Rows(InventoryCountStockSourceRecords.Transitions).InsertOneAsync(tx, transition, cancellationToken: ct);
        if ((await Rows(InventoryCountStockSourceRecords.Heads).UpdateOneAsync(tx, head,
            Builders<BsonDocument>.Update.Inc("revision", 1L).Inc("fence", 1L).Set("lastTransitionId", transition["_id"])
                .Set("lastHash", transition["transitionHash"]).Set("updatedAt", source.Time), cancellationToken: ct)).ModifiedCount != 1)
            throw InventoryCountStockSourceRecords.Changed();
        await proof.RequireAsync(database, tx, observation, ct); proof.Consume(); return proof;
    }
    private async Task<BsonDocument> TransitionAsync(InventoryCountStockSourceEvent source, BsonDocument? previous,
        long revision, long before, long after, CancellationToken ct) {
        var row = new BsonDocument { { "_id", (await ids.NextAsync(InventoryCountStockSourceRecords.Transitions, ct)).Value }, { "schemaVersion", 1 },
            { "kind", "inventory.countsource.transition" }, { "poolId", source.PoolId }, { "revision", previous is null ? 1L : previous["revision"].AsInt64 + 1 },
            { "previousId", previous is null ? BsonNull.Value : previous["_id"] }, { "previousHash", previous is null ? BsonNull.Value : previous["transitionHash"] },
            { "sourceKind", source.Kind }, { "sourceId", source.Id }, { "sourceHash", source.Hash }, { "sourceAuditId", source.Audit["_id"] },
            { "actorId", source.Audit["actorId"] }, { "actorGrantVersion", source.Audit["actorGrantVersion"] }, { "recordedAt", source.Time },
            { "poolRevision", revision }, { "beforePoolFence", before }, { "afterPoolFence", after },
            { "observationId", source.Kind == "count" ? new BsonInt64(source.Id) : BsonNull.Value } };
        row.Add("transitionHash", InventoryCountStockSourceRecords.Hash(row, "transitionHash")); return row;
    }
}
