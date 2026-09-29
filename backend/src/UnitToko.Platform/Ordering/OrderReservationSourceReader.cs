using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;

namespace UnitToko.Platform.Ordering;

/// <summary>Ordering's real source adapter. Reads the order and its reservations just inserted in the
/// owner transaction; no browser or caller-supplied stock legs reach the Inventory writer.</summary>
internal sealed class OrderReservationSourceReader(IMongoDatabase database, Func<CancellationToken, Task> finalAuthority) : IInventorySourceReader
{
    public string Namespace => "ordering.order";
    public IMongoDatabase Database => database;
    public async Task<InventorySourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, InventorySource source, long expectedRevision, CancellationToken ct)
    {
        var f = Builders<BsonDocument>.Filter;
        if (source.Namespace != Namespace || source.EventKind != "reserve" || expectedRevision != 1) throw OrderAdmissionPolicy.Invalid();
        var orders = database.GetCollection<BsonDocument>(B.Orders);
        var order = await orders.Find(tx, f.Eq("_id", source.DocumentId) & f.Eq("createdBy", actor.UserId) & OrderSubmissionSchemas.Validator(B.Orders)).SingleOrDefaultAsync(ct);
        if (order is null || order["revision"].AsInt64 != 1 || B.SnapshotHash(order) != order["snapshotHash"].AsString || order["fence"].AsInt64 >= long.MaxValue - 1) throw OrderAdmissionPolicy.Invalid();
        var expected = order["allocations"].AsBsonArray.Select(item => item.AsBsonDocument).Where(item => item["poolId"].AsInt64 == source.Occurrence).ToArray();
        var store = database.GetCollection<BsonDocument>(B.Reservations);
        var rows = await store.Find(tx, f.Eq("orderId", source.DocumentId) & f.Eq("allocation.poolId", source.Occurrence)).Limit(101).ToListAsync(ct);
        if (rows.Count is < 1 or > 100 || rows.Count != expected.Length) throw OrderAdmissionPolicy.Invalid();
        foreach (var row in rows) {
            var allocation = row["allocation"].AsBsonDocument;
            if (row["revision"].AsInt64 != 1 || row["remaining"] != allocation["packages"] || row["dispatched"] != 0L || row["released"] != 0L ||
                row["fence"].AsInt64 >= long.MaxValue - 1 || !order["suborderIds"].AsBsonArray.Contains(row["suborderId"]) ||
                expected.Count(value => CatalogSchemas.SemanticEquals(value, allocation)) != 1) throw OrderAdmissionPolicy.Invalid();
            var match = f.And(row, OrderSubmissionSchemas.Validator(B.Reservations));
            if ((await store.UpdateOneAsync(tx, match, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw OrderAdmissionPolicy.Invalid();
        }
        if ((await orders.UpdateOneAsync(tx, order, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw OrderAdmissionPolicy.Invalid();
        var allocations = rows.Select(row => B.Allocation(row["allocation"].AsBsonDocument)).ToArray();
        if (allocations.Select(item => item.Pool).Distinct().Count() != 1) throw OrderAdmissionPolicy.Invalid();
        await finalAuthority(ct);
        return new(source, 1, allocations[0].Pool, [.. allocations.Select(item => item.Change)]);
    }
}
