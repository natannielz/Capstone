using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    internal async Task<StockSourceObligationResult> ReadFulfillmentStockSourceObligationAsync(IClientSessionHandle tx,
        ActorContext actor, StockSourceObligationExpectation expected, CancellationToken ct)
    {
        try {
            var repository = new InventoryRepository(database); repository.RequireSession(tx);
            var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
            if (actor is null || expected is null || expected.Location is null || expected.PoolId <= 0 ||
                expected.PoolRevision <= 0 || expected.PoolFence < 0) throw FulfillmentStockSourceRecords.Unavailable();
            actor.Validate(); expected.Location.Validate();
            var capturedAt = clock.UtcNow;
            if (capturedAt == default || capturedAt.Offset != TimeSpan.Zero) throw FulfillmentStockSourceRecords.Unavailable();
            await FulfillmentStockSourceStorage.RequireAsync(database, ct);
            await SuspensionStorageAsync(ct);
            var snapshot = await authorization.LoadAsync(actor, tx, ct);
            // The existing source owner authenticates current AND historical disabled mappings.
            // An old channel cannot be excluded from scope or unresolved work by removing its alias.
            var context = await SuspensionContextAsync(actor, snapshot, tx, expected.PoolId, Permissions.StockSourceRead, ct);
            if (context.Pool["location"] != StockSourceSetupBson.Location(expected.Location) ||
                context.Pool["revision"] != expected.PoolRevision || context.Pool["fence"] != expected.PoolFence)
                throw FulfillmentStockSourceRecords.Changed();
            var history = new ShipmentDispatchValueRecords.HistoricalRead(database, clock, tx);
            var facts = await new FulfillmentStockSourceRecords(database).ReadAsync(tx,
                new FulfillmentStockSourceEvents(database, ids, clock, history: history), expected, ct);
            if (StockSourceSetupBson.Time(facts.Head["updatedAt"]) > capturedAt || facts.Head["fence"].AsInt64 >= long.MaxValue - 2)
                throw FulfillmentStockSourceRecords.Unavailable();
            if ((await Rows(FulfillmentStockSourceRecords.Heads).UpdateOneAsync(tx, facts.Head,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
                throw FulfillmentStockSourceRecords.Changed();
            // This owns no pool CAS: the gate writes the canonical pool after every domain reader.
            var currentPool = await Rows("inventoryPools").Find(tx, F.Eq("_id", expected.PoolId)).SingleOrDefaultAsync(ct);
            if (currentPool is null || !currentPool.Equals(context.Pool)) throw FulfillmentStockSourceRecords.Changed();
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, context.Resources, tx, ct);
            history.Require(database, tx);
            repository.RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || clock.UtcNow < capturedAt)
                throw FulfillmentStockSourceRecords.Unavailable();
            ct.ThrowIfCancellationRequested();
            return new(StockSourceObligationDomain.Fulfillment, expected, facts.Head["revision"].AsInt64, true, facts.OpenCount);
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
}
