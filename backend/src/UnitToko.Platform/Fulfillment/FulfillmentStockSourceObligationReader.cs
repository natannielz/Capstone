using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Fulfillment;

/// <summary>Genuine physical custody only; other owning domains remain required. Does not
/// activate a source, transfer an exception, release inventory or grant financial acceptance.</summary>
internal sealed class FulfillmentStockSourceObligationReader(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
    : IStockSourceObligationReader
{
    public StockSourceObligationDomain Domain => StockSourceObligationDomain.Fulfillment;
    public IMongoDatabase Database => database;
    public Task<StockSourceObligationResult> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor,
        StockSourceObligationExpectation pool, CancellationToken ct) =>
        new StockSourceSetupService(database, unit, ids, clock).ReadFulfillmentStockSourceObligationAsync(tx, actor, pool, ct);
}
