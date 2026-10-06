using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    internal async Task<(BsonDocument Pool, BsonDocument Configuration, long SuspensionRevision, UnitToko.Modules.Inventory.StockLocation[] Locations)>
        ReadProcurementCoverageTopologyAsync(IClientSessionHandle tx, long poolId, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx);
        var context = await HeldConfigurationAsync(tx, poolId, ct);
        return (context.Pool.DeepClone().AsBsonDocument, context.Configuration.DeepClone().AsBsonDocument,
            context.Latest?["suspensionRevision"].AsInt64 ?? 0, context.Locations);
    }
    internal async Task RequireProcurementCoverageTopologyAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx);
        var context = await HeldConfigurationAsync(tx, row["poolId"].AsInt64, ct);
        var configuration = row["configuration"].AsBsonDocument;
        // Reuse the actual historical setup/revision/alias graph. These are binding facts,
        // not an invented operating-policy proposal or permission to receive/release stock.
        var binding = new BsonDocument { { "poolId", row["poolId"] }, { "poolRevision", row["poolRevision"] },
            { "suspensionRevision", row["suspensionRevision"] }, { "proposedAt", row["recordedAt"] }, { "configuration", configuration } };
        await RequireOperatingPolicyBindingAsync(tx, binding, context, ct);
        if (configuration["source"]["poolRevision"] != row["poolRevision"] ||
            StockSourceSetupBson.Time(configuration["source"]["approvedAt"]) > StockSourceSetupBson.Time(row["recordedAt"]) ||
            configuration["source"]["input"]["location"] != row["location"] &&
            !configuration["mappings"].AsBsonArray.Any(mapping => mapping["requestedLocation"] == row["location"]))
            throw ProcurementStockSourceCoverage.Unavailable();
    }
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
            await new StockSourceTopologyFence(database).FenceAsync(tx, expected.Location.OrganizationId.Value, ct);
            var snapshot = await authorization.LoadAsync(actor, tx, ct);
            // Existing owning authority covers the canonical owner and every historical or
            // disabled alias. Immutable receipts themselves carry their exact actual pool ID.
            var context = await SuspensionContextAsync(actor, snapshot, tx, expected.PoolId, Permissions.StockSourceRead, ct);
            if (context.Pool["location"] != StockSourceSetupBson.Location(expected.Location) ||
                context.Pool["revision"] != expected.PoolRevision || context.Pool["fence"] != expected.PoolFence)
                throw ProcurementStockSourceRecords.Changed();
            var facts = await new ProcurementStockSourceRecords(database).ReadAsync(tx,
                new ProcurementStockSourceEvents(database, ids, clock), expected, ct);
            var coverage = await new ProcurementStockSourceCoverage(database, ids, clock).ReadAsync(tx, expected, context.Locations, ct);
            if (StockSourceSetupBson.Time(facts.Head["updatedAt"]) > capturedAt || facts.Head["fence"].AsInt64 >= long.MaxValue - 2)
                throw ProcurementStockSourceRecords.Unavailable();
            if ((await Rows(ProcurementStockSourceRecords.Heads).UpdateOneAsync(tx, facts.Head,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
                throw ProcurementStockSourceRecords.Changed();
            if (StockSourceSetupBson.Time(coverage.Head["updatedAt"]) > capturedAt || coverage.Head["fence"].AsInt64 >= long.MaxValue - 2 ||
                (await Rows(ProcurementStockSourceCoverage.Heads).UpdateOneAsync(tx, coverage.Head,
                    Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
                throw ProcurementStockSourceCoverage.Unavailable();
            var currentPool = await Rows("inventoryPools").Find(tx, F.Eq("_id", expected.PoolId)).SingleOrDefaultAsync(ct);
            if (currentPool is null || currentPool != context.Pool) throw ProcurementStockSourceRecords.Changed();
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, context.Resources, tx, ct);
            repository.RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || clock.UtcNow < capturedAt)
                throw ProcurementStockSourceRecords.Unavailable();
            ct.ThrowIfCancellationRequested();
            return new(StockSourceObligationDomain.Procurement, expected,
                checked(facts.Head["revision"].AsInt64 + coverage.Head["revision"].AsInt64 - 1), true,
                checked(facts.OpenCount + coverage.OpenCount));
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
}
