using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class PaymentSubmissionMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 34;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, bool Index)>();
        foreach (var name in PaymentSubmissionSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad(name);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new(); RequireOptions(definition, options, name);
            var key = PaymentSubmissionSchemas.Key(name); var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            RequireIndexes(indexes, key, definition is not null, name);
            var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == "ux_" + name).ToArray();
            if (matching.Any(index => !index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean)) throw Bad(name);
            var counter = await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Bad(name);
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = PaymentSubmissionSchemas.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current : new("$and", new BsonArray { current, required }), matching.Length == 0));
        }
        var heads = (await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", PaymentDraftBson.Heads) }, ct)).SingleAsync(ct));
        var headOptions = heads["options"].AsBsonDocument; RequireOptions(heads, headOptions, PaymentDraftBson.Heads);
        RequireIndexes(await (await database.GetCollection<BsonDocument>(PaymentDraftBson.Heads).Indexes.ListAsync(ct)).ToListAsync(ct), new("_id", 1), true, PaymentDraftBson.Heads);
        var replacement = PaymentSubmissionSchemas.ExtendDeclaration(headOptions["validator"].AsBsonDocument);
        foreach (var validator in new[] { PaymentDraftSchemas.Validator(PaymentDraftBson.Heads), headOptions["validator"].AsBsonDocument })
            if (await database.GetCollection<BsonDocument>(PaymentDraftBson.Heads).Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad(PaymentDraftBson.Heads);
        // No application writer runs until acceptance. Validate real predecessor histories before widening the state shape.
        using (var cursor = await database.GetCollection<BsonDocument>(PaymentDraftBson.Heads).Find(new BsonDocument()).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var head in cursor.Current) {
                await lease.RenewAsync(ct);
                if (head["state"] != "draft" || head["schemaVersion"] != 1) throw Bad(PaymentDraftBson.Heads);
                using var tx = await database.Client.StartSessionAsync(cancellationToken: ct);
                tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
                await new PaymentDraftRecords(database).HeadAsync(tx, head["recordedBy"].AsInt64, head["_id"].AsInt64, ct);
                await tx.CommitTransactionAsync(ct);
            }
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new("operation", PaymentSubmissionBson.Operation)), ("auditEvents", new("operation", PaymentSubmissionBson.Operation)),
            ("outboxEvents", new("kind", "payment.ocr.requested")) })
            if (await database.GetCollection<BsonDocument>(name).Find(filter).Limit(1).AnyAsync(ct)) throw Bad(name);
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name }, { "validator", item.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            if (item.Index) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(PaymentSubmissionSchemas.Key(item.Name), new CreateIndexOptions { Name = "ux_" + item.Name, Unique = true }), cancellationToken: ct);
        }
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", PaymentDraftBson.Heads }, { "validator", replacement }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
    }
    private static void RequireOptions(BsonDocument? definition, BsonDocument options, string name) {
        if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
            options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad(name);
    }
    private static void RequireIndexes(IReadOnlyList<BsonDocument> indexes, BsonDocument key, bool exists, string name) {
        if (exists && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1))) ||
            indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") || index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key))) throw Bad(name);
    }
    private static InvalidOperationException Bad(string name) => new("Payment submission migration preflight failed: " + name);
}
