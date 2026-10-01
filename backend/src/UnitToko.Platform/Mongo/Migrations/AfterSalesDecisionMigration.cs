using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using D = UnitToko.Platform.Fulfillment.AfterSalesDecisionRecords;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Mongo.Migrations;
internal sealed class AfterSalesDecisionMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 48;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct);
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", D.Collection) }, ct)).SingleOrDefaultAsync(ct);
        var rows = database.GetCollection<BsonDocument>(D.Collection).WithReadConcern(ReadConcern.Majority);
        if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad();
        var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
        if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
            options.Contains("encryptedFields") || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
        var key = AfterSalesDecisionSchemas.Key(); var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct); const string indexName = "ux_after_sales_case_decision_request";
        if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") || index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key)) ||
            definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Bad();
        var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == indexName).ToArray();
        if (matching.Any(index => !index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean)) throw Bad();
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            if (await database.GetCollection<BsonDocument>(table).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument(field, new BsonRegularExpression(@"^fulfillment\.case\.decide(?:[.:]|$)"))).Limit(1).AnyAsync(ct)) throw Bad();
        const string fenceName = InvoiceService.CaseReturnFences;
        var fenceDefinition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", fenceName) }, ct)).SingleOrDefaultAsync(ct);
        var fenceRows = database.GetCollection<BsonDocument>(fenceName).WithReadConcern(ReadConcern.Majority);
        if (await fenceRows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad();
        var fenceOptions = fenceDefinition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
        if (fenceDefinition is not null && fenceDefinition.GetValue("type", "") != "collection" || fenceOptions.GetValue("capped", false).AsBoolean || fenceOptions.Contains("timeseries") || fenceOptions.Contains("expireAfterSeconds") ||
            fenceOptions.Contains("encryptedFields") || fenceOptions.TryGetValue("collation", out var fenceCollation) && !InvoiceStorage.Simple(fenceCollation)) throw Bad();
        var fenceIndexes = fenceDefinition is null ? [] : await (await fenceRows.Indexes.ListAsync(ct)).ToListAsync(ct);
        if (fenceIndexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") || index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean) ||
            fenceDefinition is not null && !fenceIndexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Bad();
        // Preflight preserves populated version-47 requests and their exact finalized-invoice anchors.
        // Protected runtime readers still verify the complete shipment/order/journal chain.
        using (var tx = await database.Client.StartSessionAsync(cancellationToken: ct)) {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority)); var count = 0;
            using var cursor = await database.GetCollection<BsonDocument>(R.Collection).Find(tx, new BsonDocument()).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
                await lease.RenewAsync(ct); var root = await new R(database).ReadAsync(tx, candidate["_id"].AsInt64, ct);
                var invoice = await database.GetCollection<BsonDocument>(InvoiceBson.Invoices).Find(tx,
                    Builders<BsonDocument>.Filter.Eq("_id", root["source"]["invoiceId"]) & InvoiceSchemas.Validator(InvoiceBson.Invoices)).SingleOrDefaultAsync(ct) ?? throw Bad();
                if (invoice["snapshotHash"] != InvoiceBson.Hash(invoice) || !InvoiceRecords.Equal(root["source"], InvoiceService.CaseSnapshot(invoice))) throw Bad(); count++;
            }
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                if (await database.GetCollection<BsonDocument>(table).CountDocumentsAsync(tx, new BsonDocument(field, R.Operation), cancellationToken: ct) != count) throw Bad();
            await tx.AbortTransactionAsync(ct);
        }
        var required = AfterSalesDecisionSchemas.Validator(); var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
        var validator = current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current : new BsonDocument("$and", new BsonArray { current, required });
        await lease.RenewAsync(ct); await database.RunCommandAsync<BsonDocument>(new BsonDocument { { definition is null ? "create" : "collMod", D.Collection }, { "validator", validator },
            { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        if (matching.Length == 0) await rows.WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = indexName }), cancellationToken: ct);
        var fenceRequired = InvoiceCaseReturnSchemas.Validator(); var fenceCurrent = fenceOptions.GetValue("validator", new BsonDocument()).AsBsonDocument;
        var fenceValidator = fenceCurrent.ElementCount == 0 ? fenceRequired : InventorySchemas.Contains(fenceCurrent, fenceRequired) ? fenceCurrent : new BsonDocument("$and", new BsonArray { fenceCurrent, fenceRequired });
        await lease.RenewAsync(ct); await database.RunCommandAsync<BsonDocument>(new BsonDocument { { fenceDefinition is null ? "create" : "collMod", fenceName }, { "validator", fenceValidator },
            { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
    }
    private static InvalidOperationException Bad() => new("After-sales decision migration preflight failed");
}
