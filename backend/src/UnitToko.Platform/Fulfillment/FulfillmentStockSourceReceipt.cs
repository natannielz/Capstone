using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Fulfillment;

public sealed partial class ShipmentReceiptService
{
    internal async Task<FulfillmentStockSourceEvent> ReadFulfillmentStockSourceReceiptAsync(IClientSessionHandle tx,
        long id, CancellationToken ct)
    {
        var row = await Rows(ShipmentReceiptRecords.Receipts).Find(tx, F.Eq("_id", id) & OrderLineageSchemas.ReceiptValidator(ShipmentReceiptRecords.Receipts))
            .SingleOrDefaultAsync(ct) ?? throw FulfillmentStockSourceRecords.Unavailable();
        var source = await shipments.FulfillmentStockSourceDocumentAsync(tx, row["shipmentId"].AsInt64, ct);
        var confirmed = await VerifyReceiptAsync(tx, row, source, ct);
        var shipment = await new ShipmentDispatchRecords(database, shipments.StockSourceClock, shipments.StockSourceHistory, shipments.StockSourceLiveRead)
            .ReadAsync(tx, row["shipmentId"].AsInt64, ct);
        var result = ShipmentReceiptRecords.Result(row, ShipmentReceiptRecords.Confirm);
        var outbox = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", ShipmentReceiptRecords.Confirm + ":" + id)).SingleOrDefaultAsync(ct);
        // Receipt owner verifies exact consent, recipient/person independence, challenge/document,
        // quantities, audit and original command receipt. Outbox is independently authenticated here.
        if (outbox is null || outbox["schemaVersion"] != 1 || outbox["kind"] != ShipmentReceiptRecords.Confirm ||
            outbox["createdAt"] != row["recordedAt"] || !CatalogSchemas.SemanticEquals(outbox["payload"], result))
            throw FulfillmentStockSourceRecords.Unavailable();
        // Any exception retains custody until a genuine separate AfterSales owner transfers it.
        // No inferred closure from a rejected/missing quantity, refund or invoice status.
        var open = confirmed.Any(line => line.HasException);
        if (row["hasException"] != open) throw FulfillmentStockSourceRecords.Unavailable();
        return new("receipt", id, new BsonDocument { { "shipment", shipment }, { "receipt", row } },
            FulfillmentStockSourceEvents.PoolIds(shipment), ShipmentReceiptRecords.Audit(row, ShipmentReceiptRecords.Confirm),
            row["recordedAt"], row["shipmentId"].AsInt64, open);
    }
}
