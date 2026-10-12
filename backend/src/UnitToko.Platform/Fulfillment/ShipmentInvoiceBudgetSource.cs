using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Fulfillment;

public sealed partial class ShipmentQueryService
{
    internal Task<BsonDocument> CurrentInvoiceBudgetAsync(ActorContext actor, IClientSessionHandle tx, long orderId, CancellationToken ct) =>
        orders.CurrentInvoiceBudgetAsync(tx, actor, orderId, ct);
}
public sealed partial class ShipmentReceiptService
{
    internal Task<BsonDocument> CurrentInvoiceBudgetAsync(ActorContext actor, IClientSessionHandle tx, long orderId, CancellationToken ct) =>
        shipments.CurrentInvoiceBudgetAsync(actor, tx, orderId, ct);
}
