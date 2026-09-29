using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

internal enum StockSourceObligationDomain
{
    Orders, Reservations, Fulfillment, Procurement, AfterSales, SourceReconciliation, InventoryOperations
}

internal sealed record StockSourceObligationExpectation(long PoolId, StockLocation Location, long PoolRevision, long PoolFence);
internal sealed record StockSourceObligationResult(StockSourceObligationDomain Domain, StockSourceObligationExpectation Pool,
    long ProducerRevision, bool Complete, long OpenCount);

/// <summary>Trusted owning-module adapter, never request data. Validate actual storage and load/fence
/// complete producer state in this session, across ALL channels of the canonical pool, including
/// disabled mappings. Missing storage/state is unavailable, not zero. Do not perform external I/O.
/// Every command creating/changing an obligation must also conditionally WRITE this actual pool;
/// otherwise an empty query cannot protect against a concurrent insertion. This reader fences its
/// own producer records; the gate owns the single pool fence. It must not commit/retry the session.</summary>
internal interface IStockSourceObligationReader
{
    StockSourceObligationDomain Domain { get; }
    IMongoDatabase Database { get; }
    Task<StockSourceObligationResult> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor,
        StockSourceObligationExpectation pool, CancellationToken ct);
}

/// <summary>Valid only within the caller's still-open transaction. Pool is the incremented fence;
/// Results bind its predecessor. This is not a durable drain certificate or accepted stock control.</summary>
internal sealed record StockSourceDrainCheck(StockSourceObligationExpectation Pool, ImmutableArray<StockSourceObligationResult> Results);

/// <summary>Private prerequisite for the owning control/handoff transaction. Requires every operational
/// producer, not just those currently implemented. The owner must separately verify policy/configuration/
/// suspension provenance, actual handoff evidence, actor/person/scope/MFA authority and current decisions,
/// then atomically persist acceptance/audit/receipt/outbox. No production readers are registered here.
/// A successful zero-work check does not activate a pool, approve control, or promise stock.</summary>
internal sealed class StockSourceObligationGate
{
    private static readonly ImmutableArray<StockSourceObligationDomain> Required = [.. Enum.GetValues<StockSourceObligationDomain>()];
    private readonly IMongoDatabase database;
    private readonly InventoryRepository repository;
    private readonly ImmutableDictionary<StockSourceObligationDomain, IStockSourceObligationReader> readers;

    internal StockSourceObligationGate(IMongoDatabase database, IEnumerable<IStockSourceObligationReader> readers)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(readers);
        this.database = database; repository = new(database);
        var registered = ImmutableDictionary.CreateBuilder<StockSourceObligationDomain, IStockSourceObligationReader>();
        foreach (var reader in readers.ToArray())
        {
            if (reader is null || !Enum.IsDefined(reader.Domain) || reader.Database is null ||
                !ReferenceEquals(reader.Database.Client, database.Client) || reader.Database.DatabaseNamespace != database.DatabaseNamespace ||
                !registered.TryAdd(reader.Domain, reader))
                throw new ArgumentException("Obligation readers must uniquely own known domains in the same application database and client", nameof(readers));
        }
        this.readers = registered.ToImmutable();
    }

    internal async Task<StockSourceDrainCheck> RequireDrainedAsync(IClientSessionHandle tx, ActorContext actor,
        StockSourceObligationExpectation expected, CancellationToken ct = default)
    {
        try
        {
            repository.RequireSession(tx); ct.ThrowIfCancellationRequested();
            var transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
            void RequireSameTransaction()
            {
                repository.RequireSession(tx);
                if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber)
                    throw Invalid("inventory_transaction_required");
            }
            if (actor is null || expected is null || expected.PoolId <= 0 || expected.Location is null ||
                expected.PoolRevision <= 0 || expected.PoolFence < 0 || expected.PoolFence == long.MaxValue)
                throw Invalid("invalid_stock_source_obligation_request");
            actor.Validate(); expected.Location.Validate();
            if (!Enum.IsDefined(actor.Assurance)) throw Invalid("invalid_stock_source_obligation_request");
            if (Required.Any(domain => !readers.ContainsKey(domain)))
                throw Invalid("stock_source_obligation_coverage_missing");
            await InventoryRepository.RequireStorageAsync(database, "inventoryPools",
                StockSourceSetupSchemas.Validator("inventoryPools"), StockSourceSetupSchemas.Key("inventoryPools"), ct);
            RequireSameTransaction();
            var pools = database.GetCollection<BsonDocument>("inventoryPools");
            var pool = await pools.Find(tx, new BsonDocument("_id", expected.PoolId)).SingleOrDefaultAsync(ct);
            InventoryGraph.RequirePool(pool);
            if (pool!["active"].AsBoolean || pool["revision"].AsInt64 != expected.PoolRevision ||
                pool["fence"].AsInt64 != expected.PoolFence || InventoryGraph.Location(pool["location"].AsBsonDocument) != expected.Location)
                throw Invalid("stock_source_obligations_changed");

            var results = ImmutableArray.CreateBuilder<StockSourceObligationResult>(Required.Length);
            foreach (var domain in Required)
            {
                ct.ThrowIfCancellationRequested();
                var result = await readers[domain].ReadAndFenceAsync(tx, actor, expected, ct);
                RequireSameTransaction();
                if (result is null || result.Domain != domain || result.Pool != expected ||
                    result.ProducerRevision <= 0 || !result.Complete || result.OpenCount < 0)
                    throw Invalid("stock_source_obligation_result_unavailable");
                // Counts describe different kinds of work and can overlap; never sum them.
                if (result.OpenCount != 0) throw Invalid("stock_source_obligations_outstanding");
                results.Add(result);
            }
            ct.ThrowIfCancellationRequested();
            // This write catches changes made after any reader's snapshot, including new obligations
            // for which there was no source row to lock. All producers must share this pool fence.
            var changed = await pools.UpdateOneAsync(tx, pool, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct);
            RequireSameTransaction();
            if (changed.ModifiedCount != 1)
                throw new PersistenceConflictException("stock_source_obligations_changed", "The reviewed stock source changed");
            ct.ThrowIfCancellationRequested();
            return new(expected with { PoolFence = expected.PoolFence + 1 }, results.ToImmutable());
        }
        catch { await InventoryRepository.AbortAsync(tx); throw; }
    }

    private static DomainValidationException Invalid(string code) =>
        new(code, "The stock source requires complete current producer-backed obligation checks");
}
