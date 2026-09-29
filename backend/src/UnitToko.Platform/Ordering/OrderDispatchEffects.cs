using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;

namespace UnitToko.Platform.Ordering;

public sealed partial class OrderSubmissionService
{
    internal async Task ApplyShipmentReservationsAsync(IClientSessionHandle tx, BsonDocument shipment, CancellationToken ct) {
        foreach (var effect in shipment["stockEffects"].AsBsonArray.Select(value => value.AsBsonDocument)) {
            var row = await Rows(B.Reservations).Find(tx, F.Eq("_id", effect["reservationId"]) & F.Eq("orderId", shipment["orderId"]) &
                F.Eq("suborderId", shipment["suborderId"]) & OrderSubmissionSchemas.Validator(B.Reservations)).SingleOrDefaultAsync(ct) ?? throw Invalid();
            OrderDispatchFacts.Require(row, effect, false);
            if (row["revision"].AsInt64 == long.MaxValue || row["fence"].AsInt64 >= long.MaxValue - 1) throw Invalid();
            var packages = effect["allocation"]["packages"].AsInt64;
            if ((await Rows(B.Reservations).UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("remaining", -packages).Inc("dispatched", packages)
                .Inc("revision", 1L).Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw Invalid();
        }
    }
    internal async Task<OrderStockAllocation[]> FenceShipmentReservationsAsync(IClientSessionHandle tx, BsonDocument shipment, long poolId, CancellationToken ct) {
        var effects = shipment["stockEffects"].AsBsonArray.Select(value => value.AsBsonDocument).Where(value => value["allocation"]["poolId"] == poolId).ToArray();
        if (effects.Length is < 1 or > 100) throw Invalid();
        foreach (var effect in effects) {
            var row = await Rows(B.Reservations).Find(tx, F.Eq("_id", effect["reservationId"]) & F.Eq("orderId", shipment["orderId"]) &
                F.Eq("suborderId", shipment["suborderId"]) & OrderSubmissionSchemas.Validator(B.Reservations)).SingleOrDefaultAsync(ct) ?? throw Invalid();
            OrderDispatchFacts.Require(row, effect, true);
            if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(B.Reservations).UpdateOneAsync(tx, row,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw Invalid();
        }
        var order = await Rows(B.Orders).Find(tx, F.Eq("_id", shipment["orderId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
        await TouchAsync(tx, order, ct);
        var facts = effects.Select(value => B.Allocation(value["allocation"].AsBsonDocument)).ToArray();
        if (facts.Select(value => value.Pool).Distinct().Count() != 1) throw Invalid();
        return facts;
    }
    internal async Task<long> NextShipmentSequenceAsync(IClientSessionHandle tx, ActorContext actor, long orderId, CancellationToken ct) {
        var order = await GraphAsync(tx, actor, orderId, ct, requireOwner: false);
        return checked((await DecisionGraphAsync(tx, order, ct)).DispatchSequence + 1);
    }
}
