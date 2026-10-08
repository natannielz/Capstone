using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

/// <summary>Single actual canonical CAS, bound to this observed row/capture/session.
/// Descriptive hashes or a copied numeric fence cannot construct a write handle.</summary>
internal sealed class InventoryCountCanonicalWrite
{
    private readonly IMongoDatabase database;
    private readonly IClientSessionHandle tx;
    private readonly long number, observationId;
    private readonly string observationFingerprint;
    private readonly BsonDocument before, after;
    private readonly InventoryCountPoolCapture capture;
    private bool consumed;
    private InventoryCountCanonicalWrite(IMongoDatabase database, IClientSessionHandle tx, BsonDocument before,
        BsonDocument after, BsonDocument observation, InventoryCountPoolCapture capture) {
        this.database = database; this.tx = tx; number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        this.before = before.DeepClone().AsBsonDocument; this.after = after.DeepClone().AsBsonDocument;
        observationId = observation["_id"].AsInt64; observationFingerprint = observation["fingerprint"].AsString; this.capture = capture;
    }
    internal long PoolId => before["_id"].AsInt64;
    internal long Revision => before["revision"].AsInt64;
    internal long BeforeFence => before["fence"].AsInt64;
    internal long AfterFence => after["fence"].AsInt64;
    internal static async Task<InventoryCountCanonicalWrite> FenceAsync(IMongoDatabase database, IClientSessionHandle tx,
        InventoryCountPoolCapture capture, BsonDocument observation, CancellationToken ct) {
        capture.Require(database, tx); var before = capture.Pool; InventoryGraph.RequirePool(before);
        if (before["fence"].AsInt64 >= long.MaxValue - 2 || StockCountBson.Read(observation).PoolBinding != capture.Binding)
            throw InventoryCountStockSourceRecords.Unavailable();
        var after = before.DeepClone().AsBsonDocument; after["fence"] = checked(before["fence"].AsInt64 + 1);
        if ((await database.GetCollection<BsonDocument>("inventoryPools").UpdateOneAsync(tx, before,
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
            throw InventoryCountStockSourceRecords.Changed();
        var proof = new InventoryCountCanonicalWrite(database, tx, before, after, observation, capture);
        await proof.RequireAsync(database, tx, observation, ct); return proof;
    }
    private void Context(IMongoDatabase current, IClientSessionHandle session, BsonDocument observation) {
        capture.Require(current, session);
        if (!ReferenceEquals(session, tx) || !ReferenceEquals(current.Client, database.Client) || !current.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
            tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || observation["_id"] != observationId || observation["fingerprint"] != observationFingerprint)
            throw InventoryCountStockSourceRecords.Unavailable();
    }
    internal async Task RequireAsync(IMongoDatabase current, IClientSessionHandle session, BsonDocument observation, CancellationToken ct) {
        Context(current, session, observation); if (consumed) throw InventoryCountStockSourceRecords.Unavailable();
        var actual = await current.GetCollection<BsonDocument>("inventoryPools").Find(session, new BsonDocument("_id", PoolId)).SingleOrDefaultAsync(ct);
        if (actual is null || actual != after) throw InventoryCountStockSourceRecords.Changed(); Context(current, session, observation);
    }
    internal void Consume() { if (consumed) throw InventoryCountStockSourceRecords.Unavailable(); consumed = true; }
    internal async Task RequireFinalAsync(IMongoDatabase current, IClientSessionHandle session, BsonDocument observation, CancellationToken ct) {
        Context(current, session, observation); if (!consumed) throw InventoryCountStockSourceRecords.Unavailable();
        var actual = await current.GetCollection<BsonDocument>("inventoryPools").Find(session, new BsonDocument("_id", PoolId)).SingleOrDefaultAsync(ct);
        if (actual is null || actual != after) throw InventoryCountStockSourceRecords.Changed(); Context(current, session, observation);
    }
}
