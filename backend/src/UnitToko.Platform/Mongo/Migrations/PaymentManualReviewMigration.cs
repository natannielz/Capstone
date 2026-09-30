using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class PaymentManualReviewMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 42;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, bool Index)>();
        foreach (var name in PaymentManualReviewSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad(name);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new(); RequireOptions(definition, options, name);
            var key = PaymentManualReviewSchemas.Key(name); var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") || index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key)) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Bad(name);
            var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == "ux_" + name).ToArray();
            if (matching.Any(index => !index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean)) throw Bad(name);
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = PaymentManualReviewSchemas.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current : new("$and", new BsonArray { current, required }), matching.Length == 0));
            var counter = await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Bad(name);
        }
        var decisions = PaymentVerificationBson.Decisions;
        var saved = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", decisions) }, ct)).SingleOrDefaultAsync(ct) ?? throw Bad(decisions);
        var savedOptions = saved["options"].AsBsonDocument; RequireOptions(saved, savedOptions, decisions);
        if (savedOptions.GetValue("validationLevel", "strict") != "strict" || savedOptions.GetValue("validationAction", "error") != "error" ||
            await database.GetCollection<BsonDocument>(decisions).Find(new BsonDocument("$nor", new BsonArray { PaymentVerificationSchemas.Validator(decisions) })).AnyAsync(ct)) throw Bad(decisions);
        var oldValidator = PaymentVerificationSchemas.Validator(decisions); var extension = PaymentManualReviewSchemas.VerificationValidator();
        // Preserve any additional operator constraints; replace only the exact former owning schema.
        BsonDocument Extend(BsonDocument current) {
            if (current.Equals(oldValidator) || current.Equals(extension)) return extension;
            if (current.ElementCount == 1 && current.TryGetValue("$and", out var terms) && terms.IsBsonArray) {
                var changed = false; var result = new BsonArray();
                foreach (var term in terms.AsBsonArray) {
                    if (term.IsBsonDocument && (term.AsBsonDocument.Equals(oldValidator) || term.AsBsonDocument.Equals(extension))) { result.Add(extension); changed = true; }
                    else result.Add(term.DeepClone());
                }
                if (changed) return new("$and", result);
            }
            throw Bad(decisions);
        }
        var extended = Extend(savedOptions.GetValue("validator", new BsonDocument()).AsBsonDocument);
        var prefix = new BsonRegularExpression(@"^billing\.payment\.review\.manual(?:[.:]|$)");
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new("operation", prefix)), ("auditEvents", new("kind", prefix)), ("outboxEvents", new("kind", prefix))
        }) if (await database.GetCollection<BsonDocument>(name).Find(filter).Limit(1).AnyAsync(ct)) throw Bad(name);
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name }, { "validator", item.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            if (item.Index) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(PaymentManualReviewSchemas.Key(item.Name), new CreateIndexOptions { Name = "ux_" + item.Name, Unique = true }), cancellationToken: ct);
        }
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", decisions }, { "validator", extended }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
    }
    private static void RequireOptions(BsonDocument? definition, BsonDocument options, string name) {
        if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
            options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad(name);
    }
    private static InvalidOperationException Bad(string name) => new("Payment manual review migration preflight failed: " + name);
}
