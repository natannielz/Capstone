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
    private readonly bool sealedOrigin;
    // Full rechecks performed (tests assert the per-operation memo); sealed proofs recheck once per owning operation.
    internal static long FullRechecks;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IClientSessionHandle, HashSet<(long Number, long Receipt)>> rechecked = new();
    private HeldReceiptOriginProof(IMongoDatabase database, IClientSessionHandle transaction, ProcurementStockReceiptRecords records,
        PurchaseOrderSourceReadCapture capture, IClock clock, BsonDocument receipt, bool sealedOrigin = false)
    {
        this.sealedOrigin = sealedOrigin;
        this.database = database; this.transaction = transaction; this.records = records;
        this.capture = capture; this.clock = clock; capturedAt = clock.UtcNow;
        transactionNumber = transaction.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        this.receipt = receipt.DeepClone().AsBsonDocument;
    }
    private const string SealKind = UnitToko.Platform.Mongo.Migrations.ProvenanceSealSchemas.ReceiptOriginKind;
    internal long ReceiptId => receipt["_id"].AsInt64;
    internal BsonDocument Receipt => receipt.DeepClone().AsBsonDocument;
    internal static async Task<HeldReceiptOriginProof> CaptureAsync(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
        CatalogAuthoringService catalog, PurchaseOrderService orders, StockSourceSetupService sources,
        IClientSessionHandle tx, long receiptId, CancellationToken ct)
    {
        new InventoryRepository(database).RequireSession(tx);
        // A present seal is authoritative: it is verified (MAC, live receipt hash, exact membership) and never
        // bypassed by a fresh rebuild. An absent seal keeps the full proof below, which then seals.
        var ring = UnitToko.Platform.Integrity.ProvenanceSealRing.Current;
        var sealRows = database.GetCollection<BsonDocument>(UnitToko.Platform.Mongo.Migrations.ProvenanceSealSchemas.Collection);
        if (ring is not null) {
            var existing = await sealRows.Find(tx, new BsonDocument("_id", receiptId)).SingleOrDefaultAsync(ct);
            if (existing is not null) {
                var row = await database.GetCollection<BsonDocument>(ProcurementStockReceiptBson.Receipts).Find(tx, new BsonDocument("_id", receiptId)).SingleOrDefaultAsync(ct)
                    ?? throw HeldReceiptMovementHistory.Unavailable();
                IReadOnlyList<BsonDocument> verified;
                try { verified = await new UnitToko.Platform.Integrity.SealVerifier(database, ring).VerifyAsync(tx, existing, SealKind, receiptId, row, ct); }
                catch (UnitToko.Platform.Integrity.SealUnavailableException) { throw HeldReceiptMovementHistory.Unavailable(); }
                var storageOwner = sources.CaptureHistory(new PurchaseOrderSourceReadCapture());
                var sealedCapture = PurchaseOrderSourceReadCapture.FromSealed(database, tx, verified, storageOwner);
                // Membership comes from the verified seal; custody and private inspection evidence stay fresh.
                var sealedRecords = new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, storageOwner, new ProcurementQualityProofGraph(tx), sealedCapture);
                await sealedRecords.RequireOriginInspectionAsync(tx, row, ct);
                return new HeldReceiptOriginProof(database, tx, sealedRecords, sealedCapture, clock, row, sealedOrigin: true);
            }
        }
        // Keep the existing transaction-local immutable ancestor captures. Reuse
        // still re-reads actual PO facts, custody/inspection heads and every private
        // pin; this graph has no origin shortcut and grants no current stock.
        var graph = new ProcurementQualityProofGraph(tx);
        var capture = new PurchaseOrderSourceReadCapture();
        var records = new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources.CaptureHistory(capture), graph, capture);
        var receipt = await records.ReadOriginCoreAsync(tx, receiptId, ct);
        capture.Seal();
        var proof = new HeldReceiptOriginProof(database, tx, records, capture, clock, receipt);
        // Only the owning unit-of-work operation seals; caller-supplied sessions never write seals.
        if (ring is not null && MongoUnitOfWork.OwnsOperation(tx) && capture.SealedScopes() is { } scopes)
            await sealRows.InsertOneAsync(tx, UnitToko.Platform.Integrity.ProvenanceSeal.Create(ring, SealKind, receiptId,
                UnitToko.Platform.Integrity.ProvenanceSeal.RowHash(receipt), UnitToko.Platform.Integrity.SealVerifier.Membership(scopes, capture.SealedCounts()), [], clock.UtcNow), cancellationToken: ct);
        return proof;
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
        var memo = sealedOrigin && MongoUnitOfWork.OwnsOperation(tx) ? (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber, ReceiptId) : ((long, long)?)null;
        if (memo is { } key && rechecked.TryGetValue(tx, out var done)) lock (done) if (done.Contains(key)) return;
        Interlocked.Increment(ref FullRechecks);
        await capture.RequireAsync(database, tx, ct);
        // Only immutable semantic interpretation is retained. Actual complete owner
        // rows/membership, mutable pools and custody/private evidence are fresh.
        await records.RequireOriginInspectionAsync(tx, receipt, ct);
        Require(owner, tx);
        if (memo is { } passed) { var table = rechecked.GetValue(tx, _ => []); lock (table) table.Add(passed); }
    }
}
