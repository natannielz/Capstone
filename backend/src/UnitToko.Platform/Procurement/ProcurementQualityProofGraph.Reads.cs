using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using I = UnitToko.Platform.Procurement.ProcurementInspectionBson;
using C = UnitToko.Platform.Procurement.ProcurementReceivingBson;
using Q = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;
using R = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;

namespace UnitToko.Platform.Procurement;

// Only successful owning readers can populate these proofs. Their immutable
// ancestors belong to this database and this transaction attempt, while custody,
// inspection heads, held stock and private pins are checked again on every reuse.
// No current policy, actor grant, PDF read window, claim or disposition is cached.
internal sealed partial class ProcurementQualityProofGraph
{
    private string? proofDatabase;
    private readonly Dictionary<long, (ProcurementInspectionHistory History, DateTimeOffset At)> completedInspections = [];
    private readonly Dictionary<long, (BsonDocument Receipt, BsonDocument Pool, DateTimeOffset At)> completedReceipts = [];
    private readonly Dictionary<long, PurchaseOrderHistoricalSourceProof> completedFunding = [];
    internal HeldReceiptOriginProof? CurrentMovementOrigin(IMongoDatabase database, IClientSessionHandle current, long receiptId) {
        RequireDatabase(database, current, receiptId);
        if (!currentMovementOrigins.TryGetValue(receiptId, out var origin)) return null;
        origin.Require(database, current); return origin;
    }
    internal void RememberCurrentMovementOrigin(IMongoDatabase database, IClientSessionHandle current, BsonDocument receipt, HeldReceiptOriginProof origin) {
        RequireDatabase(database, current, receipt["_id"].AsInt64); origin.Require(database, current);
        if (origin.Receipt != receipt || currentMovementOrigins.TryGetValue(origin.ReceiptId, out var existing) && !ReferenceEquals(existing, origin)) throw Q.Broken();
        currentMovementOrigins[origin.ReceiptId] = origin;
    }

    // A successful current owner may supply only its completed immutable ancestors
    // to an origin reader. No mutable receipt/pool/current movement or active cycle
    // state crosses this boundary; existing reuse still refreshes every owner/pin.
    internal ProcurementQualityProofGraph CopyOriginAncestors(IMongoDatabase database, IClock clock,
        IClientSessionHandle current, HeldReceiptOriginProof retainedOrigin) {
        RequireDatabase(database, current, retainedOrigin.ReceiptId); retainedOrigin.Require(database, current);
        if (active.Count != 0 || originRead is not null || clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero)
            throw Q.Broken();
        var result = new ProcurementQualityProofGraph(current, retainedOrigin);
        result.RequireDatabase(database, current, retainedOrigin.ReceiptId);
        foreach (var (id, proof) in completedFunding) {
            if (proof.Source["order"]["_id"] != id || !result.completedFunding.TryAdd(id, proof)) throw Q.Broken();
        }
        foreach (var (id, completed) in completedInspections) {
            if (clock.UtcNow < completed.At || !completed.History.Submissions.Any(row => row["_id"] == id) ||
                !result.completedInspections.TryAdd(id, (Clone(completed.History), completed.At))) throw Q.Broken();
        }
        RequireDatabase(database, current, retainedOrigin.ReceiptId); retainedOrigin.Require(database, current);
        return result;
    }

    internal void AttachFundingProof(IClientSessionHandle current, PurchaseOrderHistoricalSourceProof proof) {
        Require(current);
        var id = proof.Source["order"]["_id"].AsInt64;
        if (!completedFunding.TryAdd(id, proof) && !completedFunding[id].SameProof(proof)) throw Q.Broken();
    }
    internal async Task<BsonDocument> FundingAsync(IMongoDatabase database, PurchaseOrderService orders, IClientSessionHandle current,
        long orderId, long orderVersion, long approvalId, CancellationToken ct) {
        RequireDatabase(database, current, orderId);
        if (orderVersion <= 0 || approvalId <= 0) throw Q.Broken();
        if (!completedFunding.TryGetValue(orderId, out var proof)) {
            proof = await PurchaseOrderHistoricalSourceProof.ReadAsync(orders, database, current, orderId, orderVersion, approvalId, ct);
            AttachFundingProof(current, proof);
        }
        var source = proof.Source;
        if (source["order"]["_id"] != orderId || source["order"]["version"] != orderVersion || source["order"]["approvalId"] != approvalId) throw Q.Broken();
        // Genuine semantic ancestry only: every reuse checks its exact owner,
        // database/client/transaction, complete captured rows/count predicates and
        // fresh inbound/latest source membership. It never caches current authority.
        await proof.RequireAsync(orders, database, current, source, ct);
        RequireDatabase(database, current, orderId); ct.ThrowIfCancellationRequested(); return source;
    }
    private async Task RequireFundingAsync(IMongoDatabase database, PurchaseOrderService orders,
        IClientSessionHandle current, BsonDocument source, CancellationToken ct) {
        RequireDatabase(database, current, source["order"]["_id"].AsInt64);
        if (!completedFunding.TryGetValue(source["order"]["_id"].AsInt64, out var proof)) throw Q.Broken();
        await proof.RequireAsync(orders, database, current, source, ct);
    }

