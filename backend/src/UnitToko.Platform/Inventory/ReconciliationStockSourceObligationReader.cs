using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

/// <summary>The real reconciliation owner only; remaining obligation domains are still required.
/// No source activation, release, physical adjustment or financial acceptance is granted.</summary>
internal sealed class ReconciliationStockSourceObligationReader(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
    : IStockSourceObligationReader
{
    public StockSourceObligationDomain Domain => StockSourceObligationDomain.SourceReconciliation;
    public IMongoDatabase Database => database;
    public Task<StockSourceObligationResult> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor,
        StockSourceObligationExpectation pool, CancellationToken ct) =>
        new StockSourceSetupService(database, unit, ids, clock).ReadReconciliationStockSourceObligationAsync(tx, actor, pool, ct);
}

public sealed partial class StockSourceSetupService
{
    internal async Task<StockSourceObligationResult> ReadReconciliationStockSourceObligationAsync(IClientSessionHandle tx,
        ActorContext actor, StockSourceObligationExpectation expected, CancellationToken ct)
    {
        try {
            var repository = new InventoryRepository(database); repository.RequireSession(tx);
            var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
            if (actor is null || expected is null || expected.Location is null || expected.PoolId <= 0 ||
                expected.PoolRevision <= 0 || expected.PoolFence < 0) throw ReconciliationStockSourceRecords.Unavailable();
            actor.Validate(); expected.Location.Validate();
            var capturedAt = clock.UtcNow;
            if (capturedAt == default || capturedAt.Offset != TimeSpan.Zero) throw ReconciliationStockSourceRecords.Unavailable();
            await ReconciliationStockSourceStorage.RequireAsync(database, ct);
            await SuspensionStorageAsync(ct);
            var snapshot = await authorization.LoadAsync(actor, tx, ct);
            // The existing source owner authenticates current AND historical disabled mappings.
            // An old channel cannot be excluded from scope or unresolved work by removing its alias.
            var context = await SuspensionContextAsync(actor, snapshot, tx, expected.PoolId, Permissions.StockSourceRead, ct);
            if (context.Pool["location"] != StockSourceSetupBson.Location(expected.Location) ||
                context.Pool["revision"] != expected.PoolRevision || context.Pool["fence"] != expected.PoolFence)
                throw ReconciliationStockSourceRecords.Changed();
            var facts = await new ReconciliationStockSourceRecords(database).ReadAsync(tx, this, expected, ct);
            if (StockSourceSetupBson.Time(facts.Head["updatedAt"]) > capturedAt || facts.Head["fence"].AsInt64 >= long.MaxValue - 2)
                throw ReconciliationStockSourceRecords.Unavailable();
            if ((await Rows(ReconciliationStockSourceRecords.Heads).UpdateOneAsync(tx, facts.Head,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
                throw ReconciliationStockSourceRecords.Changed();
            // This owns no pool CAS: the gate writes the canonical pool after every domain reader.
            var currentPool = await Rows("inventoryPools").Find(tx, F.Eq("_id", expected.PoolId)).SingleOrDefaultAsync(ct);
            if (currentPool is null || !currentPool.Equals(context.Pool)) throw ReconciliationStockSourceRecords.Changed();
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, context.Resources, tx, ct);
            repository.RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || clock.UtcNow < capturedAt)
                throw ReconciliationStockSourceRecords.Unavailable();
            ct.ThrowIfCancellationRequested();
            return new(StockSourceObligationDomain.SourceReconciliation, expected, facts.Head["revision"].AsInt64, true, facts.OpenCount);
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
}
