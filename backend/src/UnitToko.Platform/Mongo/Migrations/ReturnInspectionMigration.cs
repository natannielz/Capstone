using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using I = UnitToko.Platform.Inventory.ReturnInspectionRecords;
using Q = UnitToko.Platform.Inventory.ReturnQuarantineLedger;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Mongo.Migrations;
internal sealed class ReturnInspectionMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 50;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct); var rows = database.GetCollection<BsonDocument>(I.Collection).WithReadConcern(ReadConcern.Majority);
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", I.Collection) }, ct)).SingleOrDefaultAsync(ct);
        if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad();
        var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
        if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
            options.Contains("encryptedFields") || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
        var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct); var key = ReturnInspectionSchemas.Key(); const string name = "ux_return_inspection_request";
        if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") || index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key)) ||
            definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1))) ||
            indexes.Any(index => (index["key"].Equals(key) || index["name"] == name) && (!index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean))) throw Bad();
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            if (await database.GetCollection<BsonDocument>(table).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument(field, new BsonRegularExpression(@"^fulfillment\.case\.return_inspect(?:[.:]|$)"))).Limit(1).AnyAsync(ct)) throw Bad();
        await InventoryRepository.RequireStorageAsync(database, Q.Collection, ReturnQuarantineSchemas.Validator(), [ReturnQuarantineSchemas.Key(), ReturnQuarantineSchemas.RequestKey()], ct);
        using (var tx = await database.Client.StartSessionAsync(cancellationToken: ct)) {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority)); var requests = 0L; var decisions = 0L; var receipts = 0L;
            using var cursor = await database.GetCollection<BsonDocument>(R.Collection).Find(tx, new BsonDocument()).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
                await lease.RenewAsync(ct); var root = await new R(database).ReadAsync(tx, candidate["_id"].AsInt64, ct);
                var invoice = await database.GetCollection<BsonDocument>(InvoiceBson.Invoices).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", root["source"]["invoiceId"]) & InvoiceSchemas.Validator(InvoiceBson.Invoices)).SingleOrDefaultAsync(ct) ?? throw Bad();
                if (invoice["snapshotHash"] != InvoiceBson.Hash(invoice) || !InvoiceRecords.Equal(root["source"], InvoiceService.CaseSnapshot(invoice))) throw Bad();
                var store = new AfterSalesDecisionRecords(database); var decision = await store.ReadAsync(tx, root, ct); await store.RequireCapacityAsync(tx, root, [], ct); requests++;
                if (decision is null) continue; decisions++;
                if (decision["state"] != "return_authorized") continue;
                var stock = await InvoiceService.CaseDispatchStockAsync(database, tx, root["source"].AsBsonDocument, ct);
                var source = Q.Source(root, decision, new BsonArray(stock.Where(line => decision["input"]["lines"].AsBsonArray.Any(item => item["receiptId"] == line["receiptId"] && item["skuId"] == line["skuId"]))));
                receipts = checked(receipts + (await new Q(database, clock).ReadAsync(tx, source, ct)).Revision);
            }
            if (await database.GetCollection<BsonDocument>(AfterSalesDecisionRecords.Collection).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != decisions ||
                await database.GetCollection<BsonDocument>(Q.Collection).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != receipts) throw Bad();
            foreach (var (operation, count) in new[] { (R.Operation, requests), (AfterSalesDecisionRecords.Operation, decisions), (Q.Operation, receipts) })
                foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                    if (await database.GetCollection<BsonDocument>(table).CountDocumentsAsync(tx, new BsonDocument(field, operation), cancellationToken: ct) != count) throw Bad();
            await tx.AbortTransactionAsync(ct);
        }
        var required = ReturnInspectionSchemas.Validator(); var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
        var validator = current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current : new BsonDocument("$and", new BsonArray { current, required });
        await lease.RenewAsync(ct); await database.RunCommandAsync<BsonDocument>(new BsonDocument { { definition is null ? "create" : "collMod", I.Collection }, { "validator", validator },
            { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        if (!indexes.Any(index => index["key"].Equals(key))) await rows.WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = name }), cancellationToken: ct);
    }
    private static InvalidOperationException Bad() => new("Return inspection migration preflight failed");
}
