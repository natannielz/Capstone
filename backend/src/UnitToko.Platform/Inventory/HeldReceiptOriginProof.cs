using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Inventory;

/// <summary>Full immutable PO/custody/private-inspection/receipt/lot genesis. Never current stock authority.</summary>
internal sealed class HeldReceiptOriginProof
{
    private readonly IMongoDatabase database;
    private readonly IClientSessionHandle transaction;
    private readonly long transactionNumber;
    private readonly ProcurementStockReceiptRecords records;
    private readonly PurchaseOrderSourceReadCapture capture;
    private readonly IClock clock;
    private readonly DateTimeOffset capturedAt;
    private readonly BsonDocument receipt;
    private HeldReceiptOriginProof(IMongoDatabase database, IClientSessionHandle transaction, ProcurementStockReceiptRecords records,
        PurchaseOrderSourceReadCapture capture, IClock clock, BsonDocument receipt)
    {
        this.database = database; this.transaction = transaction; this.records = records;
        this.capture = capture; this.clock = clock; capturedAt = clock.UtcNow;
        transactionNumber = transaction.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        this.receipt = receipt.DeepClone().AsBsonDocument;
    }
    internal long ReceiptId => receipt["_id"].AsInt64;
    internal BsonDocument Receipt => receipt.DeepClone().AsBsonDocument;
    internal static async Task<HeldReceiptOriginProof> CaptureAsync(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
        CatalogAuthoringService catalog, PurchaseOrderService orders, StockSourceSetupService sources,
        IClientSessionHandle tx, long receiptId, CancellationToken ct)
    {
        new InventoryRepository(database).RequireSession(tx);
        // Keep the existing transaction-local immutable ancestor captures. Reuse
        // still re-reads actual PO facts, custody/inspection heads and every private
        // pin; this graph has no origin shortcut and grants no current stock.
        var graph = new ProcurementQualityProofGraph(tx);
        var capture = new PurchaseOrderSourceReadCapture();
        var records = new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources.CaptureHistory(capture), graph, capture);
        var receipt = await records.ReadOriginCoreAsync(tx, receiptId, ct);
        capture.Seal();
        return new(database, tx, records, capture, clock, receipt);
    }
    internal void Require(IMongoDatabase owner, IClientSessionHandle tx)
    {
        new InventoryRepository(owner).RequireSession(tx);
        if (!ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
            !ReferenceEquals(tx, transaction) || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber ||
            clock.UtcNow < capturedAt)
            throw HeldReceiptMovementHistory.Unavailable();
    }
    internal async Task RequireAsync(IMongoDatabase owner, IClientSessionHandle tx, CancellationToken ct)
    {
        Require(owner, tx);
        await capture.RequireAsync(database, tx, ct);
        // Only immutable semantic interpretation is retained. Actual complete owner
        // rows/membership, mutable pools and custody/private evidence are fresh.
        await records.RequireOriginInspectionAsync(tx, receipt, ct);
        Require(owner, tx);
    }
}
