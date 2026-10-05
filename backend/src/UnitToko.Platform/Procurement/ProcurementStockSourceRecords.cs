using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementStockSourceSnapshot(BsonDocument Head, long HeldReceiptCount, long OpenCount);

/// <summary>Authenticated membership of actually bound held receipts only. Upstream
/// inbound membership is unavailable; this owner cannot prove complete Procurement drain.</summary>
internal sealed class ProcurementStockSourceRecords(IMongoDatabase database)
{
    internal const string Heads = "procurementHeldStockSourceHeads", Transitions = "procurementHeldStockSourceTransitions";
    internal const int MaximumRows = 4096;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    // Preserve existing owning receipt failure contracts for future callers.
    internal static PersistenceConflictException Unavailable() => B.Broken();
    internal static PersistenceConflictException Changed() => B.Changed();
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "transitionHash")));
    internal async Task<BsonDocument[]> BoundedAsync(string name, FilterDefinition<BsonDocument> filter,
        IClientSessionHandle tx, CancellationToken ct)
    {
        var rows = await Rows(name).Find(tx, filter).Sort(new BsonDocument("_id", 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (rows.Count > MaximumRows) throw Unavailable(); return rows.ToArray();
    }
    internal async Task<(BsonDocument Head, BsonDocument[] History, Dictionary<long, ProcurementStockSourceEvent> Receipts)>
        HistoryAsync(IClientSessionHandle tx, ProcurementStockSourceEvents owner, long poolId, CancellationToken ct)
    {
        var repository = new InventoryRepository(database); repository.RequireSession(tx);
        var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var head = await Rows(Heads).Find(tx, F.Eq("_id", poolId) & ProcurementStockSourceSchemas.Validator(Heads))
            .SingleOrDefaultAsync(ct) ?? throw Unavailable();
        var history = await Rows(Transitions).Find(tx, F.Eq("poolId", poolId)).Sort(new BsonDocument("revision", 1))
            .Limit(MaximumRows + 1).ToListAsync(ct);
        if (history.Count is < 1 or > MaximumRows || head["revision"].AsInt64 != history.Count) throw Unavailable();
        var receipts = new Dictionary<long, ProcurementStockSourceEvent>(); BsonDocument? previous = null; BsonValue? location = null;
        foreach (var row in history) {
            if (!await Rows(Transitions).Find(tx, F.Eq("_id", row["_id"]) & ProcurementStockSourceSchemas.Validator(Transitions)).AnyAsync(ct) ||
                row["revision"].AsInt64 != (previous is null ? 1L : checked(previous["revision"].AsInt64 + 1)) ||
                row["previousId"] != (previous is null ? BsonNull.Value : previous["_id"]) ||
                row["previousHash"] != (previous is null ? BsonNull.Value : previous["transitionHash"]) ||
                row["transitionHash"] != Hash(row) || previous is not null && row["recordedAt"].CompareTo(previous["recordedAt"]) < 0)
                throw Unavailable();
            var kind = row["sourceKind"].AsString; var id = row["sourceId"].AsInt64;
            if ((previous is null) != (kind == "setup")) throw Unavailable();
            var source = await owner.ReadAsync(tx, kind, id, true, ct);
            if (source.PoolId != poolId || row["sourceHash"] != source.Hash || row["sourceAuditId"] != source.Audit["_id"] ||
                row["actorId"] != source.Audit["actorId"] || row["actorGrantVersion"] != source.Audit["actorGrantVersion"] || row["recordedAt"] != source.Time)
                throw Unavailable();
            if (kind == "setup") {
                if (id != head["setupDecisionId"].AsInt64 || row["poolRevision"] != 1L || row["beforePoolFence"] != 0L || row["afterPoolFence"] != 0L ||
                    !row["receiptId"].IsBsonNull || !row["lotId"].IsBsonNull || !row["balanceId"].IsBsonNull) throw Unavailable();
                location = source.Record["proposal"]["input"]["location"];
            } else {
                if (kind != "heldReceipt" || source.Movement is null || !receipts.TryAdd(id, source) || row["receiptId"] != id ||
                    row["lotId"] != source.Record["lotId"] || row["balanceId"] != source.Record["balanceId"] ||
                    row["afterPoolFence"].AsInt64 != checked(row["beforePoolFence"].AsInt64 + 1) ||
                    row["poolRevision"].AsInt64 < previous!["poolRevision"].AsInt64 || row["beforePoolFence"].AsInt64 < previous["afterPoolFence"].AsInt64 ||
                    row["poolRevision"] != source.Record["input"]["poolRevision"] || row["beforePoolFence"] != source.Record["poolSource"]["poolFence"])
                    throw Unavailable();
            }
            previous = row;
        }
        if (previous is null || location is null || head["location"] != location || head["lastTransitionId"] != previous["_id"] ||
            head["lastHash"] != previous["transitionHash"] || head["updatedAt"] != previous["recordedAt"]) throw Unavailable();
        repository.RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw Unavailable();
        return (head, history.ToArray(), receipts);
    }
    internal async Task<ProcurementStockSourceSnapshot> ReadAsync(IClientSessionHandle tx, ProcurementStockSourceEvents owner,
        StockSourceObligationExpectation expected, CancellationToken ct)
    {
        var (head, history, receipts) = await HistoryAsync(tx, owner, expected.PoolId, ct);
        if (head["location"] != StockSourceSetupBson.Location(expected.Location)) throw Unavailable();
        var pool = await Rows("inventoryPools").Find(tx, F.Eq("_id", expected.PoolId)).SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
        if (pool!["location"] != head["location"] || pool["revision"] != expected.PoolRevision || pool["fence"] != expected.PoolFence) throw Changed();
        if (pool["revision"].AsInt64 < history[^1]["poolRevision"].AsInt64 || pool["fence"].AsInt64 < history[^1]["afterPoolFence"].AsInt64) throw Unavailable();
        await RequireSourcesAsync(tx, expected.PoolId, history, null, ct);
        await owner.OrphansAsync(tx, ct);
        // A nonzero current custody quantity remains an obligation even after quality rejection.
        var open = receipts.Values.LongCount(value => value.Movement is null || value.Movement.Current.Balance.OnHand > 0 ||
            value.Movement.Current.Balance.Held > 0 || value.Movement.Current.Balance.Reserved > 0);
        return new(head, receipts.Count, open);
    }
    internal async Task RequireSourcesAsync(IClientSessionHandle tx, long poolId, BsonDocument[] history,
        ProcurementStockSourceEvent? pending, CancellationToken ct)
    {
        var receipts = await BoundedAsync(B.Receipts, F.Eq("poolId", poolId), tx, ct);
        var expected = receipts.Select(row => row["_id"].AsInt64).ToHashSet();
        if (expected.Count != receipts.Length) throw Unavailable();
        var actual = history.Where(row => row["sourceKind"] == "heldReceipt").Select(row => row["sourceId"].AsInt64).ToHashSet();
        if (pending is not null && (pending.Kind != "heldReceipt" || pending.PoolId != poolId || !actual.Add(pending.Id))) throw Unavailable();
        if (!actual.SetEquals(expected)) throw Unavailable();
        var ids = receipts.Select(row => row["_id"]).ToArray();
        var audits = await BoundedAsync("auditEvents", F.Eq("kind", B.Operation) &
            (F.In("_id", receipts.Select(row => row["auditId"])) | F.In("result.receiptId", ids)), tx, ct);
        var outbox = await BoundedAsync("outboxEvents", F.Eq("kind", B.Operation) &
            (F.In("sourceKey", receipts.Select(B.EffectKey)) | F.In("payload.receiptId", ids)), tx, ct);
        var commands = await BoundedAsync("idempotencyRecords", F.Eq("operation", B.Operation) & F.In("result.receiptId", ids), tx, ct);
        // Immutable root lots do not carry a pool field. The genuine receipt owns
        // that binding; do not infer it from current aliases or add a fictional field.
        var lots = await BoundedAsync("lots", F.Eq("origin", "procurementReceipt") & F.In("sourceId", ids), tx, ct);
        var movements = await BoundedAsync("movements", F.Eq("source.namespace", B.Operation) &
            (F.Eq("physicalPoolId", poolId) | F.In("source.documentId", ids)), tx, ct);
        if (audits.Length != receipts.Length || outbox.Length != receipts.Length || commands.Length != receipts.Length ||
            lots.Length != receipts.Length || movements.Length != receipts.Length) throw Unavailable();
        foreach (var lot in lots) {
            var receipt = receipts.SingleOrDefault(row => row["lotId"] == lot["_id"] && row["_id"] == lot["sourceId"]) ?? throw Unavailable();
            if (lot != InventoryHeldReceiptWriter.Lot(receipt)) throw Unavailable();
        }
        foreach (var movement in movements)
            if (receipts.Count(row => row["movementId"] == movement["_id"] && row["_id"] == movement["source"]["documentId"]) != 1) throw Unavailable();
    }
}
