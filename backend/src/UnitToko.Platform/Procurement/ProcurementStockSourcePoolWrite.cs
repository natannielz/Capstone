using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Procurement;

/// <summary>Consumes the held writer's genuine existing canonical CAS, not a numeric fence
/// supplied by a caller. No extra pool write is introduced. Valid for one current transaction only.</summary>
internal sealed class ProcurementStockSourcePoolWrite
{
    private readonly IMongoDatabase database;
    private readonly IClientSessionHandle session;
    private readonly long transaction;
    private readonly BsonDocument before, after;
    private readonly long receiptId;
    private readonly string receiptHash;
    private bool consumed;
    private ProcurementStockSourcePoolWrite(IMongoDatabase database, IClientSessionHandle tx,
        BsonDocument before, BsonDocument after, BsonDocument receipt)
    {
        this.database = database; session = tx;
        transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        this.before = before.DeepClone().AsBsonDocument;
        this.after = after.DeepClone().AsBsonDocument;
        receiptId = receipt["_id"].AsInt64;
        receiptHash = receipt["snapshotHash"].AsString;
    }
    internal long PoolId => before["_id"].AsInt64;
    internal long ReceiptId => receiptId;
    internal string ReceiptHash => receiptHash;
    internal long Revision => before["revision"].AsInt64;
    internal long BeforeFence => before["fence"].AsInt64;
    internal long AfterFence => after["fence"].AsInt64;
    internal BsonDocument Location => before["location"].DeepClone().AsBsonDocument;

    internal static async Task<ProcurementStockSourcePoolWrite> FenceAsync(IMongoDatabase database,
        IClientSessionHandle tx, ActorContext actor, ProcurementStockReceiptWriteProof receiptProof, CancellationToken ct)
    {
        var repository = new InventoryRepository(database); repository.RequireSession(tx);
        var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        receiptProof.Require(actor, tx);
        var receipt = receiptProof.Row.DeepClone().AsBsonDocument;
        var storedReceipt = await database.GetCollection<BsonDocument>(ProcurementStockReceiptBson.Receipts)
            .Find(tx, new BsonDocument("_id", receipt["_id"])).SingleOrDefaultAsync(ct);
        if (storedReceipt is null || storedReceipt != receipt || receipt["snapshotHash"] != ProcurementStockReceiptBson.Hash(receipt))
            throw ProcurementStockSourceRecords.Unavailable();
        var capture = receiptProof.Pool;
        capture.RequireCurrent();
        var before = capture.Pool.DeepClone().AsBsonDocument; InventoryGraph.RequirePool(before);
        var pools = database.GetCollection<BsonDocument>("inventoryPools");
        var actual = await pools.Find(tx, new BsonDocument("_id", before["_id"])).SingleOrDefaultAsync(ct);
        if (actual is null || actual != before || before["fence"].AsInt64 >= long.MaxValue - 2)
            throw ProcurementStockSourceRecords.Changed();
        // This delegates the complete source/policy/authority checks and the existing ONE pool CAS.
        await capture.FenceAsync(ct);
        var after = capture.Pool.DeepClone().AsBsonDocument;
        var expected = before.DeepClone().AsBsonDocument; expected["fence"] = checked(before["fence"].AsInt64 + 1);
        actual = await pools.Find(tx, new BsonDocument("_id", before["_id"])).SingleOrDefaultAsync(ct);
        repository.RequireSession(tx); capture.RequireCurrent();
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || after != expected || actual != after)
            throw ProcurementStockSourceRecords.Changed();
        ct.ThrowIfCancellationRequested();
        receiptProof.Require(actor, tx);
        return new(database, tx, before, after, receipt);
    }
    internal async Task RequireAsync(IMongoDatabase current, IClientSessionHandle tx, CancellationToken ct)
    {
        new InventoryRepository(current).RequireSession(tx);
        if (consumed || !ReferenceEquals(tx, session) || !ReferenceEquals(current.Client, database.Client) ||
            current.DatabaseNamespace != database.DatabaseNamespace ||
            tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction)
            throw ProcurementStockSourceRecords.Unavailable();
        var actual = await current.GetCollection<BsonDocument>("inventoryPools")
            .Find(tx, new BsonDocument("_id", PoolId)).SingleOrDefaultAsync(ct);
        if (actual is null || actual != after) throw ProcurementStockSourceRecords.Changed();
        new InventoryRepository(current).RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction)
            throw ProcurementStockSourceRecords.Unavailable();
        ct.ThrowIfCancellationRequested();
    }
    internal void Consume() => consumed = true;
}
