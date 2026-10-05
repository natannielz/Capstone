using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Fulfillment;

/// <summary>Genuine physical custody only; other owning domains remain required. Does not
/// activate a source, transfer an exception, release inventory or grant financial acceptance.</summary>
internal sealed class AfterSalesStockSourceObligationReader(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
    : IStockSourceObligationReader
{
    public StockSourceObligationDomain Domain => StockSourceObligationDomain.AfterSales;
    public IMongoDatabase Database => database;
    public Task<StockSourceObligationResult> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor,
        StockSourceObligationExpectation pool, CancellationToken ct) =>
        new StockSourceSetupService(database, unit, ids, clock).ReadAfterSalesStockSourceObligationAsync(tx, actor, pool, ct);
}
