using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Fulfillment;
internal sealed record AfterSalesStockSourceSnapshot(BsonDocument Head, long OpenCount);
internal sealed partial class AfterSalesStockSourceRecords(IMongoDatabase database)
{
    internal const string Heads = "afterSalesStockSourceHeads", Transitions = "afterSalesStockSourceTransitions";
    internal const string CausalHeads = "afterSalesCausalSourceHeads", CausalTransitions = "afterSalesCausalSourceTransitions";
    internal const int MaximumRows = 4096;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static DomainValidationException Unavailable() => new("aftersales_source_obligation_unavailable", "Authentic returned custody and dispatched provenance history is required");
    internal static PersistenceConflictException Changed() => new("stock_source_revision_changed", "The reviewed stock source changed; reload it");
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "transitionHash")));
    internal async Task<(BsonDocument Head, BsonDocument[] History, HashSet<long> Open)> HistoryAsync(IClientSessionHandle tx,
        AfterSalesStockSourceEvents owner, long poolId, CancellationToken ct)
    {
        new InventoryRepository(database).RequireSession(tx);
        var head = await Rows(Heads).Find(tx, F.Eq("_id", poolId) & AfterSalesStockSourceSchemas.Validator(Heads)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        var history = await Rows(Transitions).Find(tx, F.Eq("poolId", poolId)).Sort(new BsonDocument("revision", 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (history.Count is < 1 or > MaximumRows || head["revision"].AsInt64 != history.Count) throw Unavailable();
        var open = new HashSet<long>(); var inspected = new HashSet<long>(); var verified = new HashSet<long>(); var keys = new HashSet<string>();
        BsonDocument? previous = null; BsonValue? location = null;
        foreach (var row in history) {
            if (!await Rows(Transitions).Find(tx, F.Eq("_id", row["_id"]) & AfterSalesStockSourceSchemas.Validator(Transitions)).AnyAsync(ct) ||
                row["revision"].AsInt64 != (previous is null ? 1L : checked(previous["revision"].AsInt64 + 1)) ||
                row["previousId"] != (previous is null ? BsonNull.Value : previous["_id"]) ||
                row["previousHash"] != (previous is null ? BsonNull.Value : previous["transitionHash"]) || row["transitionHash"] != Hash(row) ||
                previous is not null && row["recordedAt"].CompareTo(previous["recordedAt"]) < 0) throw Unavailable();
            var kind = row["sourceKind"].AsString; var id = row["sourceId"].AsInt64;
            if (!keys.Add(kind + ":" + id) || (previous is null) != (kind == "setup")) throw Unavailable();
            var source = await owner.ReadAsync(tx, kind, id, true, ct);
            if (!source.PoolIds.Contains(poolId) || row["sourceHash"] != source.Hash || row["sourceAuditId"] != source.Audit["_id"] ||
                row["actorId"] != source.Audit["actorId"] || row["actorGrantVersion"] != source.Audit["actorGrantVersion"] || row["recordedAt"] != source.Time) throw Unavailable();
            if (kind == "setup") {
                if (id != head["setupDecisionId"].AsInt64 || row["poolRevision"] != 1L || row["beforePoolFence"] != 0L || row["afterPoolFence"] != 0L || !row["returnId"].IsBsonNull) throw Unavailable();
                location = source.Record["proposal"]["input"]["location"];
            } else {
                if (row["afterPoolFence"].AsInt64 != checked(row["beforePoolFence"].AsInt64 + 1) ||
                    row["poolRevision"].AsInt64 < previous!["poolRevision"].AsInt64 || row["beforePoolFence"].AsInt64 < previous["afterPoolFence"].AsInt64 ||
                    row["returnId"].IsBsonNull || row["returnId"].AsInt64 != source.ReturnId) throw Unavailable();
                if (kind == "receive") { if (!open.Add(id)) throw Unavailable(); }
                else if (!open.Contains(id) || kind == "inspect" && !inspected.Add(id) || kind == "verifyLot" && (!inspected.Contains(id) || !verified.Add(id))) throw Unavailable();
            }
            previous = row;
        }
        if (previous is null || location is null || head["location"] != location || head["lastTransitionId"] != previous["_id"] ||
            head["lastHash"] != previous["transitionHash"] || head["updatedAt"] != previous["recordedAt"]) throw Unavailable();
        return (head, history.ToArray(), open);
    }
    internal async Task RequireSourcesAsync(IClientSessionHandle tx, AfterSalesStockSourceEvents owner, long poolId,
        BsonDocument[] history, AfterSalesStockSourceEvent? pending, CancellationToken ct)
    {
        await owner.OrphansAsync(tx, ct);
        var actual = new HashSet<string> { "setup:" + history[0]["sourceId"].AsInt64 };
        var candidates = await Rows(ReturnQuarantineLedger.Collection).Find(tx, F.Eq("source.stock.originalLots.poolId", poolId))
            .Sort(new BsonDocument("_id", 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (candidates.Count > MaximumRows) throw Unavailable();
        foreach (var candidate in candidates) {
            var source = await owner.ReadAsync(tx, "receive", candidate["_id"].AsInt64, true, ct);
            // An authorized-but-not-received SKU can mention other candidate pools in the source.
            // Exact physical input lines decide this returned custody's provenance membership.
            if (!source.PoolIds.Contains(poolId)) continue;
            actual.Add("receive:" + source.Id);
            var inspection = await new ReturnInspectionRecords(database, owner.Clock).ReadAsync(tx, candidate, ct);
            if (inspection is null) continue;
            actual.Add("inspect:" + source.Id);
            var verification = await new ReturnedLotVerificationRecords(database, owner.Clock).ReadAsync(tx, candidate, inspection, ct);
            if (verification is not null) actual.Add("verifyLot:" + source.Id);
            // Resolution requests have an owner, but are proposals rather than physical closure.
            _ = await new ReturnResolutionRecords(database, owner.Clock).ReadAsync(tx, inspection, ct);
        }
        var authenticated = history.Select(row => row["sourceKind"].AsString + ":" + row["sourceId"].AsInt64).ToHashSet();
        if (pending is not null && (!pending.PoolIds.Contains(poolId) || !authenticated.Add(pending.Kind + ":" + pending.Id))) throw Unavailable();
        if (!actual.SetEquals(authenticated)) throw Unavailable();
    }
    internal async Task<AfterSalesStockSourceSnapshot> ReadAsync(IClientSessionHandle tx, AfterSalesStockSourceEvents owner,
        StockSourceObligationExpectation expected, CancellationToken ct)
    {
        var (head, history, open) = await HistoryAsync(tx, owner, expected.PoolId, ct);
        var pool = await Rows("inventoryPools").Find(tx, F.Eq("_id", expected.PoolId)).SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
        if (head["location"] != StockSourceSetupBson.Location(expected.Location) || pool!["location"] != head["location"] || pool["revision"] != expected.PoolRevision || pool["fence"] != expected.PoolFence) throw Changed();
        if (pool["revision"].AsInt64 < history[^1]["poolRevision"].AsInt64 || pool["fence"].AsInt64 < history[^1]["afterPoolFence"].AsInt64) throw Unavailable();
        await RequireSourcesAsync(tx, owner, expected.PoolId, history, null, ct);
        return new(head, open.Count);
    }
}
