using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Fulfillment;

internal sealed class ShipmentDispatchStockSource(IMongoDatabase database, OrderSubmissionService orders,
    Func<CancellationToken, Task> finalAuthority) : IInventorySourceReader
{
    public string Namespace => "fulfillment.shipment";
    public IMongoDatabase Database => database;
    public async Task<InventorySourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, InventorySource source, long expectedRevision, CancellationToken ct) {
        if (source.Namespace != Namespace || source.EventKind != "dispatch" || expectedRevision != 1) throw ShipmentDispatchRecords.Invalid();
        var f = Builders<BsonDocument>.Filter;
        var shipment = await database.GetCollection<BsonDocument>(ShipmentDispatchBson.Collection).Find(tx, f.Eq("_id", source.DocumentId) &
            f.Eq("dispatchedBy", actor.UserId) & f.Eq("actorGrantVersion", actor.GrantVersion) & f.Eq("revision", 1L) & ShipmentDispatchSchemas.Validator())
            .SingleOrDefaultAsync(ct) ?? throw ShipmentDispatchRecords.Invalid();
        var facts = await orders.FenceShipmentReservationsAsync(tx, shipment, source.Occurrence, ct);
        await finalAuthority(ct);
        return new(source, expectedRevision, facts[0].Pool, [.. facts.Select(fact => fact.Change with { Action = InventoryBalanceAction.Dispatch })]);
    }
}
