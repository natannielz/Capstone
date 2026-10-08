using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using B = UnitToko.Platform.Fulfillment.ShipmentDispatchBson;

namespace UnitToko.Platform.Fulfillment;

public sealed partial class ShipmentDispatchService
{
    internal void RequireAdmissionOwner(ApprovedShipmentDispatchAdmissionReader reader, IMongoDatabase expectedDatabase,
        IClock expectedClock, OrderSubmissionService expectedOrders) {
        if (!ReferenceEquals(admissionReader, reader) || !ReferenceEquals(clock, expectedClock) || !ReferenceEquals(orders, expectedOrders) ||
            !ReferenceEquals(database.Client, expectedDatabase.Client) || !database.DatabaseNamespace.Equals(expectedDatabase.DatabaseNamespace))
            throw ShipmentDispatchAdmissionPolicy.Invalid();
    }
    // Only this actual service's live command can authenticate its own newly
    // written movement before its complete receipt exists. No final callback is
    // invoked here: that callback is the admission reader currently asking.
    internal async Task<bool> RequireAdmissionPhysicalAsync(ApprovedShipmentDispatchAdmissionReader reader, IClientSessionHandle tx,
        ActorContext actor, ShipmentDispatchAdmission admission, long reservationId, InventoryBalanceDocument before, long packages, CancellationToken ct) {
        if (!ReferenceEquals(admissionReader, reader)) throw ShipmentDispatchAdmissionPolicy.Invalid();
        new InventoryRepository(database).RequireSession(tx);
        var physical = await new InventoryRepository(database).ReadAsync(tx, before.Id, ct);
        if (physical == before) return false;
        if (!valueCommands.TryGetValue(tx, out var context) || context.Actor != actor || !ReferenceEquals(context.Capture, admission.CapturedValue))
            throw ShipmentDispatchAdmissionPolicy.Invalid();
        context.Require(this);
        if (context.Row["phase"] is not BsonString { Value: "preparing" or "committed" } || context.Row["admission"] != B.Admission(admission) ||
            await Rows(B.Collection).Find(tx, context.Row).SingleOrDefaultAsync(ct) is null) throw ShipmentDispatchAdmissionPolicy.Invalid();
        var effects = context.Row["valueEffects"].AsBsonArray.Where(value => value["reservationId"] == reservationId && value["balanceId"] == before.Id).ToArray();
        if (effects.Length != 1) throw ShipmentDispatchAdmissionPolicy.Invalid();
        var effect = effects[0].AsBsonDocument;
        if (effect["before"] != InventoryBson.Balance(before) || effect["packages"] != packages || effect["movementId"].IsBsonNull ||
            !context.Capture.Lots.Any(value => value.Before == before)) throw ShipmentDispatchAdmissionPolicy.Invalid();
        var movement = await Rows("movements").Find(tx, new BsonDocument { { "_id", effect["movementId"] },
            { "$and", new BsonArray { InventoryMovementSchemas.Validator() } } }).SingleOrDefaultAsync(ct) ?? throw ShipmentDispatchAdmissionPolicy.Invalid();
        ShipmentDispatchValueRecords.RequireMovement(context.Row, effect, movement);
        if (physical != InventoryBson.ReadBalance(effect["after"].AsBsonDocument) ||
            await new InventoryRepository(database).ReadAsync(tx, before.Id, ct) != physical) throw ShipmentDispatchAdmissionPolicy.Invalid();
        context.Require(this); ct.ThrowIfCancellationRequested(); return true;
    }
}
