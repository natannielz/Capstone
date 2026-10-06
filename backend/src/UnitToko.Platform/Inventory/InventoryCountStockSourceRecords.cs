using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Inventory;

internal sealed record InventoryCountStockSourceEvent(string Kind, long Id, long PoolId, BsonDocument Record, BsonDocument Audit, BsonValue Time)
{
    internal string Hash => CatalogRepository.Digest(new BsonDocument { { "kind", Kind }, { "record", Record } });
}
internal sealed record InventoryCountStockSourceSnapshot(BsonDocument Head, long OpenCount);

/// <summary>Original explicit-source count associations and canonical pool history.
/// InventoryCountCoverage additionally owns exhaustive bound/unbound observation membership.</summary>
internal sealed class InventoryCountStockSourceRecords(IMongoDatabase database)
{
    internal const string Heads = "inventoryCountSourceHeads", Transitions = "inventoryCountSourceTransitions", Associations = "inventoryCountSourceAssociations";
    internal const int MaximumRows = 4096;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static DomainValidationException Unavailable() => new("inventory_count_source_obligation_unavailable", "The original count and explicit source association must be authenticated");
    internal static PersistenceConflictException Changed() => new("stock_source_revision_changed", "The selected count source changed; refresh before recording");
    internal static string Hash(BsonDocument row, string field) => CatalogRepository.Digest(new BsonDocument(row.Where(element => element.Name != field)));
    internal static string SourceHash(BsonDocument binding, BsonDocument configuration, BsonArray people) => CatalogRepository.Digest(new BsonDocument {
        { "poolBinding", binding }, { "configuration", configuration }, { "people", people } });
    internal static BsonDocument Association(BsonDocument observation, InventoryCountPoolCapture source) {
        var binding = StockCountBson.PoolBinding(source.Binding); var configuration = source.Configuration; var people = source.People;
        if (observation["poolBinding"] != binding || observation["poolSourceHash"] != SourceHash(binding, configuration, people)) throw Unavailable();
        var row = new BsonDocument { { "_id", observation["_id"] }, { "schemaVersion", 1 }, { "kind", "inventory.countsource.association" },
            { "observationId", observation["_id"] }, { "poolId", source.Binding.PoolId }, { "location", observation["location"].DeepClone() },
            { "poolBinding", binding }, { "configuration", configuration }, { "observationFingerprint", observation["fingerprint"] },
            { "observationBodyHash", observation["bodyHash"] }, { "observationAuditId", observation["auditId"] }, { "actorId", observation["actorId"] },
            { "actorGrantVersion", observation["actorGrantVersion"] }, { "recordedAt", observation["recordedAt"] }, { "requestKey", observation["requestKey"] }, { "people", people } };
        row.Add("associationHash", Hash(row, "associationHash")); return row;
    }
    private async Task<BsonDocument[]> BoundedAsync(string name, BsonDocument filter, IClientSessionHandle tx, CancellationToken ct,
        string order = "_id") {
        var rows = await Rows(name).Find(tx, filter).Sort(new BsonDocument(order, 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (rows.Count > MaximumRows) throw Unavailable(); return rows.ToArray();
    }
    internal async Task<InventoryCountStockSourceEvent> EventAsync(IClientSessionHandle tx, StockCountService counts,
        StockSourceSetupService sources, string kind, long id, bool completedSetup, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx);
        if (kind == "setup") {
            var source = await new OrderStockSourceRecords(database).SetupAsync(tx, id, completedSetup, ct);
            if (source.PoolIds.Length != 1) throw Unavailable();
            return new(kind, id, source.PoolIds[0], source.Record, source.Audit, source.Time);
        }
        if (kind != "count" || id <= 0) throw Unavailable();
        var row = await Rows(StockCountBson.Collection).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        var count = await counts.GraphObservationAsync(tx, row, ct); var binding = count.PoolBinding ?? throw Unavailable();
        var association = await Rows(Associations).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        if (!await Rows(Associations).Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray {
            InventoryCountStockSourceSchemas.Validator(Associations) } } }).AnyAsync(ct)) throw Unavailable();
        if (association["observationId"] != id || association["poolId"] != binding.PoolId || association["location"] != row["location"] ||
            association["poolBinding"] != row["poolBinding"] || association["observationFingerprint"] != row["fingerprint"] ||
            association["observationBodyHash"] != row["bodyHash"] || association["observationAuditId"] != row["auditId"] ||
            association["actorId"] != row["actorId"] || association["actorGrantVersion"] != row["actorGrantVersion"] || association["recordedAt"] != row["recordedAt"] || association["requestKey"] != row["requestKey"] ||
            association["associationHash"] != Hash(association, "associationHash") ||
            row["poolSourceHash"] != SourceHash(association["poolBinding"].AsBsonDocument, association["configuration"].AsBsonDocument, association["people"].AsBsonArray)) throw Unavailable();
        var person = association["people"].AsBsonArray;
        if (person.Count != 1 || person[0]["userId"] != row["actorId"] || person[0]["verification"] != "Verified" ||
            !person[0]["personId"].IsInt64 || person[0]["personId"].AsInt64 <= 0 || !person[0]["version"].IsInt64 || person[0]["version"].AsInt64 <= 0) throw Unavailable();
        await sources.VerifyInventoryCountConfigurationAsync(tx, binding, association["configuration"].AsBsonDocument, count.Location, count.RecordedAt, ct);
        // Count uses its genuine custom audit and completed full-row receipt.
        // No outbox is emitted by the count owner, and none is fabricated here.
        var audits = await BoundedAsync("auditEvents", new BsonDocument { { "kind", StockCountBson.AuditKind }, { "observationId", id } }, tx, ct);
        var receipts = await BoundedAsync("idempotencyRecords", new BsonDocument { { "operation", StockCountBson.Operation }, { "$or", new BsonArray {
            new BsonDocument("stockCount.observationId", id), new BsonDocument("result._id", id) } } }, tx, ct);
        if (audits.Length != 1 || receipts.Length != 1) throw Unavailable();
        StockCountBson.Graph(row, audits[0], receipts[0]);
        return new(kind, id, binding.PoolId, new BsonDocument { { "observation", row }, { "association", association } }, audits[0], row["recordedAt"]);
    }
    internal async Task<(BsonDocument Head, BsonDocument[] History, HashSet<long> Counts)> HistoryAsync(IClientSessionHandle tx,
        StockCountService counts, StockSourceSetupService sources, long poolId, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx); var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var head = await Rows(Heads).Find(tx, new BsonDocument { { "_id", poolId }, { "$and", new BsonArray {
            InventoryCountStockSourceSchemas.Validator(Heads) } } }).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        var history = await BoundedAsync(Transitions, new BsonDocument("poolId", poolId), tx, ct, "revision");
        if (history.Length is < 1 or > MaximumRows || head["revision"].AsInt64 != history.Length) throw Unavailable();
        var membership = new HashSet<long>(); BsonDocument? previous = null; BsonValue? location = null;
        foreach (var transition in history) {
            if (!await Rows(Transitions).Find(tx, new BsonDocument { { "_id", transition["_id"] }, { "$and", new BsonArray {
                InventoryCountStockSourceSchemas.Validator(Transitions) } } }).AnyAsync(ct) || transition["transitionHash"] != Hash(transition, "transitionHash") ||
                transition["revision"].AsInt64 != (previous is null ? 1L : previous["revision"].AsInt64 + 1) ||
                transition["previousId"] != (previous is null ? BsonNull.Value : previous["_id"]) ||
                transition["previousHash"] != (previous is null ? BsonNull.Value : previous["transitionHash"]) ||
                previous is not null && transition["recordedAt"].CompareTo(previous["recordedAt"]) < 0) throw Unavailable();
            var kind = transition["sourceKind"].AsString; var id = transition["sourceId"].AsInt64;
            if ((previous is null) != (kind == "setup")) throw Unavailable();
            var source = await EventAsync(tx, counts, sources, kind, id, true, ct);
            if (source.PoolId != poolId || transition["sourceHash"] != source.Hash || transition["sourceAuditId"] != source.Audit["_id"] ||
                transition["actorId"] != source.Audit["actorId"] || transition["actorGrantVersion"] != source.Audit["actorGrantVersion"] || transition["recordedAt"] != source.Time) throw Unavailable();
            if (kind == "setup") {
                if (id != head["setupDecisionId"].AsInt64 || transition["poolRevision"] != 1L || transition["beforePoolFence"] != 0L ||
                    transition["afterPoolFence"] != 0L || !transition["observationId"].IsBsonNull) throw Unavailable();
                location = source.Record["proposal"]["input"]["location"];
            } else {
                if (!membership.Add(id) || transition["observationId"] != id || transition["afterPoolFence"].AsInt64 != transition["beforePoolFence"].AsInt64 + 1 ||
                    transition["poolRevision"].AsInt64 < previous!["poolRevision"].AsInt64 || transition["beforePoolFence"].AsInt64 < previous["afterPoolFence"].AsInt64 ||
                    transition["poolRevision"] != source.Record["association"]["poolBinding"]["poolRevision"]) throw Unavailable();
            }
            previous = transition;
        }
        if (previous is null || location is null || head["location"] != location || head["lastTransitionId"] != previous["_id"] ||
            head["lastHash"] != previous["transitionHash"] || head["updatedAt"] != previous["recordedAt"]) throw Unavailable();
        new InventoryRepository(database).RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw Unavailable();
        return (head, history, membership);
    }
    internal async Task RequireSourcesAsync(IClientSessionHandle tx, StockCountService counts, StockSourceSetupService sources,
        long poolId, BsonDocument[] history, InventoryCountStockSourceEvent? pending, CancellationToken ct) {
        var ids = history.Where(row => row["sourceKind"] == "count").Select(row => row["sourceId"].AsInt64).ToHashSet();
        if (pending is not null && (pending.Kind != "count" || pending.PoolId != poolId || !ids.Add(pending.Id))) throw Unavailable();
        var values = new BsonArray(ids.Order().Select(id => new BsonInt64(id)));
        var associations = await BoundedAsync(Associations, new BsonDocument("$or", new BsonArray {
            new BsonDocument("poolId", poolId), new BsonDocument("observationId", new BsonDocument("$in", values)), new BsonDocument("_id", new BsonDocument("$in", values)) }), tx, ct);
        var observations = await BoundedAsync(StockCountBson.Collection, new BsonDocument("poolBinding.poolId", poolId), tx, ct);
        if (associations.Length != ids.Count || observations.Length != ids.Count || !ids.SetEquals(associations.Select(row => row["_id"].AsInt64)) ||
            !ids.SetEquals(observations.Select(row => row["_id"].AsInt64))) throw Unavailable();
        foreach (var id in ids) _ = await EventAsync(tx, counts, sources, "count", id, true, ct);
        var transitions = await BoundedAsync(Transitions, new BsonDocument("$or", new BsonArray { new BsonDocument("poolId", poolId),
            new BsonDocument { { "sourceKind", "count" }, { "sourceId", new BsonDocument("$in", values) } },
            new BsonDocument { { "sourceKind", "setup" }, { "sourceId", history[0]["sourceId"] } } }), tx, ct);
        if (transitions.Length != history.Length || history.Any(row => !transitions.Any(actual => actual == row))) throw Unavailable();
    }
    internal async Task<InventoryCountStockSourceSnapshot> ReadAsync(IClientSessionHandle tx, StockCountService counts,
        StockSourceSetupService sources, StockSourceObligationExpectation expected, CancellationToken ct) {
        await InventoryCountStockSourceStorage.RequireAsync(database, ct);
        var (head, history, membership) = await HistoryAsync(tx, counts, sources, expected.PoolId, ct);
        var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", expected.PoolId)).SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
        if (pool!["location"] != StockSourceSetupBson.Location(expected.Location) || head["location"] != pool["location"] ||
            pool["revision"] != expected.PoolRevision || pool["fence"] != expected.PoolFence) throw Changed();
        if (pool["revision"].AsInt64 < history[^1]["poolRevision"].AsInt64 || pool["fence"].AsInt64 < history[^1]["afterPoolFence"].AsInt64) throw Unavailable();
        await RequireSourcesAsync(tx, counts, sources, expected.PoolId, history, null, ct);
        return new(head, membership.Count);
    }
    internal async Task RequireObservationAsync(IClientSessionHandle tx, StockCountService counts, StockSourceSetupService sources,
        BsonDocument observation, CancellationToken ct) {
        await InventoryCountStockSourceStorage.RequireAsync(database, ct);
        var source = await EventAsync(tx, counts, sources, "count", observation["_id"].AsInt64, true, ct);
        if (source.Record["observation"] != observation) throw Unavailable();
        var (_, history, ids) = await HistoryAsync(tx, counts, sources, source.PoolId, ct);
        if (!ids.Contains(source.Id)) throw Unavailable(); await RequireSourcesAsync(tx, counts, sources, source.PoolId, history, null, ct);
    }
}
