using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Fulfillment;

/// <summary>Only fresh setup/dispatch/recipient confirmation owner callbacks append. Existing
/// original-key replay/recovery does not repair history. Its additional canonical fence changes
/// neither stock quantity nor pool revision/authority and preserves each command's final guards.</summary>
internal sealed class FulfillmentStockSourceProducer(IMongoDatabase database, IdAllocator ids, IClock clock)
{
    private readonly FulfillmentStockSourceRecords records = new(database);
    private readonly FulfillmentStockSourceEvents owner = new(database, ids, clock);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task InitializeAsync(IClientSessionHandle tx, long setupDecisionId, CancellationToken ct)
    {
        await FulfillmentStockSourceStorage.RequireAsync(database, ct);
        var repository = new InventoryRepository(database); repository.RequireSession(tx);
        var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var source = await owner.ReadAsync(tx, "setup", setupDecisionId, false, ct);
        var poolId = source.PoolIds.Single();
        var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", poolId)).SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
        if (pool!["active"].AsBoolean || pool["revision"] != 1L || pool["fence"] != 0L ||
            pool["location"] != source.Record["proposal"]["input"]["location"] ||
            await Rows(FulfillmentStockSourceRecords.Heads).Find(tx, new BsonDocument("_id", poolId)).AnyAsync(ct) ||
            await Rows(FulfillmentStockSourceRecords.Transitions).Find(tx, new BsonDocument("poolId", poolId)).AnyAsync(ct) ||
            await Rows("shipments").Find(tx, new BsonDocument("stockEffects.allocation.poolId", poolId)).AnyAsync(ct))
            throw FulfillmentStockSourceRecords.Unavailable();
        await records.RequireRetainedRootsAsync(tx, ct);
        RequireTime(source);
        var transition = await TransitionAsync(source, poolId, null, 1L, 0L, 0L, null, null, ct);
        var head = new BsonDocument { { "_id", poolId }, { "schemaVersion", 1 }, { "kind", "fulfillment.stocksource.head" },
            { "location", pool["location"].DeepClone() }, { "setupDecisionId", setupDecisionId }, { "revision", 1L },
            { "lastTransitionId", transition["_id"] }, { "lastHash", transition["transitionHash"] }, { "updatedAt", source.Time }, { "fence", 0L } };
        await Rows(FulfillmentStockSourceRecords.Transitions).InsertOneAsync(tx, transition, cancellationToken: ct);
        await Rows(FulfillmentStockSourceRecords.Heads).InsertOneAsync(tx, head, cancellationToken: ct);
        repository.RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw FulfillmentStockSourceRecords.Unavailable();
        ct.ThrowIfCancellationRequested();
    }
    internal async Task RecordAsync(IClientSessionHandle tx, string kind, long sourceId, CancellationToken ct)
    {
        if (kind is not ("dispatch" or "receipt")) throw FulfillmentStockSourceRecords.Unavailable();
        await FulfillmentStockSourceStorage.RequireAsync(database, ct);
        var source = await owner.ReadAsync(tx, kind, sourceId, false, ct);
        RequireTime(source);
        foreach (var poolId in source.PoolIds.Order()) {
            var beforePool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", poolId)).SingleOrDefaultAsync(ct);
            InventoryGraph.RequirePool(beforePool);
            // A real factory performs this exact CAS; no handle is invented for an earlier
            // Inventory/Ordering numeric fence. This is an additional documented owning fence.
            var proof = await FulfillmentStockSourcePoolWrite.FenceAsync(database, tx, beforePool!, ct);
            await AppendAsync(tx, source, proof, ct);
        }
    }
    private async Task AppendAsync(IClientSessionHandle tx, FulfillmentStockSourceEvent source,
        FulfillmentStockSourcePoolWrite proof, CancellationToken ct)
    {
        await proof.RequireAsync(database, tx, ct);
        var (head, history, open) = await records.HistoryAsync(tx, owner, proof.PoolId, ct);
        if (!source.PoolIds.Contains(proof.PoolId) || head["revision"].AsInt64 >= FulfillmentStockSourceRecords.MaximumRows ||
            head["fence"].AsInt64 >= long.MaxValue - 2 || head["location"] != proof.Location ||
            proof.PoolRevision < history[^1]["poolRevision"].AsInt64 || proof.BeforeFence < history[^1]["afterPoolFence"].AsInt64 ||
            source.Time.CompareTo(head["updatedAt"]) < 0) throw FulfillmentStockSourceRecords.Unavailable();
        _ = await records.RequireSourcesAsync(tx, proof.PoolId, history, source, ct);
        var shipmentId = source.ShipmentId ?? throw FulfillmentStockSourceRecords.Unavailable();
        if (source.Kind == "dispatch") {
            if (open.ContainsKey(shipmentId) || source.Open != true) throw FulfillmentStockSourceRecords.Unavailable();
        } else if (!open.TryGetValue(shipmentId, out var wasOpen) || !wasOpen || !source.Open.HasValue)
            throw FulfillmentStockSourceRecords.Unavailable();
        var transition = await TransitionAsync(source, proof.PoolId, history[^1], proof.PoolRevision, proof.BeforeFence, proof.AfterFence,
            source.Kind == "dispatch" ? null : true, source.Open, ct);
        await Rows(FulfillmentStockSourceRecords.Transitions).InsertOneAsync(tx, transition, cancellationToken: ct);
        if ((await Rows(FulfillmentStockSourceRecords.Heads).UpdateOneAsync(tx, head, Builders<BsonDocument>.Update.Inc("revision", 1L).Inc("fence", 1L)
            .Set("lastTransitionId", transition["_id"]).Set("lastHash", transition["transitionHash"]).Set("updatedAt", source.Time), cancellationToken: ct)).ModifiedCount != 1)
            throw FulfillmentStockSourceRecords.Changed();
        await proof.RequireAsync(database, tx, ct); ct.ThrowIfCancellationRequested(); proof.Consume();
    }
    private void RequireTime(FulfillmentStockSourceEvent source)
    {
        var now = clock.UtcNow;
        if (now == default || now.Offset != TimeSpan.Zero || now < StockSourceSetupBson.Time(source.Time)) throw FulfillmentStockSourceRecords.Unavailable();
    }
    private async Task<BsonDocument> TransitionAsync(FulfillmentStockSourceEvent source, long poolId, BsonDocument? previous,
        long poolRevision, long beforeFence, long afterFence, bool? beforeOpen, bool? afterOpen, CancellationToken ct)
    {
        var row = new BsonDocument { { "_id", (await ids.NextAsync(FulfillmentStockSourceRecords.Transitions, ct)).Value },
            { "schemaVersion", 1 }, { "kind", "fulfillment.stocksource.transition" }, { "poolId", poolId },
            { "revision", previous is null ? 1L : checked(previous["revision"].AsInt64 + 1) },
            { "previousId", previous is null ? BsonNull.Value : previous["_id"] }, { "previousHash", previous is null ? BsonNull.Value : previous["transitionHash"] },
            { "sourceKind", source.Kind }, { "sourceId", source.Id }, { "sourceHash", source.Hash }, { "sourceAuditId", source.Audit["_id"] },
            { "actorId", source.Audit["actorId"] }, { "actorGrantVersion", source.Audit["actorGrantVersion"] }, { "recordedAt", source.Time },
            { "poolRevision", poolRevision }, { "beforePoolFence", beforeFence }, { "afterPoolFence", afterFence },
            { "shipmentId", source.ShipmentId.HasValue ? new BsonInt64(source.ShipmentId.Value) : BsonNull.Value },
            { "beforeOpen", beforeOpen.HasValue ? new BsonBoolean(beforeOpen.Value) : BsonNull.Value },
            { "afterOpen", afterOpen.HasValue ? new BsonBoolean(afterOpen.Value) : BsonNull.Value } };
        row.Add("transitionHash", FulfillmentStockSourceRecords.Hash(row)); return row;
    }
}
