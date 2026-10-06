using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

/// <summary>Mechanical shared write fence for source topology and causal legacy admission.
/// No business permission, source binding, source activation or zero-work proof is created here.</summary>
internal sealed class StockSourceTopologyFence(IMongoDatabase database)
{
    internal const string Collection = "stockSourceTopologyFences";

    // ponytail: one lane per organization serializes topology and causal admission;
    // narrow to warehouse lanes only if measured contention warrants their extra coordination.
    internal async Task FenceAsync(IClientSessionHandle tx, long organizationId, CancellationToken ct)
    {
        try
        {
            new InventoryRepository(database).RequireSession(tx);
            var transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
            if (organizationId <= 0)
                throw new DomainValidationException("invalid_stock_topology_scope", "An exact stock organization is required");
            await InventoryRepository.RequireStorageAsync(database, Collection, StockSourceTopologySchemas.Validator(), new BsonDocument("_id", 1), ct);
            var rows = database.GetCollection<BsonDocument>(Collection);
            var row = await rows.Find(tx, new BsonDocument("_id", organizationId)).SingleOrDefaultAsync(ct);
            if (row is null)
            {
                await rows.InsertOneAsync(tx, new BsonDocument { { "_id", organizationId }, { "schemaVersion", 1 },
                    { "kind", "stocksource.topology.fence" }, { "revision", 1L } }, cancellationToken: ct);
            }
            else
            {
                if (row.ElementCount != 4 || !row["_id"].IsInt64 || row["_id"] != organizationId || !row["schemaVersion"].IsInt32 || row["schemaVersion"] != 1 ||
                    row["kind"] != "stocksource.topology.fence" || !row["revision"].IsInt64 || row["revision"].AsInt64 <= 0 ||
                    row["revision"].AsInt64 >= long.MaxValue - 2)
                    throw new DomainValidationException("stock_topology_fence_unavailable", "Stock topology cannot be safely fenced");
                if ((await rows.UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("revision", 1L), cancellationToken: ct)).ModifiedCount != 1)
                    throw new PersistenceConflictException("stock_topology_changed", "Stock source topology changed");
            }
            new InventoryRepository(database).RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber)
                throw new DomainValidationException("inventory_transaction_required", "Stock topology requires the original transaction");
            ct.ThrowIfCancellationRequested();
        }
        catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
}
