using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;

namespace UnitToko.Platform.Ordering;

public sealed partial class OrderSubmissionService
{
    private async Task VerifyDispatchHistoryAsync(IClientSessionHandle tx, BsonDocument order, BsonDocument shipment,
        Dictionary<long, BsonDocument> expected, CancellationToken ct) {
        var suborderId = shipment["suborderId"].AsInt64;
        var sub = await Rows(B.Suborders).Find(tx, F.Eq("_id", suborderId) & F.Eq("orderId", order["_id"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
        var reserved = expected.Values.Where(row => row["suborderId"] == suborderId).OrderBy(row => row["_id"].AsInt64).ToArray();
        var quote = B.Quote(order["quote"].AsBsonDocument);
        var lines = quote.Lines.Where(line => sub["skuIds"].AsBsonArray.Contains(new BsonInt64(line.Definition.SkuId.Value)))
            .Select(line => new FulfillmentOrderLine(line.Definition.SkuId, line.PublicId, line.SkuCode, line.FamilyName, line.Definition, line.Packages,
                checked((int)reserved.Where(row => row["allocation"]["skuId"] == line.Definition.SkuId.Value).Sum(row => row["remaining"].AsInt64)))).ToImmutableArray();
        var before = new FulfillmentOrderView(new(order["_id"].AsInt64), new(suborderId), shipment["decisionRevision"].AsInt64,
            FulfillmentReview(order, suborderId, shipment["decisionRevision"].AsInt64, reserved), order["terms"]["buyer"]["name"].AsString,
            OrderDraftBson.Input(order["input"].AsBsonDocument).Delivery!, lines, true, OrderDraftBson.Time(order["orderedAt"]));
        if (!CatalogSchemas.SemanticEquals(ShipmentPreparationBson.Source(before), shipment["source"]) ||
            !CatalogSchemas.SemanticEquals(ShipmentPreparationBson.Scope(ReviewResource(order, sub)), shipment["scope"])) throw Invalid();
        var picked = ShipmentDispatchBson.Lots(shipment["pickedLots"].AsBsonArray);
        _ = ShipmentDispatchPolicy.Require(before, before.Review, ShipmentPreparationBson.Lines(shipment["lines"].AsBsonArray),
            [.. reserved.Select(row => new ShipmentReservationFact(new(row["_id"].AsInt64), new(row["allocation"]["skuId"].AsInt64),
                new(row["allocation"]["definitionId"].AsInt64), new(row["allocation"]["lotId"].AsInt64), checked((int)row["remaining"].AsInt64)))], picked);
        var effects = shipment["stockEffects"].AsBsonArray.Select(value => value.AsBsonDocument).ToArray();
        foreach (var effect in effects) {
            var reservation = reserved.SingleOrDefault(row => row["_id"] == effect["reservationId"]) ?? throw Invalid();
            OrderDispatchFacts.Require(reservation, effect, false);
            var packages = effect["allocation"]["packages"].AsInt64;
            reservation["remaining"] = checked(reservation["remaining"].AsInt64 - packages);
            reservation["dispatched"] = checked(reservation["dispatched"].AsInt64 + packages);
            reservation["revision"] = checked(reservation["revision"].AsInt64 + 1);
        }
        foreach (var pool in effects.GroupBy(row => row["allocation"]["poolId"].AsInt64)) {
            var source = new InventorySource("fulfillment.shipment", shipment["_id"].AsInt64, "dispatch", pool.Key);
            var movement = await Rows("movements").Find(tx, F.Eq("sourceEvent", source.EventKey) & InventoryMovementSchemas.Validator()).SingleOrDefaultAsync(ct) ?? throw Invalid();
            if (movement["actorId"] != shipment["dispatchedBy"] || movement["actorGrantVersion"] != shipment["actorGrantVersion"] || movement["sourceRevision"] != 1L ||
                movement["physicalPoolId"] != pool.Key || movement["poolRevision"] != pool.First()["allocation"]["poolRevision"] ||
                movement["source"]["namespace"] != source.Namespace || movement["source"]["documentId"] != source.DocumentId || movement["source"]["eventKind"] != source.EventKind ||
                movement["source"]["occurrence"] != source.Occurrence || movement["legs"].AsBsonArray.Count != pool.Count()) throw Invalid();
            foreach (var effect in pool) {
                var allocation = B.Allocation(effect["allocation"].AsBsonDocument);
                var leg = movement["legs"].AsBsonArray.SingleOrDefault(value => value["balanceId"].AsInt64 == allocation.Change.Id) ?? throw Invalid();
                var stockBefore = InventoryBson.ReadBalance(leg["before"].AsBsonDocument); var stockAfter = InventoryBson.ReadBalance(leg["after"].AsBsonDocument);
                if (leg["action"] != "Dispatch" || leg["packages"].AsInt64 != allocation.Change.Packages || stockBefore.Id != allocation.Change.Id ||
                    stockBefore.PhysicalPoolId != allocation.Pool.Id || stockBefore.Balance.Key != allocation.Change.Key || stockBefore.Balance.Revision != allocation.Change.ExpectedRevision ||
                    stockAfter != stockBefore with { Balance = StockBalancePolicy.Dispatch(stockBefore.Balance, allocation.Change.ExpectedRevision, allocation.Change.Packages) }) throw Invalid();
            }
        }
    }
}
