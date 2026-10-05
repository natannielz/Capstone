using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementStockSourceEvent(string Kind, long Id, long PoolId, BsonDocument Record,
    BsonDocument Audit, BsonValue Time, HeldReceiptMovementState? Movement)
{
    internal string Hash => CatalogRepository.Digest(new BsonDocument { { "kind", Kind }, { "record", Record } });
}

/// <summary>Existing semantic owners authenticate these events. No injected descriptive
/// snapshot or current alias is accepted as Procurement pool membership.</summary>
internal sealed class ProcurementStockSourceEvents
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IClock clock;
    private readonly ProcurementStockReceiptRecords receipts;
    private readonly ProcurementStockReceiptService receiptOwner;
    private readonly ProcurementQuarantineDisposition dispositions;
    private readonly ProcurementQualityAcceptanceRecords quality;
    private readonly ProcurementQualityAcceptanceCapacityReader qualityCapacity;
    internal ProcurementStockSourceEvents(IMongoDatabase database, IdAllocator ids, IClock clock)
    {
        this.database = database; this.clock = clock;
        unit = new(database.Client as MongoClient ?? throw ProcurementStockSourceRecords.Unavailable());
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var requests = new ProcurementRequestService(database, unit, ids, clock, catalog);
        var suppliers = new SupplierService(database, unit, ids, clock, catalog);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, requests, suppliers);
        var sources = new StockSourceSetupService(database, unit, ids, clock);
        receiptOwner = new(database, unit, ids, clock, catalog, orders,
            new ProcurementReceivingService(database, unit, ids, clock, catalog, orders, requests), sources);
        receipts = new(database, unit, clock, catalog, orders, sources);
        dispositions = new(database, new SupplierClaimRecords(database, unit, clock, catalog, orders));
        quality = new(database, unit, clock, catalog, orders, sources);
        qualityCapacity = new(database, sources);
    }
    internal async Task<ProcurementStockSourceEvent> ReadAsync(IClientSessionHandle tx, string kind, long id,
        bool requireSetupReceipt, CancellationToken ct)
    {
        // Reuse the exact existing storage admission, including current supplier-document
        // schema unions, private inspection owners and allowed disposition index branches.
        await receiptOwner.RequireStockSourcePredecessorStorageAsync(ct);
        return await ReadCoreAsync(tx, kind, id, requireSetupReceipt, ct);
    }
    internal async Task<ProcurementStockSourceEvent> ReadRetainedForMigrationAsync(IClientSessionHandle tx, string kind, long id,
        bool requireSetupReceipt, MigrationLease lease, CancellationToken ct)
    {
        await RequireRetainedMigrationAsync(lease, ct);
        return await ReadCoreAsync(tx, kind, id, requireSetupReceipt, ct);
    }
    private async Task<ProcurementStockSourceEvent> ReadCoreAsync(IClientSessionHandle tx, string kind, long id,
        bool requireSetupReceipt, CancellationToken ct)
    {
        if (kind == "setup") {
            var source = await new OrderStockSourceRecords(database).SetupAsync(tx, id, requireSetupReceipt, ct);
            if (source.PoolIds.Length != 1) throw ProcurementStockSourceRecords.Unavailable();
            return new(kind, id, source.PoolIds[0], source.Record, source.Audit, source.Time, null);
        }
        if (kind != "heldReceipt") throw ProcurementStockSourceRecords.Unavailable();
        // ReadAsync requires the completed owning command result, audit/outbox/person,
        // full PO/request/custody/inspection/private pin and historical pool policy.
        var row = await receipts.ReadAsync(tx, id, ct);
        var inspection = row["inspection"];
        var line = inspection["input"]["lines"][row["inspectionLineNumber"].AsInt32 - 1];
        var membership = new BsonDocument("input.lines", new BsonDocument("$elemMatch", new BsonDocument {
            { "basis", "physical_received" }, { "inspectionId", row["inspectionId"] }, { "inspectionLineNumber", row["inspectionLineNumber"] } }));
        if (await database.GetCollection<BsonDocument>(SupplierClaimBson.Requests).CountDocumentsAsync(tx, membership, cancellationToken: ct) >
            ProcurementStockSourceRecords.MaximumRows ||
            await database.GetCollection<BsonDocument>(ProcurementStockReceiptBson.Receipts).CountDocumentsAsync(tx,
                new BsonDocument { { "inspectionId", row["inspectionId"] }, { "inspectionLineNumber", row["inspectionLineNumber"] } }, cancellationToken: ct) >
                ProcurementStockSourceRecords.MaximumRows) throw ProcurementStockSourceRecords.Unavailable();
        _ = await dispositions.ReadAsync(tx, SupplierClaimBasis.PhysicalReceived, row["inspectionId"].AsInt64,
            row["inspectionLineNumber"].AsInt32, inspection["snapshotHash"].AsString, line["receivedPackages"].AsInt32,
            row["location"].AsBsonDocument, row["orderId"].AsInt64, row["supplierId"].AsInt64, ct);
        await dispositions.OrphansAsync(tx, ct);
        // Rejected review is still a finding, not removal of physical custody. Authenticate
        // existing quality lineage without interpreting its accepted capacity as release.
        var raw = await database.GetCollection<BsonDocument>(ProcurementQualityAcceptanceBson.Proposals)
            .Find(tx, new BsonDocument("input.receiptId", id)).Sort(new BsonDocument("_id", 1))
            .Limit(ProcurementStockSourceRecords.MaximumRows + 1).ToListAsync(ct);
        if (raw.Count > ProcurementStockSourceRecords.MaximumRows) throw ProcurementStockSourceRecords.Unavailable();
        foreach (var proposal in raw) {
            var proved = await quality.ProposalAsync(tx, proposal["_id"].AsInt64, ct);
            _ = await quality.DecisionAsync(tx, proved, ct);
        }
        _ = await qualityCapacity.ReadAsync(tx, row, ct);
        await quality.OrphansAsync(tx, ct);
        var movement = await new HeldReceiptMovementHistory(database, unit, clock).RequireAsync(tx, row, ct);
        return new(kind, id, row["poolId"].AsInt64, row, ProcurementStockReceiptBson.Audit(row), row["recordedAt"], movement);
    }
    internal Task OrphansAsync(IClientSessionHandle tx, CancellationToken ct) => receipts.OrphansAsync(tx, ct);
    internal async Task PreflightAsync(IClientSessionHandle tx, CancellationToken ct)
    {
        await receiptOwner.RequireStockSourcePredecessorStorageAsync(ct);
        await PreflightCoreAsync(tx, ct);
    }
    internal async Task PreflightRetainedForMigrationAsync(IClientSessionHandle tx, MigrationLease lease, CancellationToken ct)
    {
        await RequireRetainedMigrationAsync(lease, ct);
        await PreflightCoreAsync(tx, ct);
    }
    private async Task RequireRetainedMigrationAsync(MigrationLease lease, CancellationToken ct)
    {
        await lease.RenewAsync(ct);
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(94).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, 93) || history.Count != 93 || history.Take(92).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw ProcurementStockSourceRecords.Unavailable();
        await receiptOwner.RequirePreInboundStockSourceStorageForMigrationAsync(ct);
    }
    private async Task PreflightCoreAsync(IClientSessionHandle tx, CancellationToken ct)
    {
        await receipts.OrphansAsync(tx, ct);
        await dispositions.OrphansAsync(tx, ct);
        await quality.PreflightAsync(tx, ct);
    }
}
