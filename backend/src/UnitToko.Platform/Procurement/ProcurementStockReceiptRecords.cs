using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using Q = UnitToko.Platform.Procurement.ProcurementQuarantineDispositionBson;

namespace UnitToko.Platform.Procurement;

internal sealed class ProcurementStockReceiptRecords(IMongoDatabase database, MongoUnitOfWork unit, IClock clock, CatalogAuthoringService catalog,
    PurchaseOrderService orders, StockSourceSetupService sources)
{
    private IMongoCollection<BsonDocument> Rows(string table) => database.GetCollection<BsonDocument>(table);
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal async Task<ProcurementInspectionHistory> InspectionAsync(IClientSessionHandle tx, long inspectionId, CancellationToken ct) =>
        await new SupplierDocumentRecords(database, unit, clock, catalog, orders).InspectionAsync(tx, inspectionId, ct);
    internal static void Review(ProcurementStockReceiptInput input, BsonDocument source, BsonDocument inspection) {
        Equal(source["order"]["_id"], input.OrderId); Equal(source["order"]["version"], input.OrderVersion); Equal(source["order"]["approvalId"], input.OrderApprovalId);
        Equal(source["order"]["location"], R.Location(input.Location)); Equal(inspection["_id"], input.InspectionId); Equal(inspection["location"], R.Location(input.Location));
        if (input.InspectionLineNumber > inspection["input"]["lines"].AsBsonArray.Count) throw B.Changed();
        var line = inspection["input"]["lines"][input.InspectionLineNumber - 1];
        if (input.Packages > line["receivedPackages"].AsInt32 || !line["lotCode"].IsBsonNull && line["lotCode"] != input.LotReference ||
            !line["expiryDate"].IsBsonNull && (input.ExpiresOn is null || line["expiryDate"] != PurchaseOrderBson.Date(input.ExpiresOn.Value))) throw B.Changed();
    }
    // No disposition-head recursion: its owner reconstructs allocations against this complete receipt proof.
    internal async Task<BsonDocument> ReadAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows(B.Receipts).Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { ProcurementStockReceiptSchemas.Validator(B.Receipts) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(row["snapshotHash"], B.Hash(row)); var input = B.Input(row["input"]); Equal(row["input"], B.Input(input)); Equal(row["receiptId"], id); Equal(row["revision"], 1L);
        foreach (var field in new[] { "location", "orderId", "inspectionId", "inspectionLineNumber", "poolId", "receiptReference", "reason" }) Equal(row[field], row["input"][field]);
        var source = await orders.FundingSourceAsync(database, tx, input.OrderId, input.OrderVersion, input.OrderApprovalId, ct); Equal(row["source"], source); Equal(row["supplierId"], source["proposal"]["input"]["supplierId"]);
        var history = await InspectionAsync(tx, input.InspectionId, ct); Equal(history.Source["notice"]["source"], source);
        var inspection = history.Submissions.SingleOrDefault(value => value["_id"] == input.InspectionId) ?? throw B.Broken(); Equal(row["inspection"], inspection); Review(input, source, inspection);
        var at = new DateTimeOffset(row["recordedAt"].ToUniversalTime()); if (at > clock.UtcNow || inspection["recordedAt"].CompareTo(row["recordedAt"]) > 0) throw B.Broken();
        await sources.RequireHeldReceiptHistoryAsync(tx, row["poolSource"].AsBsonDocument, at, ct);
        Equal(row["poolSource"]["poolId"], input.PoolId); Equal(row["poolSource"]["requestedLocation"], row["location"]); Equal(row["poolSource"]["configuration"]["source"]["poolRevision"], input.PoolRevision);
        var before = row["before"].AsBsonDocument; Q.RequireCapacity(before, SupplierClaimBasis.PhysicalReceived, input.InspectionId, input.InspectionLineNumber, inspection["snapshotHash"].AsString, inspection["input"]["lines"][input.InspectionLineNumber - 1]["receivedPackages"].AsInt32);
        Equal(row["after"], Q.After(before, row["disposition"]["headId"].AsInt64, input.Packages));
        if ((before["version"] == 0L) != row["disposition"]["previousAllocationId"].IsBsonNull || row["disposition"]["previousAllocationId"].IsBsonNull != row["disposition"]["previousHash"].IsBsonNull) throw B.Broken();
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, input, source, inspection, row["poolSource"].AsBsonDocument, before));
        if (row["people"].AsBsonArray.Count != 1 || R.Person(row["people"][0]) is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 } person || person.UserId != row["actorId"].AsInt64) throw B.Broken();
        await InventoryHeldReceiptWriter.RequireHistoryAsync(database, tx, row, ct); await EffectsAsync(tx, row, ct); return row;
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.Body(B.Input(row["input"]), row["binding"].AsString), row["reason"].AsString));
        Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", B.Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (receipt.ElementCount != 8 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || receipt["createdAt"].CompareTo(row["recordedAt"]) > 0) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.EffectKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var field in new[] { "actorId", "requestKey", "location", "orderId" }) Equal(effect[field], row[field]); Equal(effect["kind"], B.Operation); Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string key, CancellationToken ct) {
        var row = await Rows(B.Receipts).Find(tx, new BsonDocument { { "actorId", actor }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) return await ReadAsync(tx, row["_id"].AsInt64, ct);
        foreach (var (table, field, keyField) in new[] { ("auditEvents", "kind", "requestKey"), ("outboxEvents", "kind", "requestKey"), ("idempotencyRecords", "operation", "key") })
            if (await Rows(table).Find(tx, new BsonDocument { { "actorId", actor }, { field, B.Operation }, { keyField, key } }).AnyAsync(ct)) throw B.Broken();
        return null;
    }
    internal async Task OrphansAsync(IClientSessionHandle tx, CancellationToken ct) {
        foreach (var (table, filter, local) in new[] {
            ("auditEvents", new BsonDocument("kind", B.Operation), "result.receiptId"), ("outboxEvents", new BsonDocument("kind", B.Operation), "payload.receiptId"),
            ("idempotencyRecords", new BsonDocument("operation", B.Operation), "result.receiptId"), ("lots", new BsonDocument("origin", "procurementReceipt"), "sourceId"),
            ("movements", new BsonDocument("source.namespace", B.Operation), "source.documentId") }) {
            var orphan = await Rows(table).Aggregate<BsonDocument>(tx, new BsonDocument[] { new("$match", filter), new("$lookup", new BsonDocument { { "from", B.Receipts }, { "localField", local }, { "foreignField", "_id" }, { "as", "owner" } }),
                new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct); if (orphan.Count != 0) throw B.Broken();
        }
    }
}
