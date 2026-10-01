using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using B = UnitToko.Platform.Billing.ReturnEntitlementBson;
using D = UnitToko.Platform.Fulfillment.AfterSalesDecisionRecords;
using I = UnitToko.Platform.Inventory.ReturnInspectionRecords;
using P = UnitToko.Platform.Fulfillment.ReturnResolutionRecords;
using Q = UnitToko.Platform.Inventory.ReturnQuarantineLedger;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Mongo.Migrations;
internal sealed class ReturnEntitlementMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 53;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing)>();
        foreach (var name in ReturnEntitlementSchemas.Collections) {
            await lease.RenewAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad();
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
            var keys = ReturnEntitlementSchemas.Keys(name);
            var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                    index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !keys.Any(key => index["key"].Equals(key))) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Bad();
            var missing = new List<BsonDocument>();
            for (var i = 0; i < keys.Length; i++) {
                var key = keys[i]; var primary = key.Equals(new BsonDocument("_id", 1)); var expected = primary ? "_id_" : "ux_" + name + "_" + i;
                var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == expected).ToArray();
                if (matching.Any(index => !index["key"].Equals(key) || !primary && !index.GetValue("unique", false).AsBoolean)) throw Bad();
                if (matching.Length == 0 && !primary) missing.Add(key);
            }
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = ReturnEntitlementSchemas.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current :
                new BsonDocument("$and", new BsonArray { current, required }), missing.ToArray()));
        }
        // These new owners have never written before this migration; surviving effects are
        // evidence of missing history, never permission to initialize an empty money pool.
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey") })
            if (await database.GetCollection<BsonDocument>(table).WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument(field, new BsonRegularExpression("^" + Regex.Escape(B.Operation) + @"(?:[.:]|$)"))).Limit(1).AnyAsync(ct)) throw Bad();
        await PaymentAllocationStorage.RequireAsync(database, ct);
        foreach (var (name, validator, keys) in new[] {
            (R.Collection, AfterSalesCaseSchemas.Validator(), new[] { AfterSalesCaseSchemas.Key() }),
            (D.Collection, AfterSalesDecisionSchemas.Validator(), new[] { AfterSalesDecisionSchemas.Key() }),
            (Q.Collection, ReturnQuarantineSchemas.Validator(), new[] { ReturnQuarantineSchemas.Key(), ReturnQuarantineSchemas.RequestKey() }),
            (I.Collection, ReturnInspectionSchemas.Validator(), new[] { ReturnInspectionSchemas.Key() }),
            (P.Collection, ReturnResolutionSchemas.Validator(), new[] { ReturnResolutionSchemas.Key() }) })
            await InventoryRepository.RequireStorageAsync(database, name, validator, keys, ct);
        await PreflightSourcesAsync(database, clock, lease, ct);
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name }, { "validator", item.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in item.Missing) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = "ux_" + item.Name + "_" +
                    Array.FindIndex(ReturnEntitlementSchemas.Keys(item.Name), expected => expected.Equals(key)) }), cancellationToken: ct);
        }
    }
    internal static async Task PreflightSourcesAsync(IMongoDatabase database, IClock clock, MigrationLease lease, CancellationToken ct) {
        using var tx = await database.Client.StartSessionAsync(cancellationToken: ct);
        tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        var cases = 0L; var decisions = 0L; var physicalReceipts = 0L; var inspections = 0L; var proposals = 0L;
        using var cursor = await database.GetCollection<BsonDocument>(R.Collection).Find(tx, new BsonDocument()).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
            await lease.RenewAsync(ct);
            var root = await new R(database).ReadAsync(tx, candidate["_id"].AsInt64, ct);
            var invoice = await database.GetCollection<BsonDocument>(InvoiceBson.Invoices).Find(tx, new BsonDocument("_id", root["source"]["invoiceId"])).SingleOrDefaultAsync(ct) ?? throw Bad();
            await InvoiceRecords.VerifyPersistedAsync(database, tx, invoice, ct);
            var recipientReceipts = new ShipmentReceiptRecords(database);
            foreach (var original in InvoiceBson.Sources(invoice["sources"].AsBsonArray)) {
                var expected = original.Snapshot["receipt"].AsBsonDocument;
                var received = await recipientReceipts.FindReceiptAsync(tx, original.Snapshot["shipment"]["shipmentId"].AsInt64, ct) ?? throw Bad();
                if (!await recipientReceipts.Rows(ShipmentReceiptRecords.Receipts).Find(tx,
                        Builders<BsonDocument>.Filter.Eq("_id", received["_id"]) & ShipmentReceiptSchemas.Validator(ShipmentReceiptRecords.Receipts)).AnyAsync(ct) ||
                    expected["id"] != received["_id"] || expected["recordHash"] != CatalogRepository.Digest(received)) throw Bad();
                foreach (var field in new[] { "recordedAt", "recipientName", "documentId", "documentHash" })
                    if (expected[field] != received[field]) throw Bad();
                if (!InvoiceRecords.Equal(expected["lines"], new BsonArray(received["lines"].AsBsonArray.Where(line => line["acceptedBaseUnits"].AsInt64 > 0).Select(line => line.DeepClone())))) throw Bad();
                await recipientReceipts.VerifyEffectsAsync(tx, received, ShipmentReceiptRecords.Confirm, ct);
            }
            if (!InvoiceRecords.Equal(root["source"], InvoiceService.CaseSnapshot(invoice))) throw Bad();
            _ = await InvoiceService.AllocationSnapshotAsync(database, tx, invoice, ct);
            var decisionStore = new D(database); var decision = await decisionStore.ReadAsync(tx, root, ct);
            await decisionStore.RequireCapacityAsync(tx, root, [], ct); cases++;
            if (decision is null) continue;
            decisions++; if (decision["state"] != "return_authorized") continue;
            var stock = await InvoiceService.CaseDispatchStockAsync(database, tx, root["source"].AsBsonDocument, ct);
            var source = Q.Source(root, decision, new BsonArray(stock.Where(line => decision["input"]["lines"].AsBsonArray.Any(item => item["receiptId"] == line["receiptId"] && item["skuId"] == line["skuId"]))));
            physicalReceipts = checked(physicalReceipts + (await new Q(database, clock).ReadAsync(tx, source, ct)).Revision);
            using var physical = await database.GetCollection<BsonDocument>(Q.Collection).Find(tx, new BsonDocument("caseId", root["_id"])).ToCursorAsync(ct);
            while (await physical.MoveNextAsync(ct)) foreach (var receipt in physical.Current) {
                var inspection = await new I(database, clock).ReadAsync(tx, receipt, ct);
                if (inspection is null) continue;
                inspections++;
                if (await new P(database, clock).ReadAsync(tx, inspection, ct) is not null) proposals++;
            }
        }
        foreach (var (name, count) in new[] { (R.Collection, cases), (D.Collection, decisions), (Q.Collection, physicalReceipts), (I.Collection, inspections), (P.Collection, proposals) })
            if (await database.GetCollection<BsonDocument>(name).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != count) throw Bad();
        foreach (var (operation, count) in new[] { (R.Operation, cases), (D.Operation, decisions), (Q.Operation, physicalReceipts), (I.Operation, inspections), (P.Operation, proposals) })
            foreach (var (name, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                if (await database.GetCollection<BsonDocument>(name).CountDocumentsAsync(tx, new BsonDocument(field, operation), cancellationToken: ct) != count) throw Bad();
        await tx.AbortTransactionAsync(ct);
    }
    private static InvalidOperationException Bad() => new("Return entitlement migration preflight failed");
}
