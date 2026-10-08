using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Fulfillment;

/// <summary>Created only by the actual single canonical-pool CAS. Never caller-supplied evidence.</summary>
internal sealed class FulfillmentStockSourcePoolWrite
{
    private readonly IMongoDatabase database;
    private readonly IClientSessionHandle transaction;
    private readonly long transactionNumber;
    private readonly BsonDocument before, after;
    private bool consumed;
    private FulfillmentStockSourcePoolWrite(IMongoDatabase database, IClientSessionHandle tx, BsonDocument before, BsonDocument after)
    {
        this.database = database; transaction = tx;
        transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        this.before = before.DeepClone().AsBsonDocument; this.after = after.DeepClone().AsBsonDocument;
    }
    internal long PoolId => before["_id"].AsInt64;
    internal long PoolRevision => before["revision"].AsInt64;
    internal long BeforeFence => before["fence"].AsInt64;
    internal long AfterFence => after["fence"].AsInt64;
    internal BsonDocument Location => before["location"].DeepClone().AsBsonDocument;
    internal static async Task<FulfillmentStockSourcePoolWrite> FenceAsync(IMongoDatabase database,
        IClientSessionHandle tx, BsonDocument actualBeforePool, CancellationToken ct)
    {
        var repository = new InventoryRepository(database); repository.RequireSession(tx);
        var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        await InventoryRepository.RequireStorageAsync(database, "inventoryPools", StockSourceSetupSchemas.Validator("inventoryPools"),
            StockSourceSetupSchemas.Key("inventoryPools"), ct);
        var before = actualBeforePool.DeepClone().AsBsonDocument; InventoryGraph.RequirePool(before);
        if (before["fence"].AsInt64 == long.MaxValue) throw StockSourceRevisionBson.Invalid();
        var after = before.DeepClone().AsBsonDocument; after["fence"] = checked(before["fence"].AsInt64 + 1);
        if ((await database.GetCollection<BsonDocument>("inventoryPools").UpdateOneAsync(tx, before,
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
            throw new PersistenceConflictException("stock_source_revision_changed", "The reviewed source settings changed; refresh before proposing or reviewing again");
        repository.RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw FulfillmentStockSourceRecords.Unavailable();
        return new(database, tx, before, after);
    }
    internal async Task RequireAsync(IMongoDatabase current, IClientSessionHandle tx, CancellationToken ct)
    {
        new InventoryRepository(current).RequireSession(tx);
        if (consumed || !ReferenceEquals(tx, transaction) || !ReferenceEquals(current.Client, database.Client) ||
            !current.DatabaseNamespace.Equals(database.DatabaseNamespace) || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber)
            throw FulfillmentStockSourceRecords.Unavailable();
        var pool = await current.GetCollection<BsonDocument>("inventoryPools").Find(tx, new BsonDocument("_id", PoolId)).SingleOrDefaultAsync(ct);
        if (pool is null || !pool.Equals(after)) throw FulfillmentStockSourceRecords.Changed();
        new InventoryRepository(current).RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber) throw FulfillmentStockSourceRecords.Unavailable();
    }
    internal void Consume() => consumed = true;
}
