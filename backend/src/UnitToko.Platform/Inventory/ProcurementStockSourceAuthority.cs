using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    internal async Task<StockSourceObligationResult> ReadProcurementHeldStockSourceObligationAsync(IClientSessionHandle tx,
        ActorContext actor, StockSourceObligationExpectation expected, CancellationToken ct)
    {
        try {
            var repository = new InventoryRepository(database); repository.RequireSession(tx);
            var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
            if (actor is null || expected is null || expected.Location is null || expected.PoolId <= 0 ||
                expected.PoolRevision <= 0 || expected.PoolFence < 0) throw ProcurementStockSourceRecords.Unavailable();
            actor.Validate(); expected.Location.Validate(); var capturedAt = clock.UtcNow;
            if (capturedAt == default || capturedAt.Offset != TimeSpan.Zero) throw ProcurementStockSourceRecords.Unavailable();
            await ProcurementStockSourceStorage.RequireAsync(database, ct); await SuspensionStorageAsync(ct);
            var snapshot = await authorization.LoadAsync(actor, tx, ct);
            // Existing owning authority covers the canonical owner and every historical or
            // disabled alias. Immutable receipts themselves carry their exact actual pool ID.
            var context = await SuspensionContextAsync(actor, snapshot, tx, expected.PoolId, Permissions.StockSourceRead, ct);
            if (context.Pool["location"] != StockSourceSetupBson.Location(expected.Location) ||
                context.Pool["revision"] != expected.PoolRevision || context.Pool["fence"] != expected.PoolFence)
                throw ProcurementStockSourceRecords.Changed();
            var facts = await new ProcurementStockSourceRecords(database).ReadAsync(tx,
                new ProcurementStockSourceEvents(database, ids, clock), expected, ct);
            if (StockSourceSetupBson.Time(facts.Head["updatedAt"]) > capturedAt || facts.Head["fence"].AsInt64 >= long.MaxValue - 2)
                throw ProcurementStockSourceRecords.Unavailable();
            if ((await Rows(ProcurementStockSourceRecords.Heads).UpdateOneAsync(tx, facts.Head,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
                throw ProcurementStockSourceRecords.Changed();
            var currentPool = await Rows("inventoryPools").Find(tx, F.Eq("_id", expected.PoolId)).SingleOrDefaultAsync(ct);
            if (currentPool is null || currentPool != context.Pool) throw ProcurementStockSourceRecords.Changed();
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, context.Resources, tx, ct);
            repository.RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || clock.UtcNow < capturedAt)
                throw ProcurementStockSourceRecords.Unavailable();
            ct.ThrowIfCancellationRequested();
            // Even a genuine empty receipt set says nothing about unbound purchase orders,
            // arrivals, inspections or pending supplier action. Never advertise full coverage.
            return new(StockSourceObligationDomain.Procurement, expected, facts.Head["revision"].AsInt64, false, facts.OpenCount);
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
}
