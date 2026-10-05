using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Inventory.StockSourceReconciliationBson;

namespace UnitToko.Platform.Inventory;

/// <summary>Fresh owning transactions only. Existing receipts/recovery never create missing
/// producer history. The command supplies the proof from its one actual canonical-pool CAS.</summary>
internal sealed class ReconciliationStockSourceProducer(IMongoDatabase database, IdAllocator ids, IClock clock)
{
    private readonly ReconciliationStockSourceRecords records = new(database);
    private readonly StockSourceSetupService owner = new(database,
        new(database.Client as MongoClient ?? throw ReconciliationStockSourceRecords.Unavailable()), ids, clock);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task InitializeAsync(IClientSessionHandle tx, long setupDecisionId, CancellationToken ct)
    {
        await ReconciliationStockSourceStorage.RequireAsync(database, ct);
        var repository = new InventoryRepository(database); repository.RequireSession(tx);
        var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var source = await owner.ReadReconciliationStockSourceEventAsync(tx, "setup", setupDecisionId, false, ct);
        var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", source.PoolId)).SingleOrDefaultAsync(ct);
        InventoryGraph.RequirePool(pool);
        if (pool!["active"].AsBoolean || pool["revision"] != 1L || pool["fence"] != 0L ||
            pool["location"] != source.Record["proposal"]["input"]["location"] ||
            await Rows(ReconciliationStockSourceRecords.Heads).Find(tx, new BsonDocument("_id", source.PoolId)).AnyAsync(ct) ||
            await Rows(ReconciliationStockSourceRecords.Transitions).Find(tx, new BsonDocument("poolId", source.PoolId)).AnyAsync(ct) ||
            await Rows(B.Proposals).Find(tx, new BsonDocument("poolId", source.PoolId)).AnyAsync(ct) ||
            await Rows(B.Decisions).Find(tx, new BsonDocument("poolId", source.PoolId)).AnyAsync(ct)) throw ReconciliationStockSourceRecords.Unavailable();
        RequireTime(source);
        var transition = await TransitionAsync(source, null, 1L, 0L, 0L, null, null, ct);
        var head = new BsonDocument { { "_id", source.PoolId }, { "schemaVersion", 1 }, { "kind", "reconciliation.stocksource.head" },
            { "location", pool["location"].DeepClone() }, { "setupDecisionId", setupDecisionId }, { "revision", 1L },
            { "lastTransitionId", transition["_id"] }, { "lastHash", transition["transitionHash"] }, { "updatedAt", source.Time }, { "fence", 0L } };
        await Rows(ReconciliationStockSourceRecords.Transitions).InsertOneAsync(tx, transition, cancellationToken: ct);
        await Rows(ReconciliationStockSourceRecords.Heads).InsertOneAsync(tx, head, cancellationToken: ct);
        repository.RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw ReconciliationStockSourceRecords.Unavailable();
        ct.ThrowIfCancellationRequested();
    }
    internal async Task RecordAsync(IClientSessionHandle tx, string kind, long sourceId,
        ReconciliationStockSourcePoolWrite proof, CancellationToken ct)
    {
        if (kind is not ("proposal" or "decision") || proof is null) throw ReconciliationStockSourceRecords.Unavailable();
        await ReconciliationStockSourceStorage.RequireAsync(database, ct);
        await proof.RequireAsync(database, tx, ct);
        var source = await owner.ReadReconciliationStockSourceEventAsync(tx, kind, sourceId, false, ct);
        if (source.PoolId != proof.PoolId) throw ReconciliationStockSourceRecords.Unavailable();
        var (head, history, open) = await records.HistoryAsync(tx, owner, proof.PoolId, ct);
        if (head["revision"].AsInt64 >= ReconciliationStockSourceRecords.MaximumRows || head["fence"].AsInt64 >= long.MaxValue - 2 ||
            head["location"] != proof.Location || proof.PoolRevision < history[^1]["poolRevision"].AsInt64 ||
            proof.BeforeFence < history[^1]["afterPoolFence"].AsInt64 || source.Time.CompareTo(head["updatedAt"]) < 0)
            throw ReconciliationStockSourceRecords.Unavailable();
        RequireTime(source);
        _ = await records.RequireSourcesAsync(tx, proof.PoolId, history, source, ct);
        var proposalId = source.ProposalId ?? throw ReconciliationStockSourceRecords.Unavailable();
        if (kind == "proposal") {
            if (open.ContainsKey(proposalId) || source.Record["baseline"]["poolRevision"] != proof.PoolRevision ||
                source.Record["baseline"]["poolFence"] != proof.BeforeFence || source.Record["committedPoolFence"] != proof.AfterFence)
                throw ReconciliationStockSourceRecords.Unavailable();
        } else if (!open.TryGetValue(proposalId, out var wasOpen) || !wasOpen ||
            proof.BeforeFence < source.Record["proposal"]["committedPoolFence"].AsInt64) throw ReconciliationStockSourceRecords.Unavailable();
        var transition = await TransitionAsync(source, history[^1], proof.PoolRevision, proof.BeforeFence, proof.AfterFence,
            kind == "proposal" ? null : true, kind == "proposal", ct);
        await Rows(ReconciliationStockSourceRecords.Transitions).InsertOneAsync(tx, transition, cancellationToken: ct);
        var update = Builders<BsonDocument>.Update.Inc("revision", 1L).Inc("fence", 1L).Set("lastTransitionId", transition["_id"])
            .Set("lastHash", transition["transitionHash"]).Set("updatedAt", source.Time);
        if ((await Rows(ReconciliationStockSourceRecords.Heads).UpdateOneAsync(tx, head, update, cancellationToken: ct)).ModifiedCount != 1)
            throw ReconciliationStockSourceRecords.Changed();
        await proof.RequireAsync(database, tx, ct);
        ct.ThrowIfCancellationRequested(); proof.Consume();
        // The pool was already written exactly once by FenceAsync; this append never increments it.
    }
    private void RequireTime(ReconciliationStockSourceEvent source)
    {
        var now = clock.UtcNow;
        if (now == default || now.Offset != TimeSpan.Zero || now < StockSourceSetupBson.Time(source.Time))
            throw ReconciliationStockSourceRecords.Unavailable();
    }
    private async Task<BsonDocument> TransitionAsync(ReconciliationStockSourceEvent source, BsonDocument? previous,
        long poolRevision, long beforeFence, long afterFence, bool? beforeOpen, bool? afterOpen, CancellationToken ct)
    {
        var row = new BsonDocument { { "_id", (await ids.NextAsync(ReconciliationStockSourceRecords.Transitions, ct)).Value },
            { "schemaVersion", 1 }, { "kind", "reconciliation.stocksource.transition" }, { "poolId", source.PoolId },
            { "revision", previous is null ? 1L : checked(previous["revision"].AsInt64 + 1) },
            { "previousId", previous is null ? BsonNull.Value : previous["_id"] }, { "previousHash", previous is null ? BsonNull.Value : previous["transitionHash"] },
            { "sourceKind", source.Kind }, { "sourceId", source.Id }, { "sourceHash", source.Hash }, { "sourceAuditId", source.Audit["_id"] },
            { "actorId", source.Audit["actorId"] }, { "actorGrantVersion", source.Audit["actorGrantVersion"] }, { "recordedAt", source.Time },
            { "poolRevision", poolRevision }, { "beforePoolFence", beforeFence }, { "afterPoolFence", afterFence },
            { "proposalId", source.ProposalId.HasValue ? new BsonInt64(source.ProposalId.Value) : BsonNull.Value },
            { "beforeOpen", beforeOpen.HasValue ? new BsonBoolean(beforeOpen.Value) : BsonNull.Value },
            { "afterOpen", afterOpen.HasValue ? new BsonBoolean(afterOpen.Value) : BsonNull.Value } };
        row.Add("transitionHash", ReconciliationStockSourceRecords.Hash(row)); return row;
    }
}
