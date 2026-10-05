using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Inventory.StockSourceReconciliationBson;

namespace UnitToko.Platform.Inventory;

internal sealed record ReconciliationStockSourceSnapshot(BsonDocument Head, long OpenCount);
internal sealed class ReconciliationStockSourceRecords(IMongoDatabase database)
{
    internal const string Heads = "reconciliationStockSourceHeads", Transitions = "reconciliationStockSourceTransitions";
    internal const int MaximumRows = 4096;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static PersistenceConflictException Unavailable() => B.Unavailable();
    internal static PersistenceConflictException Changed() => B.Changed();
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "transitionHash")));
    internal async Task<BsonDocument[]> BoundedAsync(string name, FilterDefinition<BsonDocument> filter, IClientSessionHandle tx, CancellationToken ct)
    {
        var rows = await Rows(name).Find(tx, filter).Sort(new BsonDocument("_id", 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (rows.Count > MaximumRows) throw Unavailable(); return rows.ToArray();
    }
    internal async Task<(BsonDocument Head, BsonDocument[] History, Dictionary<long, bool> Open)> HistoryAsync(
        IClientSessionHandle tx, StockSourceSetupService owner, long poolId, CancellationToken ct)
    {
        var repository = new InventoryRepository(database); repository.RequireSession(tx);
        var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var head = await Rows(Heads).Find(tx, F.Eq("_id", poolId) & ReconciliationStockSourceSchemas.Validator(Heads)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        var history = await Rows(Transitions).Find(tx, F.Eq("poolId", poolId)).Sort(new BsonDocument("revision", 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (history.Count is < 1 or > MaximumRows || head["revision"].AsInt64 != history.Count) throw Unavailable();
        var open = new Dictionary<long, bool>(); var events = new HashSet<string>(); BsonDocument? previous = null; BsonValue? location = null;
        foreach (var row in history) {
            if (!await Rows(Transitions).Find(tx, F.Eq("_id", row["_id"]) & ReconciliationStockSourceSchemas.Validator(Transitions)).AnyAsync(ct) ||
                row["revision"].AsInt64 != (previous is null ? 1L : checked(previous["revision"].AsInt64 + 1)) ||
                row["previousId"] != (previous is null ? BsonNull.Value : previous["_id"]) ||
                row["previousHash"] != (previous is null ? BsonNull.Value : previous["transitionHash"]) || row["transitionHash"] != Hash(row) ||
                previous is not null && row["recordedAt"].CompareTo(previous["recordedAt"]) < 0) throw Unavailable();
            var kind = row["sourceKind"].AsString; var id = row["sourceId"].AsInt64;
            if (!events.Add(kind + ":" + id) || (previous is null) != (kind == "setup")) throw Unavailable();
            var source = await owner.ReadReconciliationStockSourceEventAsync(tx, kind, id, true, ct);
            if (source.PoolId != poolId || row["sourceHash"] != source.Hash || row["sourceAuditId"] != source.Audit["_id"] ||
                row["actorId"] != source.Audit["actorId"] || row["actorGrantVersion"] != source.Audit["actorGrantVersion"] || row["recordedAt"] != source.Time)
                throw Unavailable();
            if (kind == "setup") {
                if (id != head["setupDecisionId"].AsInt64 || row["poolRevision"] != 1L || row["beforePoolFence"] != 0L || row["afterPoolFence"] != 0L ||
                    !row["proposalId"].IsBsonNull || !row["beforeOpen"].IsBsonNull || !row["afterOpen"].IsBsonNull) throw Unavailable();
                location = source.Record["proposal"]["input"]["location"];
            } else {
                if (row["afterPoolFence"].AsInt64 != checked(row["beforePoolFence"].AsInt64 + 1) ||
                    row["poolRevision"].AsInt64 < previous!["poolRevision"].AsInt64 || row["beforePoolFence"].AsInt64 < previous["afterPoolFence"].AsInt64 ||
                    row["proposalId"].IsBsonNull || row["proposalId"].AsInt64 != source.ProposalId) throw Unavailable();
                var proposalId = source.ProposalId!.Value;
                if (kind == "proposal") {
                    if (open.ContainsKey(proposalId) || !row["beforeOpen"].IsBsonNull || row["afterOpen"] != true ||
                        row["poolRevision"] != source.Record["baseline"]["poolRevision"] ||
                        row["beforePoolFence"] != source.Record["baseline"]["poolFence"] || row["afterPoolFence"] != source.Record["committedPoolFence"])
                        throw Unavailable();
                    open.Add(proposalId, true);
                } else {
                    if (!open.TryGetValue(proposalId, out var wasOpen) || !wasOpen || row["beforeOpen"] != true || row["afterOpen"] != false ||
                        row["beforePoolFence"].AsInt64 < source.Record["proposal"]["committedPoolFence"].AsInt64) throw Unavailable();
                    open[proposalId] = false;
                }
            }
            previous = row;
        }
        if (previous is null || location is null || head["location"] != location || head["lastTransitionId"] != previous["_id"] ||
            head["lastHash"] != previous["transitionHash"] || head["updatedAt"] != previous["recordedAt"]) throw Unavailable();
        repository.RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw Unavailable();
        return (head, history.ToArray(), open);
    }
    internal async Task<ReconciliationStockSourceSnapshot> ReadAsync(IClientSessionHandle tx, StockSourceSetupService owner,
        StockSourceObligationExpectation expected, CancellationToken ct)
    {
        var (head, history, open) = await HistoryAsync(tx, owner, expected.PoolId, ct);
        if (head["location"] != StockSourceSetupBson.Location(expected.Location)) throw Unavailable();
        var pool = await Rows("inventoryPools").Find(tx, F.Eq("_id", expected.PoolId)).SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
        if (pool!["location"] != head["location"] || pool["revision"] != expected.PoolRevision || pool["fence"] != expected.PoolFence) throw Changed();
        if (pool["revision"].AsInt64 < history[^1]["poolRevision"].AsInt64 || pool["fence"].AsInt64 < history[^1]["afterPoolFence"].AsInt64) throw Unavailable();
        var (proposals, decisions) = await RequireSourcesAsync(tx, expected.PoolId, history, null, ct);
        if (open.Count != proposals.Length) throw Unavailable();
        foreach (var row in decisions)
            if (!open.TryGetValue(row["proposalId"].AsInt64, out var value) || value ||
                proposals.Count(proposal => proposal["_id"] == row["proposalId"] && proposal["poolId"] == row["poolId"]) != 1) throw Unavailable();
        return new(head, open.LongCount(entry => entry.Value));
    }
    internal async Task<(BsonDocument[] Proposals, BsonDocument[] Decisions)> RequireSourcesAsync(IClientSessionHandle tx,
        long poolId, BsonDocument[] history, ReconciliationStockSourceEvent? pending, CancellationToken ct)
    {
        foreach (var name in StockSourceReconciliationSchemas.Collections) {
            if (await Rows(name).Find(tx, new BsonDocument("$nor", new BsonArray { StockSourceReconciliationSchemas.Validator(name) })).Limit(1).AnyAsync(ct)) throw Unavailable();
        }
        var proposals = await BoundedAsync(B.Proposals, F.Eq("poolId", poolId), tx, ct);
        var decisions = await BoundedAsync(B.Decisions, F.Eq("poolId", poolId) | F.In("proposalId", proposals.Select(row => row["_id"])), tx, ct);
        var actual = new HashSet<string> { "setup:" + history[0]["sourceId"].AsInt64 };
        foreach (var row in proposals) actual.Add("proposal:" + row["_id"].AsInt64);
        foreach (var row in decisions) actual.Add("decision:" + row["_id"].AsInt64);
        var authenticated = history.Select(row => row["sourceKind"].AsString + ":" + row["sourceId"].AsInt64).ToHashSet();
        if (pending is not null && (pending.PoolId != poolId || !authenticated.Add(pending.Kind + ":" + pending.Id))) throw Unavailable();
        if (!actual.SetEquals(authenticated)) throw Unavailable();
        foreach (var row in decisions)
            if (proposals.Count(proposal => proposal["_id"] == row["proposalId"] && proposal["poolId"] == row["poolId"]) != 1) throw Unavailable();
        await RequireMembershipAsync(tx, poolId, proposals, decisions, pending, ct);
        return (proposals, decisions);
    }
    private async Task RequireMembershipAsync(IClientSessionHandle tx, long poolId, BsonDocument[] proposals,
        BsonDocument[] decisions, ReconciliationStockSourceEvent? pending, CancellationToken ct)
    {
        // Complete reverse effects for this exact pool and its immutable producer-linked sources.
        var rows = proposals.Concat(decisions).ToArray();
        var audits = await BoundedAsync("auditEvents", F.Eq("kind", "stock.source.reconciliation.audit") &
            (F.Eq("record.poolId", poolId) | F.In("_id", rows.Select(row => row["auditId"])) |
            F.In("record.proposalId", proposals.Select(row => row["_id"])) |
            (F.Eq("operation", B.ProposeOperation) & F.In("record._id", proposals.Select(row => row["_id"]))) |
            (F.Eq("operation", B.DecideOperation) & F.In("record._id", decisions.Select(row => row["_id"])))), tx, ct);
        var outbox = await BoundedAsync("outboxEvents", F.In("kind", new[] { B.ProposeOperation, B.DecideOperation }) &
            (F.Eq("poolId", poolId) | F.In("payload.proposalId", proposals.Select(row => row["_id"])) |
            F.In("payload.decisionId", decisions.Select(row => row["_id"])) | F.In("sourceKey", rows.Select(row =>
                (row["kind"] == "stock.source.reconciliation.proposal" ? B.ProposeOperation : B.DecideOperation) + ":" + row["_id"].AsInt64))), tx, ct);
        var receipts = await BoundedAsync("idempotencyRecords", F.In("operation", new[] { B.ProposeOperation, B.DecideOperation }) &
            (F.Eq("result.poolId", poolId) | F.In("result.proposalId", proposals.Select(row => row["_id"])) |
            F.In("result.decisionId", decisions.Select(row => row["_id"]))), tx, ct);
        if (audits.Length != rows.Length || outbox.Length != rows.Length || receipts.Length != rows.Length - (pending is null ? 0 : 1)) throw Unavailable();
    }
}
