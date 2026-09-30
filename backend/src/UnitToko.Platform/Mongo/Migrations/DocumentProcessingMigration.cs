using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class DocumentProcessingMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 35;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, bool Index)>();
        foreach (var name in DocumentProcessingSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority); if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad(name);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new(); RequireOptions(definition, options, name);
            var key = DocumentProcessingSchemas.Key(name); var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") || index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key)) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Bad(name);
            var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == "ux_" + name).ToArray();
            if (matching.Any(index => !index["key"].Equals(key) || index["name"] != "_id_" && !index.GetValue("unique", false).AsBoolean)) throw Bad(name);
            var counter = await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Bad(name);
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = DocumentProcessingSchemas.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current : new("$and", new BsonArray { current, required }), matching.Length == 0 && !key.Equals(new BsonDocument("_id", 1))));
        }
        var queue = database.GetCollection<BsonDocument>("outboxEvents").WithReadConcern(ReadConcern.Majority);
        var before = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "outboxEvents") }, ct)).SingleAsync(ct);
        var beforeOptions = before["options"].AsBsonDocument; RequireOptions(before, beforeOptions, "outboxEvents");
        PaymentEvidenceMigration.RequireIndexes("outboxEvents", await (await queue.Indexes.ListAsync(ct)).ToListAsync(ct));
        var prior = beforeOptions["validator"].AsBsonDocument;
        if (!InventorySchemas.Contains(prior, EvidenceSchemas.Validator("outboxEvents")) || !InventorySchemas.Contains(prior, EvidenceValidationSchemas.OutboxValidator())) throw Bad("outboxEvents");
        var requiredQueue = DocumentProcessingSchemas.OutboxValidator();
        if (await queue.Find(new BsonDocument("$nor", new BsonArray { new BsonDocument("$and", new BsonArray { prior, requiredQueue }) })).Limit(1).AnyAsync(ct)) throw Bad("outboxEvents");
        // Before this version no OCR consumer exists. Reject unexplained prior processing rather than adopting it.
        using (var jobs = await queue.Find(new BsonDocument("kind", "payment.ocr.requested")).ToCursorAsync(ct))
            while (await jobs.MoveNextAsync(ct)) foreach (var job in jobs.Current) {
                await lease.RenewAsync(ct);
                if (job.GetValue("status", "") != "pending" || job.GetValue("attempts", -1) != 0 || job.Contains("processingFence") || job.Contains("processingReceipt") || job.Contains("leaseToken")) throw Bad("outboxEvents");
                using var tx = await database.Client.StartSessionAsync(cancellationToken: ct); tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
                _ = await new PaymentOcrSource(database).RequireAsync(tx, job, ct); await tx.CommitTransactionAsync(ct);
            }
        var next = InventorySchemas.Contains(prior, requiredQueue) ? prior : new BsonDocument("$and", new BsonArray { prior, requiredQueue });
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name }, { "validator", item.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            if (item.Index) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(DocumentProcessingSchemas.Key(item.Name), new CreateIndexOptions { Name = "ux_" + item.Name, Unique = true }), cancellationToken: ct);
        }
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" }, { "validator", next }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
    }
    private static void RequireOptions(BsonDocument? definition, BsonDocument options, string name) {
        if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
            options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad(name);
    }
    private static InvalidOperationException Bad(string name) => new("Document processing migration preflight failed: " + name);
}
