using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Procurement;

/// <summary>Actual fresh setup/held-receipt callbacks only. Never called for existing-key
/// replay. Receipt append runs after the generic command result has been persisted.</summary>
internal sealed class ProcurementStockSourceProducer(IMongoDatabase database, IdAllocator ids, IClock clock)
{
    private readonly ProcurementStockSourceRecords records = new(database);
    private readonly ProcurementStockSourceEvents owner = new(database, ids, clock);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task InitializeAsync(IClientSessionHandle tx, long setupDecisionId, CancellationToken ct)
    {
        await ProcurementStockSourceStorage.RequireAsync(database, ct);
        var repository = new InventoryRepository(database); repository.RequireSession(tx);
        var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var source = await owner.ReadAsync(tx, "setup", setupDecisionId, false, ct);
        var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", source.PoolId)).SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
        if (pool!["active"].AsBoolean || pool["revision"] != 1L || pool["fence"] != 0L ||
            pool["location"] != source.Record["proposal"]["input"]["location"] ||
            await Rows(ProcurementStockSourceRecords.Heads).Find(tx, new BsonDocument("_id", source.PoolId)).AnyAsync(ct) ||
            await Rows(ProcurementStockSourceRecords.Transitions).Find(tx, new BsonDocument("poolId", source.PoolId)).AnyAsync(ct) ||
            await Rows(ProcurementStockReceiptBson.Receipts).Find(tx, new BsonDocument("poolId", source.PoolId)).AnyAsync(ct))
            throw ProcurementStockSourceRecords.Unavailable();
        await owner.OrphansAsync(tx, ct); RequireTime(source);
        var transition = await TransitionAsync(source, null, 1L, 0L, 0L, ct);
        var head = new BsonDocument { { "_id", source.PoolId }, { "schemaVersion", 1 }, { "kind", "procurement.heldsource.head" },
            { "location", pool["location"].DeepClone() }, { "setupDecisionId", setupDecisionId }, { "revision", 1L },
            { "lastTransitionId", transition["_id"] }, { "lastHash", transition["transitionHash"] }, { "updatedAt", source.Time }, { "fence", 0L } };
        await Rows(ProcurementStockSourceRecords.Transitions).InsertOneAsync(tx, transition, cancellationToken: ct);
        await Rows(ProcurementStockSourceRecords.Heads).InsertOneAsync(tx, head, cancellationToken: ct);
        repository.RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw ProcurementStockSourceRecords.Unavailable();
        ct.ThrowIfCancellationRequested();
    }
    internal async Task RecordAsync(IClientSessionHandle tx, long receiptId, ProcurementStockSourcePoolWrite proof, CancellationToken ct)
    {
        await ProcurementStockSourceStorage.RequireAsync(database, ct);
        await proof.RequireAsync(database, tx, ct);
        var source = await owner.ReadAsync(tx, "heldReceipt", receiptId, true, ct);
        var (head, history, _) = await records.HistoryAsync(tx, owner, proof.PoolId, ct);
        if (source.Id != proof.ReceiptId || source.Record["snapshotHash"] != proof.ReceiptHash ||
            source.PoolId != proof.PoolId || head["revision"].AsInt64 >= ProcurementStockSourceRecords.MaximumRows ||
            head["fence"].AsInt64 >= long.MaxValue - 2 || head["location"] != proof.Location ||
            proof.Revision < history[^1]["poolRevision"].AsInt64 || proof.BeforeFence < history[^1]["afterPoolFence"].AsInt64 ||
            source.Record["input"]["poolRevision"] != proof.Revision || source.Record["poolSource"]["poolFence"] != proof.BeforeFence ||
            source.Time.CompareTo(head["updatedAt"]) < 0) throw ProcurementStockSourceRecords.Unavailable();
        RequireTime(source);
        await records.RequireSourcesAsync(tx, proof.PoolId, history, source, ct);
        var transition = await TransitionAsync(source, history[^1], proof.Revision, proof.BeforeFence, proof.AfterFence, ct);
        await Rows(ProcurementStockSourceRecords.Transitions).InsertOneAsync(tx, transition, cancellationToken: ct);
        if ((await Rows(ProcurementStockSourceRecords.Heads).UpdateOneAsync(tx, head,
            Builders<BsonDocument>.Update.Inc("revision", 1L).Inc("fence", 1L).Set("lastTransitionId", transition["_id"])
                .Set("lastHash", transition["transitionHash"]).Set("updatedAt", source.Time), cancellationToken: ct)).ModifiedCount != 1)
            throw ProcurementStockSourceRecords.Changed();
        await proof.RequireAsync(database, tx, ct); ct.ThrowIfCancellationRequested(); proof.Consume();
    }
    private void RequireTime(ProcurementStockSourceEvent source)
    {
        var now = clock.UtcNow;
        if (now == default || now.Offset != TimeSpan.Zero || now < StockSourceSetupBson.Time(source.Time)) throw ProcurementStockSourceRecords.Unavailable();
    }
    private async Task<BsonDocument> TransitionAsync(ProcurementStockSourceEvent source, BsonDocument? previous,
        long poolRevision, long beforeFence, long afterFence, CancellationToken ct)
    {
        var isReceipt = source.Kind == "heldReceipt";
        var row = new BsonDocument { { "_id", (await ids.NextAsync(ProcurementStockSourceRecords.Transitions, ct)).Value },
            { "schemaVersion", 1 }, { "kind", "procurement.heldsource.transition" }, { "poolId", source.PoolId },
            { "revision", previous is null ? 1L : checked(previous["revision"].AsInt64 + 1) },
            { "previousId", previous is null ? BsonNull.Value : previous["_id"] }, { "previousHash", previous is null ? BsonNull.Value : previous["transitionHash"] },
            { "sourceKind", source.Kind }, { "sourceId", source.Id }, { "sourceHash", source.Hash }, { "sourceAuditId", source.Audit["_id"] },
            { "actorId", source.Audit["actorId"] }, { "actorGrantVersion", source.Audit["actorGrantVersion"] }, { "recordedAt", source.Time },
            { "poolRevision", poolRevision }, { "beforePoolFence", beforeFence }, { "afterPoolFence", afterFence },
            { "receiptId", isReceipt ? source.Record["_id"] : BsonNull.Value }, { "lotId", isReceipt ? source.Record["lotId"] : BsonNull.Value },
            { "balanceId", isReceipt ? source.Record["balanceId"] : BsonNull.Value } };
        row.Add("transitionHash", ProcurementStockSourceRecords.Hash(row)); return row;
    }
}
