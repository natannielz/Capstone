using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Ordering;

/// <summary>Real Ordering adapters only. These two cannot satisfy the seven-domain gate.</summary>
internal sealed class OrderStockSourceObligationReader(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
    : IStockSourceObligationReader
{
    public StockSourceObligationDomain Domain => StockSourceObligationDomain.Orders;
    public IMongoDatabase Database => database;
    public Task<StockSourceObligationResult> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor,
        StockSourceObligationExpectation pool, CancellationToken ct) =>
        OrderStockSourceReadFence.ReadAsync(database, unit, ids, clock, tx, actor, pool, Domain, ct);
}
internal sealed class ReservationStockSourceObligationReader(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
    : IStockSourceObligationReader
{
    public StockSourceObligationDomain Domain => StockSourceObligationDomain.Reservations;
    public IMongoDatabase Database => database;
    public Task<StockSourceObligationResult> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor,
        StockSourceObligationExpectation pool, CancellationToken ct) =>
        OrderStockSourceReadFence.ReadAsync(database, unit, ids, clock, tx, actor, pool, Domain, ct);
}
internal static class OrderStockSourceReadFence
{
    internal static async Task<StockSourceObligationResult> ReadAsync(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        IClientSessionHandle tx, ActorContext actor, StockSourceObligationExpectation pool, StockSourceObligationDomain domain, CancellationToken ct)
    {
        try {
            var repository = new InventoryRepository(database); repository.RequireSession(tx);
            var transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
            var capturedAt = clock.UtcNow;
            if (capturedAt == default || capturedAt.Offset != TimeSpan.Zero) throw OrderStockSourceRecords.Unavailable();
            await OrderStockSourceStorage.RequireAsync(database, ct);
            var authorization = new AuthorizationStore(database, unit, clock);
            var snapshot = await authorization.LoadAsync(actor, tx, ct);
            var resource = CatalogRepository.Resource(pool.Location.ToScope());
            var query = ScopePolicy.Compile(actor, snapshot, Permissions.StockSourceRead, clock.UtcNow); AuthorizationStore.Require(query);
            if (!ScopePolicy.IsResourceActive(resource, snapshot.Directory) || !query.Matches(resource)) throw new AuthorizationDeniedException("scope_denied");
            var facts = await new OrderStockSourceRecords(database).ReadAsync(tx, new(database, unit, ids, clock), pool, ct);
            if (StockSourceSetupBson.Time(facts.Head["updatedAt"]) > capturedAt) throw OrderStockSourceRecords.Unavailable();
            async Task Fence(string name, BsonDocument row) {
                if (row["fence"].AsInt64 >= long.MaxValue - 2 ||
                    (await database.GetCollection<BsonDocument>(name).UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
                    throw new PersistenceConflictException("order_source_obligations_changed", "The reviewed Ordering producer state changed");
            }
            foreach (var row in facts.Orders) await Fence("orders", row);
            foreach (var row in facts.Reservations) await Fence("reservations", row);
            await Fence(OrderStockSourceRecords.Heads, facts.Head);
            var clauses = query.Clauses.Where(clause => clause.Matches(resource)).ToArray();
            await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
                clauses.SelectMany(clause => clause.RequiredNodes).Concat(ScopePolicy.RequiredNodes(resource, snapshot.Directory)).Distinct(), [],
                snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
                snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
            repository.RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber) throw OrderStockSourceRecords.Unavailable();
            var current = await authorization.LoadAsync(actor, tx, ct);
            if (clock.UtcNow < capturedAt) throw OrderStockSourceRecords.Unavailable();
            if (!ScopePolicy.Authorize(actor, current, Permissions.StockSourceRead, resource, clock.UtcNow).Allowed) throw new AuthorizationDeniedException("scope_denied");
            ct.ThrowIfCancellationRequested();
            return new(domain, pool, facts.Head["revision"].AsInt64, true,
                domain == StockSourceObligationDomain.Orders ? facts.OpenOrders : facts.OpenReservations);
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
}
