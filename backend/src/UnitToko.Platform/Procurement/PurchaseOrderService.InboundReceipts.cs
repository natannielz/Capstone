using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Mongo;
using I = UnitToko.Platform.Procurement.PurchaseOrderInboundBindingBson;
using B = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class PurchaseOrderService
{
    internal async Task RequirePlannedReceiptMembershipAsync(IClientSessionHandle tx, BsonDocument source,
        BsonDocument inspection, ProcurementStockReceiptInput input, CancellationToken ct) {
        var binding = await InboundRecords.ApprovedAsync(tx, source, ct);
        if (binding is null) return; // Retained unbound history remains unbound, never inferred.
        if (input.InspectionLineNumber < 1 || input.InspectionLineNumber > inspection["input"]["lines"].AsBsonArray.Count) throw B.Changed();
        var sku = inspection["input"]["lines"][input.InspectionLineNumber - 1]["requestSkuId"].AsInt64;
        var planned = I.Selection(PurchaseOrderBson.Input(source["proposal"]["input"]), binding.Value.Selection["allocations"])
            .Allocations.SingleOrDefault(row => row.RequestSkuId == sku && row.PoolId == input.PoolId);
        if (planned is null || input.Packages > planned.Packages) throw B.Changed("procurement_stock_receipt_inbound_target_changed");
    }
    internal async Task RequirePlannedReceiptCapacityAsync(IClientSessionHandle tx, BsonDocument source,
        BsonDocument inspection, ProcurementStockReceiptInput input, CancellationToken ct) {
        await RequirePlannedReceiptMembershipAsync(tx, source, inspection, input, ct);
        var binding = await InboundRecords.ApprovedAsync(tx, source, ct); if (binding is null) return;
        var sku = inspection["input"]["lines"][input.InspectionLineNumber - 1]["requestSkuId"].AsInt64;
        var planned = I.Selection(PurchaseOrderBson.Input(source["proposal"]["input"]), binding.Value.Selection["allocations"])
            .Allocations.Single(row => row.RequestSkuId == sku && row.PoolId == input.PoolId);
        var rows = await Rows(B.Receipts).Find(tx, new BsonDocument("orderId", input.OrderId)).Sort(new BsonDocument("_id", 1)).Limit(4097).ToListAsync(ct);
        if (rows.Count > 4096 || database.Client is not MongoClient client) throw B.Broken();
        long used = 0;
        var receiptOwner = new ProcurementStockReceiptRecords(database, new(client), clock, catalog, this, InboundSources);
        foreach (var raw in rows) {
            // Complete actual custody/disposition/private pin/audit/receipt/stock proof,
            // not a sum of raw quantities. This reader's membership check never recurses
            // into this capacity traversal. Existing final pool CAS serializes new legs.
            var actual = await receiptOwner.ReadCoreAsync(tx, raw["_id"].AsInt64, ct);
            if (!actual["source"].Equals(source)) throw B.Broken();
            var line = actual["inspection"]["input"]["lines"][actual["inspectionLineNumber"].AsInt32 - 1];
            if (actual["poolId"] == input.PoolId && line["requestSkuId"] == sku) used = checked(used + actual["input"]["packages"].AsInt32);
        }
        if (checked(used + input.Packages) > planned.Packages) throw B.Changed("procurement_stock_receipt_inbound_capacity_exceeded");
    }
}
