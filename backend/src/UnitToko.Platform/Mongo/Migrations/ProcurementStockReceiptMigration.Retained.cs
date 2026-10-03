using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;
using Q = UnitToko.Platform.Procurement.ProcurementQuarantineDispositionBson;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed partial class ProcurementStockReceiptMigration
{
    private async Task RequireSourcePoliciesAsync(MigrationLease lease, Dictionary<string, long> maxima, CancellationToken ct) {
        foreach (var name in StockSourceSetupSchemas.Collections.Concat(StockSourceRevisionSchemas.Collections).Concat(StockSourceAliasSchemas.Collections).Concat(StockSourcePolicySchemas.Collections).Append(StockSourceSuspensionSchemas.Collection)) {
            await lease.RenewAsync(ct);
            var setup = StockSourceSetupSchemas.Collections.Contains(name); var revision = StockSourceRevisionSchemas.Collections.Contains(name); var alias = StockSourceAliasSchemas.Collections.Contains(name);
            var validator = setup ? StockSourceSetupSchemas.Validator(name) : revision ? StockSourceRevisionSchemas.Validator(name) : alias ? StockSourceAliasSchemas.Validator(name) : name == StockSourceSuspensionSchemas.Collection ? StockSourceSuspensionSchemas.Validator() : StockSourcePolicySchemas.Validator(name);
            var key = setup ? StockSourceSetupSchemas.Key(name) : revision ? StockSourceRevisionSchemas.Key(name) : alias ? StockSourceAliasSchemas.Key(name) : name == StockSourceSuspensionSchemas.Collection ? StockSourceSuspensionSchemas.Key() : StockSourcePolicySchemas.Key(name);
            await InventoryRepository.RequireStorageAsync(database, name, validator, key, ct);
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad();
            if (name != "inventoryPoolRegistrations") await CounterAsync(name, maxima, ct);
        }
        foreach (var index in StockSourceSuspensionSchemas.LookupIndexes().Select(value => (value.Collection, value.Name, value.Key)).Concat(
            StockSourcePolicySchemas.LookupIndexes().Select(value => (value.Collection, value.Name, value.Key)))) {
            var definitions = await (await Rows(index.Collection).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (!definitions.Any(value => value["name"] == index.Name && value["key"] == index.Key && !UnitToko.Platform.Billing.InvoiceStorage.Unsafe(value) &&
                !value.Contains("partialFilterExpression") && !value.GetValue("unique", false).AsBoolean)) throw Bad();
        }
        await StockSourcePolicyMigrationSourceGraph.ValidateAsync(database, lease, ct);
        await StockSourceAliasMigrationGraph.ValidateAsync(database, lease, ct);
        await StockSourcePolicyMigrationSuspensions.ValidateAsync(database, lease, ct);
        await StockSourcePolicyMigrationGraph.ValidateAsync(database, lease, clock, ct);
    }
    private async Task RequireOwnersAsync(MigrationLease lease, Dictionary<string, long> maxima, CancellationToken ct) {
        foreach (var name in SupplierClaimSchemas.Collections) {
            await lease.RenewAsync(ct);
            var validator = name == Q.PhysicalAllocations ? ProcurementStockReceiptSchemas.DispositionValidator() : SupplierClaimSchemas.Validator(name);
            // The physical successor may be mid-install, so its exact index transition was checked separately.
            if (name != Q.PhysicalAllocations) await InventoryRepository.RequireStorageAsync(database, name, validator, SupplierClaimSchemas.Keys(name), ct);
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad();
            await CounterAsync(name, maxima, ct);
        }
        if (database.Client is not MongoClient client) throw Bad(); var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database);
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, new(database, unit, ids, clock, catalog), new(database, unit, ids, clock, catalog));
        var claims = new SupplierClaimRecords(database, unit, clock, catalog, orders);
        var receipts = new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, new(database, unit, ids, clock));
        var disposition = new ProcurementQuarantineDisposition(database, claims);
        using var tx = await client.StartSessionAsync(cancellationToken: ct); tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        try {
            foreach (var raw in await Rows(SupplierClaimBson.Requests).Find(tx, new BsonDocument()).ToListAsync(ct)) {
                await lease.RenewAsync(ct); var request = await claims.RequestAsync(tx, raw["_id"].AsInt64, ct); _ = await claims.DecisionAsync(tx, request, ct);
            }
            await claims.OrphansAsync(tx, ct);
            // Reconstruct every observed physical/shortage capacity, including deleted-head and reverse-owner cases.
            foreach (var inspection in await Rows(ProcurementInspectionBson.Submissions).Find(tx, new BsonDocument()).ToListAsync(ct)) {
                var actual = await claims.InspectionAsync(tx, inspection["_id"].AsInt64, ct);
                var order = actual.Source["notice"]["source"];
                for (var i = 0; i < inspection["input"]["lines"].AsBsonArray.Count; i++) foreach (var basis in new[] { SupplierClaimBasis.PhysicalReceived, SupplierClaimBasis.ShortageObserved }) {
                    await lease.RenewAsync(ct);
                    _ = await disposition.ReadAsync(tx, basis, inspection["_id"].AsInt64, i + 1, inspection["snapshotHash"].AsString,
                        Q.Total(basis, inspection["input"]["lines"][i]), inspection["location"].AsBsonDocument, order["order"]["_id"].AsInt64, order["proposal"]["input"]["supplierId"].AsInt64, ct);
                }
            }
            await disposition.OrphansAsync(tx, ct);
            var all = await Rows(B.Receipts).Find(tx, new BsonDocument()).ToListAsync(ct);
            foreach (var row in all) { await lease.RenewAsync(ct); _ = await receipts.ReadAsync(tx, row["_id"].AsInt64, ct); }
            await receipts.OrphansAsync(tx, ct);
            // This migration admits only this genuine root-lot owner. Unproved legacy stock cannot be adopted.
            foreach (var table in new[] { "lots", "stockBalances", "movements" })
                if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != all.Count) throw Bad();
            foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
                if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument(field, B.Operation), cancellationToken: ct) != all.Count) throw Bad();
            var prefix = new BsonRegularExpression(@"^procurement\.stockreceipt(?:[.:]|$)");
            foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
                if (await Rows(table).Find(tx, new BsonDocument(field, new BsonDocument { { "$regex", prefix }, { "$ne", B.Operation } })).AnyAsync(ct)) throw Bad();
            if (await Rows("journalEntries").Find(tx, new BsonDocument("source.namespace", prefix)).AnyAsync(ct) ||
                await Rows("auditEvents").Find(tx, new BsonDocument("operation", prefix)).AnyAsync(ct)) throw Bad();
            await tx.AbortTransactionAsync(ct);
        } catch { if (tx.IsInTransaction) await tx.AbortTransactionAsync(CancellationToken.None); throw; }
    }
}
