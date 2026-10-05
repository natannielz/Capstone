using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Procurement;

/// <summary>Partial custody adapter only. Do not register as complete activation coverage:
/// upstream inbound source binding and terminal resolution owners are still absent.</summary>
internal sealed class ProcurementStockSourceObligationReader(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
    : IStockSourceObligationReader
{
    public StockSourceObligationDomain Domain => StockSourceObligationDomain.Procurement;
    public IMongoDatabase Database => database;
    public Task<StockSourceObligationResult> ReadAndFenceAsync(IClientSessionHandle transaction, ActorContext actor,
        StockSourceObligationExpectation expected, CancellationToken cancellationToken) =>
        new StockSourceSetupService(database, unit, ids, clock).ReadProcurementHeldStockSourceObligationAsync(transaction, actor, expected, cancellationToken);
}
