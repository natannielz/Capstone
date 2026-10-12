using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
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
            !ReferenceEquals(expectedDatabase.Client, database.Client) || !expectedDatabase.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
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
        ReferenceEquals(database.Client, other.database.Client) && database.DatabaseNamespace.Equals(other.database.DatabaseNamespace) &&
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
        } else if (!ReferenceEquals(boundDatabase.Client, database.Client) || !boundDatabase.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
            !ReferenceEquals(boundTransaction, tx) || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != boundNumber) throw Q.Broken();
    }
    internal void Record(string collection, BsonDocument filter, IEnumerable<BsonDocument> actual) {
        if (sealedScopes is not null) throw Q.Broken();
        if (!filters.TryGetValue(collection, out var selected)) filters.Add(collection, selected = []);
        if (!selected.Any(value => value.Equals(filter))) selected.Add(filter.DeepClone().AsBsonDocument);
        if (!rows.TryGetValue(collection, out var frozen)) rows.Add(collection, frozen = []);
        foreach (var row in actual) {
            if (collection == "authorizationScopes") RequireAuthorizationNode(row);
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
        // The real read above retains its full schema predicate. A successful
        // exact-ID read can replay the actual row plus byte equality without
        // replanning its static schema, including conjunctive owner validators.
        // Expressions, dates, business predicates and negative queries stay intact.
        var replayFilter = filter;
        if (row is not null && filter.TryGetValue("_id", out var id) &&
            !id.IsBsonDocument && !id.IsBsonArray && !id.IsBsonNull && row["_id"] == id) {
            if (filter.TryGetValue("$jsonSchema", out var schema) && schema.IsBsonDocument) {
                // The driver flattens composite validators into root fields.
                // Remove only schema; retain every other predicate byte-for-byte.
                replayFilter = filter.DeepClone().AsBsonDocument;
                replayFilter.Remove("$jsonSchema");
            } else if (filter.ElementCount == 2) {
                var predicate = new BsonDocument(filter.Elements.Single(element => element.Name != "_id"));
                var remaining = WithoutValidatedSchema(predicate);
                replayFilter = new BsonDocument("_id", id);
                if (remaining is not null) replayFilter.AddRange(remaining);
            }
        }
        Record(collection, replayFilter, row is null ? [] : [row]); return row;
    }
    private static BsonDocument? WithoutValidatedSchema(BsonDocument predicate) {
        if (predicate.ElementCount == 1 && predicate.TryGetValue("$jsonSchema", out var schema) && schema.IsBsonDocument) return null;
        if (predicate.ElementCount != 1 || !predicate.TryGetValue("$and", out var value) || !value.IsBsonArray ||
            value.AsBsonArray.Count == 0 || value.AsBsonArray.Any(item => !item.IsBsonDocument)) return predicate;
        var remaining = new BsonArray();
        foreach (var item in value.AsBsonArray) {
            var retained = WithoutValidatedSchema(item.AsBsonDocument);
            if (retained is not null) remaining.Add(retained);
        }
        // An all-schema conjunction is already proved by the actual frozen row.
        // Do not emit an empty $and, or descend into disjunctions/expressions.
        return remaining.Count == 0 ? null : new BsonDocument("$and", remaining);
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
    private static ScopeNode RequireAuthorizationNode(BsonDocument row) {
        string[] required = ["_id", "schemaVersion", "kind", "status", "version", "fence"];
        if (required.Any(name => !row.Contains(name)) || row.Names.Any(name => !required.Contains(name) && name != "parentId") ||
            !row["schemaVersion"].IsInt32 || row["schemaVersion"].AsInt32 != AuthorizationBson.SchemaVersion ||
            !row["fence"].IsInt64 || row["fence"].AsInt64 is < 0 or long.MaxValue) throw Q.Broken();
        ScopeNode node;
        try { node = AuthorizationBson.Node(row); }
        catch (AuthorizationDeniedException) { throw Q.Broken(); }
        if (row["kind"] != AuthorizationBson.Name(node.Kind) || row["status"] != AuthorizationBson.Name(node.Status) ||
            (OrganizationChangePolicy.ExpectedParent(node.Kind) is null ? node.ParentId is not null : node.ParentId is not > 0)) throw Q.Broken();
        return node;
    }
    private static bool MatchesScope(Scope scope, IReadOnlyList<BsonDocument> actual) {
        if (scope.Collection != "authorizationScopes") return new BsonArray(actual).Equals(scope.Rows);
        if (actual.Count != scope.Rows.Count) return false;
        for (var index = 0; index < actual.Count; index++) {
            var expected = scope.Rows[index].AsBsonDocument;
            var current = actual[index];
            if (RequireAuthorizationNode(current) != RequireAuthorizationNode(expected) || current["fence"].AsInt64 < expected["fence"].AsInt64) return false;
            // This exact typed node has already been authenticated by the owner.
            // A later owning fence can advance without changing its identity or
            // any captured business field, field membership, or query membership.
            var comparison = current.DeepClone().AsBsonDocument;
            comparison["fence"] = expected["fence"];
            if (!comparison.Equals(expected)) return false;
        }
        return true;
    }
    internal async Task RequireAsync(IMongoDatabase database, IClientSessionHandle tx, CancellationToken ct) {
        Context(database, tx);
        if (sealedScopes is null) throw Q.Broken();
        foreach (var scope in sealedScopes) {
            var actual = await database.GetCollection<BsonDocument>(scope.Collection).Find(tx, scope.Filter)
                .Sort(new BsonDocument("_id", 1)).Limit(MaximumRowsPerScope + 1).ToListAsync(ct);
            if (!MatchesScope(scope, actual)) {
                var failure = Q.Broken();
                var expected = scope.Rows.Select(value => value.AsBsonDocument).ToArray();
                var changed = expected.Where(row => !actual.Any(value => value["_id"] == row["_id"] && value == row))
                    .Concat(actual.Where(row => !expected.Any(value => value["_id"] == row["_id"] && value == row))).ToArray();
                var fields = changed.SelectMany(row => row.Names).Distinct(StringComparer.Ordinal).Where(name => {
                    foreach (var row in changed) {
                        var left = expected.FirstOrDefault(value => value["_id"] == row["_id"]);
                        var right = actual.FirstOrDefault(value => value["_id"] == row["_id"]);
                        if (left is null || right is null || left.Contains(name) != right.Contains(name) ||
                            left.GetValue(name, BsonNull.Value) != right.GetValue(name, BsonNull.Value)) return true;
                    }
                    return false;
                }).Order(StringComparer.Ordinal).Take(32);
                static string OpaqueId(BsonValue value) => value.BsonType switch {
                    BsonType.Int32 => value.AsInt32.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    BsonType.Int64 => value.AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    BsonType.ObjectId => value.AsObjectId.ToString(), _ => "other" };
                failure.Data["purchaseOrderCapture.collection"] = scope.Collection;
                failure.Data["purchaseOrderCapture.expectedCount"] = expected.Length;
                failure.Data["purchaseOrderCapture.actualCount"] = actual.Count;
                failure.Data["purchaseOrderCapture.changedIds"] = string.Join(",", changed.Select(row => OpaqueId(row["_id"])).Distinct(StringComparer.Ordinal).Take(8));
                failure.Data["purchaseOrderCapture.changedFields"] = string.Join(",", fields);
                throw failure;
            }
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
    // Provenance seals: export the sealed proof membership as row scopes. Pools travel as one-row scopes
    // and return to the fence-tolerant pool check when rebuilt. Recorded counts travel through SealedCounts().
    internal IReadOnlyList<(string Collection, BsonDocument Filter, long Value)> SealedCounts() {
        if (sealedScopes is null) throw Q.Broken();
        return [.. counts.Select(count => (count.Collection, count.Filter.DeepClone().AsBsonDocument, count.Value))];
    }
    internal IReadOnlyList<(string Collection, BsonDocument Filter, IReadOnlyList<BsonDocument> Rows)>? SealedScopes() {
        if (sealedScopes is null) throw Q.Broken();
        return [.. sealedScopes.Select(scope => (scope.Collection, scope.Filter.DeepClone().AsBsonDocument,
                (IReadOnlyList<BsonDocument>)scope.Rows.Select(row => row.AsBsonDocument.DeepClone().AsBsonDocument).ToList())),
            .. pools.OrderBy(pair => pair.Key).Select(pair => (PoolCollection, new BsonDocument("_id", pair.Key),
                (IReadOnlyList<BsonDocument>)new List<BsonDocument> { pair.Value.DeepClone().AsBsonDocument }))];
    }
    private const string PoolCollection = "inventoryPools";
    internal static PurchaseOrderSourceReadCapture FromSealed(IMongoDatabase database, IClientSessionHandle tx, IReadOnlyList<BsonDocument> verifiedScopes,
        StockSourceSetupService? storageOwner) {
        var capture = new PurchaseOrderSourceReadCapture(); capture.Context(database, tx);
        if (storageOwner is not null) capture.sourceOwners.Add(storageOwner);
        var scopes = new List<Scope>();
        foreach (var scope in verifiedScopes) {
            var filter = scope["filter"].AsBsonDocument;
            if (scope.Contains("count")) { capture.counts.Add(new(scope["collection"].AsString, filter.DeepClone().AsBsonDocument, scope["count"].AsInt64)); continue; }
            var rows = scope["rows"].AsBsonArray;
            if (scope["collection"] == PoolCollection && filter.ElementCount == 1 && filter.Contains("_id") && filter["_id"].IsInt64 && rows.Count == 1) {
                InventoryGraph.RequirePool(rows[0].AsBsonDocument); capture.pools.Add(filter["_id"].AsInt64, rows[0].AsBsonDocument.DeepClone().AsBsonDocument);
            } else scopes.Add(new(scope["collection"].AsString, filter.DeepClone().AsBsonDocument, new BsonArray(rows.Select(row => row.DeepClone()))));
        }
        capture.sealedScopes = scopes; return capture;
    }
    internal bool Same(PurchaseOrderSourceReadCapture other) => sealedScopes is not null && other.sealedScopes is not null &&
        sealedScopes.Count == other.sealedScopes.Count && sealedScopes.Zip(other.sealedScopes).All(pair =>
            pair.First.Collection == pair.Second.Collection && pair.First.Filter.Equals(pair.Second.Filter) && pair.First.Rows.Equals(pair.Second.Rows)) &&
        counts.Count == other.counts.Count && counts.Zip(other.counts).All(pair => pair.First.Collection == pair.Second.Collection &&
            pair.First.Filter.Equals(pair.Second.Filter) && pair.First.Value == pair.Second.Value) &&
        pools.Count == other.pools.Count && pools.All(pair => other.pools.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
