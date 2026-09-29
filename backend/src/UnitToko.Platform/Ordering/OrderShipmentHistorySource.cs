using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Ordering;

public sealed partial class OrderSubmissionService
{
    /// <summary>Internal owner validation for an already scope-authorized immutable shipment.
    /// This is not an authorization or disclosure endpoint. Graph verifies all accepted dispatch
    /// movements against the original order and exact reservation history, including later cancellation.</summary>
    internal async Task<BsonDocument> RequireShipmentHistoryAsync(IClientSessionHandle tx, ActorContext actor, BsonDocument shipment, CancellationToken ct) {
        var order = await GraphAsync(tx, actor, shipment["orderId"].AsInt64, ct, requireOwner: false);
        if (!order["suborderIds"].AsBsonArray.Contains(shipment["suborderId"])) throw Invalid();
        await TouchAsync(tx, order, ct);
        return order["terms"]["buyer"].DeepClone().AsBsonDocument;
    }

    // Fulfillment has already authorized this shipment; only its invoice integration consumes these frozen commercial facts.
    internal async Task<BsonDocument> InvoiceCommercialAsync(IClientSessionHandle tx, ActorContext actor, BsonDocument shipment, CancellationToken ct) {
        var order = await GraphAsync(tx, actor, shipment["orderId"].AsInt64, ct, requireOwner: false);
        if (!order["suborderIds"].AsBsonArray.Contains(shipment["suborderId"])) throw Invalid();
        await TouchAsync(tx, order, ct);
        return new BsonDocument { { "orderId", order["_id"] }, { "orderHash", order["snapshotHash"] }, { "orderedAt", order["orderedAt"] },
            { "terms", order["terms"].DeepClone() }, { "quote", order["quote"].DeepClone() } };
    }
}
