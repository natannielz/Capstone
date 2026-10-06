using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using Q = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;

namespace UnitToko.Platform.Procurement;

// Only the genuine source factory can publish a proof. It records actual owner
// reads as they occur and seals them after every semantic/history check succeeds.
internal sealed class PurchaseOrderHistoricalSourceProof
{
    private readonly PurchaseOrderService owner;
    private readonly IMongoDatabase database;
    private readonly IClientSessionHandle transaction;
    private readonly long transactionNumber;
    private readonly BsonDocument source;
    private readonly PurchaseOrderSourceReadCapture capture;
    private PurchaseOrderHistoricalSourceProof(PurchaseOrderService owner, IMongoDatabase database,
        IClientSessionHandle transaction, BsonDocument source, PurchaseOrderSourceReadCapture capture) {
        this.owner = owner; this.database = database; this.transaction = transaction;
        transactionNumber = transaction.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        this.source = source.DeepClone().AsBsonDocument; this.capture = capture;
    }
    internal BsonDocument Source => source.DeepClone().AsBsonDocument;
    private void Require(PurchaseOrderService expectedOwner, IMongoDatabase expectedDatabase, IClientSessionHandle current,
        BsonDocument expectedSource) {
        if (!ReferenceEquals(expectedOwner, owner) || !ReferenceEquals(current, transaction) || !current.IsInTransaction ||
            current.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber ||
            !ReferenceEquals(expectedDatabase.Client, database.Client) || expectedDatabase.DatabaseNamespace != database.DatabaseNamespace ||
            !ReferenceEquals(current.Client, database.Client) || !expectedSource.Equals(source)) throw Q.Broken();
    }
    internal async Task RequireAsync(PurchaseOrderService expectedOwner, IMongoDatabase expectedDatabase,
        IClientSessionHandle current, BsonDocument expectedSource, CancellationToken ct) {
        Require(expectedOwner, expectedDatabase, current, expectedSource);
        await capture.RequireAsync(database, current, ct);
        // The genuine initial owner read also captures inbound source history. Replay
        // checks its complete membership and actual mutable pools, never current authority.
        Require(expectedOwner, expectedDatabase, current, expectedSource); ct.ThrowIfCancellationRequested();
    }
    internal bool SameProof(PurchaseOrderHistoricalSourceProof other) => ReferenceEquals(owner, other.owner) &&
        ReferenceEquals(transaction, other.transaction) && transactionNumber == other.transactionNumber &&
        ReferenceEquals(database.Client, other.database.Client) && database.DatabaseNamespace == other.database.DatabaseNamespace &&
        source.Equals(other.source) && capture.Same(other.capture);
    internal static async Task<PurchaseOrderHistoricalSourceProof> ReadAsync(PurchaseOrderService owner,
        IMongoDatabase database, IClientSessionHandle tx, long orderId, long orderVersion, long approvalId, CancellationToken ct) {
        // IDs only: no caller-supplied source/proof or post-reconstruction snapshot.
        var capture = new PurchaseOrderSourceReadCapture();
        var source = await owner.FundingSourceCoreAsync(database, tx, orderId, orderVersion, approvalId, ct, capture);
        capture.Seal();
        var proof = new PurchaseOrderHistoricalSourceProof(owner, database, tx, source, capture);
        proof.Require(owner, database, tx, source); ct.ThrowIfCancellationRequested(); return proof;
    }
}

