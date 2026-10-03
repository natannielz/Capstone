using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Procurement;
using D = UnitToko.Platform.Procurement.ProcurementDisbursementBson;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed partial class ProcurementSettlementMigration
{
    private async Task PreflightDisbursementsAsync(IClientSessionHandle tx, MigrationLease lease, CancellationToken ct) {
        if (database.Client is not MongoClient client) throw Bad();
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database);
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var suppliers = new SupplierService(database, unit, ids, clock, catalog);
        var requests = new ProcurementRequestService(database, unit, ids, clock, catalog);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, requests, suppliers);
        var funding = new ProcurementFundingService(database, unit, ids, clock, catalog, orders);
        var accounts = new ReceivingAccountService(database, unit, ids, clock);
        var statements = new BankStatementService(database, unit, ids, clock, accounts);
        var banks = new BankReceiptService(database, unit, ids, clock, statements);
        var beneficiaries = new SupplierBankService(database, unit, ids, clock, catalog);
        var owner = new ProcurementDisbursementService(database, unit, ids, clock, catalog, funding, beneficiaries, banks);
        var records = new ProcurementDisbursementRecords(database, unit, clock, catalog, funding);
        var history = new ProcurementDisbursementRecords.HistoricalSources(records, tx, () => BankTransactionClaims.RequireTransaction(database, tx));
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        var drafts = await Rows(D.Drafts).Find(tx, new BsonDocument()).Sort(new BsonDocument("recordedAt", 1)).ToListAsync(ct);
        foreach (var draft in drafts) { await lease.RenewAsync(ct); _ = await records.DraftAsync(tx, draft["_id"].AsInt64, ct, history: history); }
        var payments = await Rows(D.Records).Find(tx, new BsonDocument()).ToListAsync(ct);
        foreach (var payment in payments) { await lease.RenewAsync(ct); _ = await owner.ReadSettlementHistoryAsync(tx, payment["_id"].AsInt64, ct); }
        if (await Rows(D.Allocations).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != payments.Count ||
            await Rows(D.Heads).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != payments.Select(row => row["authorizationId"]).Distinct().Count()) throw Bad();
        foreach (var (operation, count) in new[] { (D.DraftOperation, drafts.Count), (D.RecordOperation, payments.Count) })
            foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("idempotencyRecords", "operation"), ("outboxEvents", "kind") })
                if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument(field, operation), cancellationToken: ct) != count) throw Bad();
        foreach (var pin in await Rows("evidencePins").Find(tx, new BsonDocument("associationKind", "ProcurementDisbursement")).ToListAsync(ct))
            if (!payments.Any(row => row["_id"] == pin["associationId"] && row["pinId"] == pin["_id"])) throw Bad();
    }
}
