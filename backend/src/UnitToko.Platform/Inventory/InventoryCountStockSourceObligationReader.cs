using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

/// <summary>Exhaustive authenticated current count membership. Original unbound
/// work remains unresolved; no observation is interpreted as a physical adjustment.</summary>
internal sealed class InventoryCountStockSourceObligationReader(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
    : IStockSourceObligationReader
{
    public StockSourceObligationDomain Domain => StockSourceObligationDomain.InventoryOperations;
    public IMongoDatabase Database => database;
    public Task<StockSourceObligationResult> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, StockSourceObligationExpectation pool, CancellationToken ct) =>
        new StockSourceSetupService(database, unit, ids, clock).ReadInventoryCountStockSourceObligationAsync(tx, actor, pool, ct);
}

public sealed partial class StockSourceSetupService
{
    internal async Task<StockSourceObligationResult> ReadInventoryCountStockSourceObligationAsync(IClientSessionHandle tx,
        ActorContext actor, StockSourceObligationExpectation expected, CancellationToken ct) {
        try {
            new InventoryRepository(database).RequireSession(tx); var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; var at = Now();
            if (actor is null || expected is null || expected.PoolId <= 0 || expected.Location is null || expected.PoolRevision <= 0 || expected.PoolFence < 0)
                throw InventoryCountStockSourceRecords.Unavailable();
            actor.Validate(); expected.Location.Validate(); await InventoryCountStockSourceStorage.RequireAsync(database, ct);
            await new StockSourceTopologyFence(database).FenceAsync(tx, expected.Location.OrganizationId.Value, ct);
            var authority = await authorization.LoadAsync(actor, tx, ct);
            var facts = await ReadInventoryCountSourceFactsAsync(actor, authority, tx, expected.PoolId, Permissions.StockSourceRead, ct);
            if (facts.Pool["location"] != StockSourceSetupBson.Location(expected.Location) || facts.Pool["revision"] != expected.PoolRevision || facts.Pool["fence"] != expected.PoolFence)
                throw InventoryCountStockSourceRecords.Changed();
            var counts = new StockCountService(database, unit, ids, clock);
            var snapshot = await new InventoryCountStockSourceRecords(database).ReadAsync(tx, counts, this, expected, ct);
            var coverageOwner = new InventoryCountCoverage(database);
            var coverage = await coverageOwner.ReadAsync(tx, counts, this, expected.Location.OrganizationId.Value, ct);
            var complete = await coverageOwner.ForPoolAsync(tx, coverage, facts, snapshot.OpenCount, ct);
            await coverageOwner.FenceAsync(tx, coverage, ct);
            if (snapshot.Head["fence"].AsInt64 >= long.MaxValue - 2 || StockSourceSetupBson.Time(snapshot.Head["updatedAt"]) > at) throw InventoryCountStockSourceRecords.Unavailable();
            if ((await Rows(InventoryCountStockSourceRecords.Heads).UpdateOneAsync(tx, snapshot.Head,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw InventoryCountStockSourceRecords.Changed();
            var pool = await Rows("inventoryPools").Find(tx, F.Eq("_id", expected.PoolId)).SingleOrDefaultAsync(ct);
            if (pool is null || pool != facts.Pool) throw InventoryCountStockSourceRecords.Changed();
            var person = await ReadInventoryCountPersonAsync(tx, actor.UserId, ct); await FenceInventoryCountPersonAsync(tx, person, ct);
            await CompleteAuthorityAsync(actor, authority, Permissions.StockSourceRead, facts.Resources, tx, ct); person.RequireCurrent(tx, Now());
            new InventoryRepository(database).RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || Now() < at) throw InventoryCountStockSourceRecords.Unavailable();
            ct.ThrowIfCancellationRequested();
            return new(StockSourceObligationDomain.InventoryOperations, expected, coverage.Head["revision"].AsInt64, complete.Complete, complete.OpenCount);
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
}