internal sealed class PurchaseOrderSourceReadCapture
{
    private const int MaximumRowsPerScope = 4096, MaximumRows = 16384;
    private sealed record Scope(string Collection, BsonDocument Filter, BsonArray Rows);
    private sealed record Count(string Collection, BsonDocument Filter, long Value);
    private readonly Dictionary<string, List<BsonDocument>> filters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<BsonValue, BsonDocument>> rows = new(StringComparer.Ordinal);
    private readonly List<Count> counts = [];
    private readonly Dictionary<long, BsonDocument> pools = [];
    private readonly List<StockSourceSetupService> sourceOwners = [];
    private IReadOnlyList<Scope>? sealedScopes;
    private IMongoDatabase? boundDatabase;
    private IClientSessionHandle? boundTransaction;
    private long boundNumber;
    private void Context(IMongoDatabase database, IClientSessionHandle tx) {
        new InventoryRepository(database).RequireSession(tx);
        if (boundDatabase is null) {
            boundDatabase = database; boundTransaction = tx; boundNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        } else if (!ReferenceEquals(boundDatabase.Client, database.Client) || boundDatabase.DatabaseNamespace != database.DatabaseNamespace ||
            !ReferenceEquals(boundTransaction, tx) || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != boundNumber) throw Q.Broken();
    }
    internal void Record(string collection, BsonDocument filter, IEnumerable<BsonDocument> actual) {
        if (sealedScopes is not null) throw Q.Broken();
        if (!filters.TryGetValue(collection, out var selected)) filters.Add(collection, selected = []);
        if (!selected.Any(value => value.Equals(filter))) selected.Add(filter.DeepClone().AsBsonDocument);
        if (!rows.TryGetValue(collection, out var frozen)) rows.Add(collection, frozen = []);
        foreach (var row in actual) {
            var id = row["_id"];
            if (frozen.TryGetValue(id, out var previous)) { if (!previous.Equals(row)) throw Q.Broken(); }
            else frozen.Add(id.DeepClone(), row.DeepClone().AsBsonDocument);
            if (frozen.Count > MaximumRowsPerScope || rows.Values.Sum(value => value.Count) > MaximumRows) throw Q.Broken();
        }
    }
    internal void RecordCount(string collection, BsonDocument filter, long value) {
        if (sealedScopes is not null || value < 0 || value > MaximumRowsPerScope) throw Q.Broken();
        var existing = counts.SingleOrDefault(count => count.Collection == collection && count.Filter.Equals(filter));
        if (existing is not null) { if (existing.Value != value) throw Q.Broken(); }
        else counts.Add(new(collection, filter.DeepClone().AsBsonDocument, value));
    }
    internal async Task<BsonDocument?> OneAsync(IMongoDatabase database, IClientSessionHandle tx, string collection,
        BsonDocument filter, CancellationToken ct) {
        Context(database, tx);
        var row = await database.GetCollection<BsonDocument>(collection).Find(tx, filter).SingleOrDefaultAsync(ct);
        // The real read above retains its full schema predicate. On replay an
        // ID lookup plus byte equality is stricter than filtering a corrupt row
        // out, and avoids copying the same static schema for every ancestor ID.
        var replayFilter = filter.ElementCount == 2 && filter.Contains("_id") && filter.Contains("$and")
            ? new BsonDocument("_id", filter["_id"]) : filter;
        Record(collection, replayFilter, row is null ? [] : [row]); return row;
    }
    internal async Task<bool> AnyAsync(IMongoDatabase database, IClientSessionHandle tx, string collection,
        BsonDocument filter, CancellationToken ct) {
        Context(database, tx);
        var exists = await database.GetCollection<BsonDocument>(collection).Find(tx, filter).AnyAsync(ct);
        if (!exists) Record(collection, filter, []);
        return exists;
    }
    internal void RecordSourceOwner(StockSourceSetupService owner) {
        if (sealedScopes is not null) throw Q.Broken();
        if (sourceOwners.Count == 0) sourceOwners.Add(owner);
        else if (!sourceOwners[0].SameCapturedHistoryOwner(owner)) throw Q.Broken();
    }
    internal void RecordPool(BsonDocument pool) {
        if (sealedScopes is not null) throw Q.Broken(); InventoryGraph.RequirePool(pool);
        var id = pool["_id"].AsInt64;
        if (pools.TryGetValue(id, out var previous)) { if (previous != pool) throw Q.Broken(); }
        else pools.Add(id, pool.DeepClone().AsBsonDocument);
    }
    internal void Seal() {
        if (sealedScopes is not null || rows.Count == 0 || boundDatabase is null || boundTransaction is null) throw Q.Broken();
        sealedScopes = filters.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new Scope(pair.Key,
            pair.Value.Count == 1 ? pair.Value[0].DeepClone().AsBsonDocument : new BsonDocument("$or", new BsonArray(pair.Value.Select(value => value.DeepClone()))),
            new BsonArray(rows[pair.Key].Values.OrderBy(row => row["_id"]).Select(row => row.DeepClone())))).ToArray();
    }
    internal async Task RequireAsync(IMongoDatabase database, IClientSessionHandle tx, CancellationToken ct) {
        Context(database, tx);
        if (sealedScopes is null) throw Q.Broken();
        foreach (var scope in sealedScopes) {
            var actual = await database.GetCollection<BsonDocument>(scope.Collection).Find(tx, scope.Filter)
                .Sort(new BsonDocument("_id", 1)).Limit(MaximumRowsPerScope + 1).ToListAsync(ct);
            if (!new BsonArray(actual).Equals(scope.Rows)) throw Q.Broken();
        }
        foreach (var count in counts)
            if (await database.GetCollection<BsonDocument>(count.Collection).CountDocumentsAsync(tx, count.Filter, cancellationToken: ct) != count.Value) throw Q.Broken();
        foreach (var owner in sourceOwners) await owner.RequireCapturedHistoryStorageAsync(database, tx, ct);
        foreach (var (id, expected) in pools) {
            var actual = await database.GetCollection<BsonDocument>("inventoryPools").Find(tx, new BsonDocument("_id", id))
                .SingleOrDefaultAsync(ct) ?? throw Q.Broken();
            InventoryGraph.RequirePool(actual);
            if (actual.ElementCount != expected.ElementCount || actual["fence"].AsInt64 < expected["fence"].AsInt64) throw Q.Broken();
            // A genuine later owning fence may advance. Every other actual schema
            // field must still match the configuration authenticated by the owner.
            foreach (var field in expected) if (field.Name != "fence" && actual[field.Name] != field.Value) throw Q.Broken();
        }
    }
    internal bool Same(PurchaseOrderSourceReadCapture other) => sealedScopes is not null && other.sealedScopes is not null &&
        sealedScopes.Count == other.sealedScopes.Count && sealedScopes.Zip(other.sealedScopes).All(pair =>
            pair.First.Collection == pair.Second.Collection && pair.First.Filter.Equals(pair.Second.Filter) && pair.First.Rows.Equals(pair.Second.Rows)) &&
        counts.Count == other.counts.Count && counts.Zip(other.counts).All(pair => pair.First.Collection == pair.Second.Collection &&
            pair.First.Filter.Equals(pair.Second.Filter) && pair.First.Value == pair.Second.Value) &&
        pools.Count == other.pools.Count && pools.All(pair => other.pools.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
