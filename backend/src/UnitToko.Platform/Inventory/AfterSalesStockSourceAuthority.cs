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
    internal async Task<StockSourceObligationResult> ReadAfterSalesStockSourceObligationAsync(IClientSessionHandle tx,
        ActorContext actor, StockSourceObligationExpectation expected, CancellationToken ct)
    {
        try {
            var repository = new InventoryRepository(database); repository.RequireSession(tx);
            var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
            if (actor is null || expected is null || expected.Location is null || expected.PoolId <= 0 ||
                expected.PoolRevision <= 0 || expected.PoolFence < 0) throw AfterSalesStockSourceRecords.Unavailable();
            actor.Validate(); expected.Location.Validate();
            var capturedAt = clock.UtcNow;
            if (capturedAt == default || capturedAt.Offset != TimeSpan.Zero) throw AfterSalesStockSourceRecords.Unavailable();
            await AfterSalesStockSourceStorage.RequireCoverageAsync(database, ct);
            await SuspensionStorageAsync(ct);
            var snapshot = await authorization.LoadAsync(actor, tx, ct);
            await new StockSourceTopologyFence(database).FenceAsync(tx, expected.Location.OrganizationId.Value, ct);
            // The existing source owner authenticates current AND historical disabled mappings.
            // An old channel cannot be excluded from scope or unresolved work by removing its alias.
            var context = await SuspensionContextAsync(actor, snapshot, tx, expected.PoolId, Permissions.StockSourceRead, ct);
            if (context.Pool["location"] != StockSourceSetupBson.Location(expected.Location) ||
                context.Pool["revision"] != expected.PoolRevision || context.Pool["fence"] != expected.PoolFence)
                throw AfterSalesStockSourceRecords.Changed();
            var facts = await new AfterSalesStockSourceRecords(database).ReadAsync(tx, new AfterSalesStockSourceEvents(database, ids, clock), expected, ct);
            var records = new AfterSalesStockSourceRecords(database); var owner = new AfterSalesStockSourceEvents(database, ids, clock);
            var causal = await records.CausalHistoryAsync(tx, owner, expected.PoolId, ct);
            await records.RequireCausalSourcesAsync(tx, owner, expected.PoolId, causal.History, null, ct);
            if (causal.Head["location"] != context.Pool["location"] || expected.PoolRevision < causal.History[^1]["poolRevision"].AsInt64 ||
                expected.PoolFence < causal.History[^1]["afterPoolFence"].AsInt64 || StockSourceSetupBson.Time(causal.Head["updatedAt"]) > capturedAt ||
                causal.Head["fence"].AsInt64 >= long.MaxValue - 2) throw AfterSalesStockSourceRecords.Unavailable();
            if (StockSourceSetupBson.Time(facts.Head["updatedAt"]) > capturedAt || facts.Head["fence"].AsInt64 >= long.MaxValue - 2)
                throw AfterSalesStockSourceRecords.Unavailable();
            if ((await Rows(AfterSalesStockSourceRecords.Heads).UpdateOneAsync(tx, facts.Head,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
                throw AfterSalesStockSourceRecords.Changed();
            if ((await Rows(AfterSalesStockSourceRecords.CausalHeads).UpdateOneAsync(tx, causal.Head,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw AfterSalesStockSourceRecords.Changed();
            // This owns no pool CAS: the gate writes the canonical pool after every domain reader.
            var currentPool = await Rows("inventoryPools").Find(tx, F.Eq("_id", expected.PoolId)).SingleOrDefaultAsync(ct);
            if (currentPool is null || !currentPool.Equals(context.Pool)) throw AfterSalesStockSourceRecords.Changed();
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, context.Resources, tx, ct);
            repository.RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || clock.UtcNow < capturedAt)
                throw AfterSalesStockSourceRecords.Unavailable();
            ct.ThrowIfCancellationRequested();
            return new(StockSourceObligationDomain.AfterSales, expected,
                checked(facts.Head["revision"].AsInt64 + causal.Head["revision"].AsInt64 - 1), true, checked(facts.OpenCount + causal.OpenCount));
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
}
