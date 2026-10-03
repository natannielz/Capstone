using MongoDB.Bson;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using P = UnitToko.Platform.Procurement.PurchaseOrderBson;
using Q = UnitToko.Platform.Procurement.ProcurementQuarantineDispositionBson;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementStockReceiptBson
{
    internal const string Receipts = "procurementStockReceipts", Operation = "procurement.stockreceipt.receive", State = "held_pending_valuation_and_release";
    internal static BsonDocument Input(ProcurementStockReceiptInput input) { input = ProcurementStockReceiptRules.Copy(input); return new() {
        { "location", R.Location(input.Location) }, { "orderId", input.OrderId }, { "orderVersion", input.OrderVersion }, { "orderApprovalId", input.OrderApprovalId },
        { "inspectionId", input.InspectionId }, { "inspectionLineNumber", input.InspectionLineNumber }, { "packages", input.Packages }, { "poolId", input.PoolId }, { "poolRevision", input.PoolRevision },
        { "receiptReference", input.ReceiptReference }, { "lotReference", input.LotReference }, { "expiresOn", input.ExpiresOn is { } date ? P.Date(date) : BsonNull.Value }, { "reason", input.Reason } }; }
    internal static ProcurementStockReceiptInput Input(BsonValue row) => ProcurementStockReceiptRules.Copy(new ProcurementStockReceiptInput(R.Location(row["location"]), row["orderId"].AsInt64,
        row["orderVersion"].AsInt64, row["orderApprovalId"].AsInt64, row["inspectionId"].AsInt64, row["inspectionLineNumber"].AsInt32, row["packages"].AsInt32, row["poolId"].AsInt64,
        row["poolRevision"].AsInt64, row["receiptReference"].AsString, row["lotReference"].AsString, row["expiresOn"].IsBsonNull ? null : P.Date(row["expiresOn"]), row["reason"].AsString));
    internal static string Hash(BsonDocument row) => P.Hash(row);
    internal static object Body(ProcurementStockReceiptInput input, string binding) => new { input = ProcurementStockReceiptRules.Copy(input), binding };
    internal static string Binding(long actor, long grantVersion, ProcurementStockReceiptInput input, BsonDocument source, BsonDocument inspection, BsonDocument poolSource, BsonDocument capacity) {
        var policy = poolSource.DeepClone().AsBsonDocument; policy.Remove("poolFence");
        return Hash(new() { { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "input", Input(input) }, { "source", source }, { "inspection", inspection }, { "poolSource", policy }, { "capacity", capacity } });
    }
    internal static BsonDocument Result(BsonDocument row) => new() { { "receiptId", row["_id"] }, { "revision", 1L }, { "lotId", row["lotId"] }, { "balanceId", row["balanceId"] }, { "movementId", row["movementId"] }, { "state", State } };
    internal static BsonDocument SourceView(BsonDocument source, BsonDocument inspection, int line) {
        var selected = SupplierClaimRecords.InspectionView(inspection, line); selected.Add("lineNumber", line);
        return new() { { "order", PurchaseOrderService.FundingOrderView(source) }, { "inspection", selected } };
    }
    internal static BsonDocument View(BsonDocument row) => new() { { "id", row["_id"] }, { "revision", 1L }, { "input", row["input"] },
        { "source", SourceView(row["source"].AsBsonDocument, row["inspection"].AsBsonDocument, row["inspectionLineNumber"].AsInt32) }, { "pool", StockSourceSetupService.HeldReceiptPoolView(row["poolSource"].AsBsonDocument) },
        { "before", row["before"] }, { "after", row["after"] }, { "lotId", row["lotId"] }, { "balanceId", row["balanceId"] }, { "movementId", row["movementId"] },
        { "onHand", row["input"]["packages"].ToInt64() }, { "held", row["input"]["packages"].ToInt64() }, { "reserved", 0L }, { "expired", 0L }, { "available", 0L },
        { "valuationState", "pending" }, { "valuation", BsonNull.Value }, { "state", State }, { "recordedBy", row["actorId"] }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Allocation(BsonDocument row) {
        var spec = row["disposition"]; var result = new BsonDocument { { "_id", spec["allocationId"] }, { "schemaVersion", 2 }, { "kind", "held_receipt" }, { "basis", "physical_received" },
            { "receiptId", row["_id"] }, { "receiptHash", row["snapshotHash"] }, { "headId", spec["headId"] }, { "inspectionId", row["inspectionId"] }, { "inspectionLineNumber", row["inspectionLineNumber"] },
            { "inspectionHash", row["inspection"]["snapshotHash"] }, { "location", row["location"] }, { "packages", row["input"]["packages"] }, { "sequence", row["after"]["version"] },
            { "before", row["before"] }, { "after", row["after"] }, { "previousAllocationId", spec["previousAllocationId"] }, { "previousHash", spec["previousHash"] }, { "recordedAt", row["recordedAt"] } };
        result.Add("snapshotHash", Hash(result)); return result;
    }
    internal static string EffectKey(BsonDocument row) => "procurement.stockreceipt.command:" + row["auditId"].AsInt64;
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "actorId", row["actorId"] },
        { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] }, { "requestKey", row["requestKey"] },
        { "location", row["location"] }, { "orderId", row["orderId"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static PersistenceConflictException Broken() => new("procurement_stock_receipt_integrity_unavailable", "The complete held receipt and original inventory history is unavailable");
    internal static PersistenceConflictException Changed(string code = "procurement_stock_receipt_changed") => new(code, "Reload the current inspection capacity and reviewed stock source");
}
