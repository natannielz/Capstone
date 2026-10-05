using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Fulfillment;
internal sealed class AfterSalesStockSourceProducer(IMongoDatabase database, IdAllocator ids, IClock clock)
{
    private readonly AfterSalesStockSourceRecords records = new(database);
    private readonly AfterSalesStockSourceEvents owner = new(database, ids, clock);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task InitializeAsync(IClientSessionHandle tx, long decisionId, CancellationToken ct)
    {
        await AfterSalesStockSourceStorage.RequireAsync(database, ct);
        new InventoryRepository(database).RequireSession(tx);
        var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var capturedAt = clock.UtcNow;
        var source = await owner.ReadAsync(tx, "setup", decisionId, false, ct); var poolId = source.PoolIds.Single();
        var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", poolId)).SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
        if (pool!["active"].AsBoolean || pool["revision"] != 1L || pool["fence"] != 0L || pool["location"] != source.Record["proposal"]["input"]["location"] ||
            await Rows(AfterSalesStockSourceRecords.Heads).Find(tx, new BsonDocument("_id", poolId)).AnyAsync(ct) ||
            await Rows(AfterSalesStockSourceRecords.Transitions).Find(tx, new BsonDocument("poolId", poolId)).AnyAsync(ct) ||
            await Rows(ReturnQuarantineLedger.Collection).Find(tx, new BsonDocument("source.stock.originalLots.poolId", poolId)).AnyAsync(ct)) throw AfterSalesStockSourceRecords.Unavailable();
        await owner.OrphansAsync(tx, ct); Time(source);
        var transition = await TransitionAsync(source, poolId, null, 1L, 0L, 0L, ct);
        await Rows(AfterSalesStockSourceRecords.Transitions).InsertOneAsync(tx, transition, cancellationToken: ct);
        await Rows(AfterSalesStockSourceRecords.Heads).InsertOneAsync(tx, new BsonDocument { { "_id", poolId }, { "schemaVersion", 1 }, { "kind", "aftersales.stocksource.head" },
            { "location", pool["location"].DeepClone() }, { "setupDecisionId", decisionId }, { "revision", 1L }, { "lastTransitionId", transition["_id"] },
            { "lastHash", transition["transitionHash"] }, { "updatedAt", source.Time }, { "fence", 0L } }, cancellationToken: ct);
        new InventoryRepository(database).RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || clock.UtcNow < capturedAt) throw AfterSalesStockSourceRecords.Unavailable();
        Time(source); ct.ThrowIfCancellationRequested();
    }
    internal async Task<Action> RecordFreshAsync(IClientSessionHandle tx, ActorContext actor, AuthorizationSnapshot authority, AuthorizationStore authorization,
        string kind, BsonDocument freshRow, Action current, CancellationToken ct)
    {
        await AfterSalesStockSourceStorage.RequireAsync(database, ct); new InventoryRepository(database).RequireSession(tx); current();
        var source = await owner.ReadAsync(tx, kind, freshRow["_id"].AsInt64, true, ct);
        var field = kind switch { "receive" => "return", "inspect" => "inspection", "verifyLot" => "verification", _ => throw AfterSalesStockSourceRecords.Unavailable() };
        if (source.Record[field] != freshRow) throw AfterSalesStockSourceRecords.Unavailable(); Time(source);
        var writes = new List<AfterSalesStockSourcePoolWrite>();
        foreach (var poolId in source.PoolIds) {
            var (head, history, open) = await records.HistoryAsync(tx, owner, poolId, ct);
            await records.RequireSourcesAsync(tx, owner, poolId, history, source, ct);
            if (head["revision"].AsInt64 >= AfterSalesStockSourceRecords.MaximumRows || head["fence"].AsInt64 >= long.MaxValue - 2 ||
                source.Time.CompareTo(head["updatedAt"]) < 0 || kind == "receive" && open.Contains(source.Id) || kind != "receive" && !open.Contains(source.Id))
                throw AfterSalesStockSourceRecords.Unavailable();
            var before = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", poolId)).SingleOrDefaultAsync(ct) ?? throw AfterSalesStockSourceRecords.Unavailable();
            var proof = await AfterSalesStockSourcePoolWrite.FenceAsync(database, authorization, tx, actor, authority, source, before,
                kind == "receive" ? Permissions.StockReceive : Permissions.StockCount, current, clock, ct);
            await proof.RequireAsync(database, tx, ct);
            if (head["location"] != proof.Location || proof.Kind != kind || proof.SourceId != source.Id || proof.SourceHash != source.Hash ||
                proof.PoolRevision < history[^1]["poolRevision"].AsInt64 || proof.BeforeFence < history[^1]["afterPoolFence"].AsInt64) throw AfterSalesStockSourceRecords.Unavailable();
            var transition = await TransitionAsync(source, poolId, history[^1], proof.PoolRevision, proof.BeforeFence, proof.AfterFence, ct);
            await Rows(AfterSalesStockSourceRecords.Transitions).InsertOneAsync(tx, transition, cancellationToken: ct);
            if ((await Rows(AfterSalesStockSourceRecords.Heads).UpdateOneAsync(tx, head, Builders<BsonDocument>.Update.Inc("revision", 1L).Inc("fence", 1L)
                .Set("lastTransitionId", transition["_id"]).Set("lastHash", transition["transitionHash"]).Set("updatedAt", source.Time), cancellationToken: ct)).ModifiedCount != 1)
                throw AfterSalesStockSourceRecords.Changed();
            await proof.RequireAsync(database, tx, ct); proof.Consume(); writes.Add(proof);
        }
        current(); ct.ThrowIfCancellationRequested();
        return () => { current(); foreach (var write in writes) write.RequireCurrent(); Time(source); };
    }
    private void Time(AfterSalesStockSourceEvent source) { if (clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero || clock.UtcNow < StockSourceSetupBson.Time(source.Time)) throw AfterSalesStockSourceRecords.Unavailable(); }
    private async Task<BsonDocument> TransitionAsync(AfterSalesStockSourceEvent source, long poolId, BsonDocument? previous, long revision, long before, long after, CancellationToken ct)
    {
        var row = new BsonDocument { { "_id", (await ids.NextAsync(AfterSalesStockSourceRecords.Transitions, ct)).Value }, { "schemaVersion", 1 }, { "kind", "aftersales.stocksource.transition" },
            { "poolId", poolId }, { "revision", previous is null ? 1L : checked(previous["revision"].AsInt64 + 1) }, { "previousId", previous is null ? BsonNull.Value : previous["_id"] },
            { "previousHash", previous is null ? BsonNull.Value : previous["transitionHash"] }, { "sourceKind", source.Kind }, { "sourceId", source.Id }, { "sourceHash", source.Hash },
            { "sourceAuditId", source.Audit["_id"] }, { "actorId", source.Audit["actorId"] }, { "actorGrantVersion", source.Audit["actorGrantVersion"] }, { "recordedAt", source.Time },
            { "poolRevision", revision }, { "beforePoolFence", before }, { "afterPoolFence", after }, { "returnId", source.ReturnId.HasValue ? new BsonInt64(source.ReturnId.Value) : BsonNull.Value } };
        row.Add("transitionHash", AfterSalesStockSourceRecords.Hash(row)); return row;
    }
}