    private void RequireDatabase(IMongoDatabase database, IClientSessionHandle current, long id) {
        Require(current);
        if (id <= 0 || !ReferenceEquals(current.Client, database.Client)) throw Q.Broken();
        var name = database.DatabaseNamespace.DatabaseName;
        if (proofDatabase is not null && proofDatabase != name) throw Q.Broken();
        proofDatabase ??= name;
    }
    private static BsonDocument Clone(BsonDocument row) => row.DeepClone().AsBsonDocument;
    private static ProcurementInspectionHistory Clone(ProcurementInspectionHistory history) => new(
        Clone(history.Source), Clone(history.CustodyHead), history.Head is null ? null : Clone(history.Head),
        Clone(history.Capacity), history.Drafts.Select(Clone).ToArray(), history.Submissions.Select(Clone).ToArray());
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw Q.Broken(); }

    internal async Task<ProcurementInspectionHistory> InspectionAsync(IMongoDatabase database, MongoUnitOfWork unit,
        IClock clock, CatalogAuthoringService catalog, PurchaseOrderService orders, IClientSessionHandle current,
        long inspectionId, CancellationToken ct) {
        RequireDatabase(database, current, inspectionId);
        if (completedInspections.TryGetValue(inspectionId, out var completed)) {
            if (clock.UtcNow < completed.At) throw Q.Broken();
            var history = completed.History;
            if (!history.Submissions.Any(row => row["_id"] == inspectionId)) throw Q.Broken();
            await RequireFundingAsync(database, orders, current, history.Source["notice"]["source"].AsBsonDocument, ct);
            // This reads the complete actual submission membership and compares
            // every byte, including this inspection, after funding awaits. The
            // original schema-valid owner row needs no duplicate ID/schema read.
            await RequireInspectionOwnersAsync(database, unit, clock, catalog, orders, current, history, ct);
            RequireDatabase(database, current, inspectionId);
            return Clone(history);
        }
        var inspection = await TransactionReadMemo.RowAsync(database.GetCollection<BsonDocument>(I.Submissions), current, "inspection-schema:" + inspectionId, () => new BsonDocument {
            { "_id", inspectionId }, { "$and", new BsonArray { ProcurementInspectionSchemas.Validator(I.Submissions) } }
        }, ct) ?? throw Q.Broken();
        var verified = await new SupplierDocumentRecords(database, unit, clock, catalog, orders, this)
            .InspectionAsync(current, inspectionId, ct);
        Equal(inspection, verified.Submissions.SingleOrDefault(row => row["_id"] == inspectionId) ?? throw Q.Broken());
        await RequireFundingAsync(database, orders, current, verified.Source["notice"]["source"].AsBsonDocument, ct);
        RequireDatabase(database, current, inspectionId);
        completedInspections.Add(inspectionId, (Clone(verified), clock.UtcNow));
        return Clone(verified);
    }

    private static async Task RequireInspectionOwnersAsync(IMongoDatabase database, MongoUnitOfWork unit,
        IClock clock, CatalogAuthoringService catalog, PurchaseOrderService orders, IClientSessionHandle current,
        ProcurementInspectionHistory history, CancellationToken ct) {
        var notice = history.Source["notice"].AsBsonDocument;
        var intake = history.Source["intake"].AsBsonDocument;
        foreach (var (collection, expected) in new[] { (C.Notices, notice), (C.Intakes, intake), (C.Heads, history.CustodyHead) })
            Equal(await database.GetCollection<BsonDocument>(collection).Find(current, new BsonDocument("_id", expected["_id"]))
                .SingleOrDefaultAsync(ct) ?? throw Q.Broken(), expected);
        var head = await database.GetCollection<BsonDocument>(I.Heads).Find(current, new BsonDocument("_id", intake["_id"]))
            .SingleOrDefaultAsync(ct);
        if ((head is null) != (history.Head is null)) throw Q.Broken();
        if (head is not null) Equal(head, history.Head!);
        foreach (var (collection, frozen, sort) in new[] {
            (I.Drafts, history.Drafts, new BsonDocument("_id", 1)),
            (I.Submissions, history.Submissions, new BsonDocument("sequence", 1)) }) {
            var actual = await database.GetCollection<BsonDocument>(collection).Find(current, new BsonDocument("intakeId", intake["_id"]))
                .Sort(sort).ToListAsync(ct);
            if (actual.Count != frozen.Count) throw Q.Broken();
            for (var index = 0; index < actual.Count; index++) Equal(actual[index], frozen[index]);
        }
        // Re-authenticate each genuine private inspection pin and its admission,
        // completion, validation and lifecycle history; a retained proof cannot
        // launder a deleted pin or changed private owner within the transaction.
        var records = new ProcurementInspectionRecords(database, unit, clock, catalog, orders);
        foreach (var row in history.Submissions) await records.RequirePinAsync(current, row, ct);
    }

    internal async Task<BsonDocument> HeldReceiptAsync(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
        CatalogAuthoringService catalog, PurchaseOrderService orders, StockSourceSetupService sources,
        IClientSessionHandle current, long receiptId, CancellationToken ct) {
        RequireDatabase(database, current, receiptId);
        if (originProof is not null && originProof.ReceiptId == receiptId) {
            if (originRead is null) await originProof.RequireAsync(database, current, ct);
            else RequireOriginRead(database, current, originProof);
            return originProof.Receipt;
        }
        var receipt = await TransactionReadMemo.RowAsync(database.GetCollection<BsonDocument>(R.Receipts), current, "receipt-schema:" + receiptId, () => new BsonDocument {
            { "_id", receiptId }, { "$and", new BsonArray { ProcurementStockReceiptSchemas.Validator(R.Receipts) } }
        }, ct) ?? throw Q.Broken();
        if (receipt["snapshotHash"] != R.Hash(receipt)) throw Q.Broken();
        if (completedReceipts.TryGetValue(receiptId, out var completed)) {
            if (clock.UtcNow < completed.At) throw Q.Broken();
            Equal(receipt, completed.Receipt);
            var inspection = await InspectionAsync(database, unit, clock, catalog, orders, current, receipt["inspectionId"].AsInt64, ct);
            Equal(inspection.Submissions.SingleOrDefault(row => row["_id"] == receipt["inspectionId"]) ?? throw Q.Broken(), receipt["inspection"]);
            await RequireHeldOwnersAsync(database, unit, clock, current, receipt, completed.Pool, ct);
            RequireDatabase(database, current, receiptId);
            return Clone(receipt);
        }
        var verified = await new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources, this)
            .ReadCoreAsync(current, receiptId, ct);
        Equal(receipt, verified);
        var pool = await database.GetCollection<BsonDocument>("inventoryPools").Find(current, new BsonDocument("_id", receipt["poolId"]))
            .SingleOrDefaultAsync(ct) ?? throw Q.Broken();
        InventoryGraph.RequirePool(pool);
        await RequireHeldOwnersAsync(database, unit, clock, current, receipt, pool, ct);
        RequireDatabase(database, current, receiptId);
        completedReceipts.Add(receiptId, (Clone(verified), Clone(pool), clock.UtcNow));
        return Clone(verified);
    }
    private async Task RequireHeldOwnersAsync(IMongoDatabase database, MongoUnitOfWork unit, IClock clock, IClientSessionHandle current,
        BsonDocument receipt, BsonDocument expectedPool, CancellationToken ct) {
        var pool = await database.GetCollection<BsonDocument>("inventoryPools").Find(current, new BsonDocument("_id", receipt["poolId"]))
            .SingleOrDefaultAsync(ct) ?? throw Q.Broken();
        InventoryGraph.RequirePool(pool); Equal(pool, expectedPool);
        // Completed semantic ancestors do not cache current stock or grant release authority.
        // Reconstruct all actual movement owners on every transaction-local reuse.
        try {
            _ = await ProcurementStockReceiptRecords.RequireMovementsAsync(database, unit, clock, current, receipt, ct, this);
        } catch (PersistenceConflictException error) when (error.Code == "procurement_stock_receipt_integrity_unavailable") {
            throw Q.Broken();
        }
    }
}
