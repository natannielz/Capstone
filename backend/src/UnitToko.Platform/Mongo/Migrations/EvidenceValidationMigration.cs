using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class EvidenceValidationMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 32;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, bool Index)>();
        foreach (var name in EvidenceValidationSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority); if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad(name);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
                options.Contains("encryptedFields") || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad(name);
            var key = EvidenceValidationSchemas.Key(name); var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") || index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key)) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Bad(name);
            var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == "ux_" + name).ToArray();
            if (matching.Any(index => !index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean)) throw Bad(name);
            var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var number) || !number.IsInt64 || number.AsInt64 is < 0 or long.MaxValue)) throw Bad(name);
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = EvidenceValidationSchemas.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current : new("$and", new BsonArray { current, required }), matching.Length == 0));
        }
        var outbox = database.GetCollection<BsonDocument>("outboxEvents").WithReadConcern(ReadConcern.Majority);
        if (await outbox.Find(new BsonDocument("$or", new BsonArray { new BsonDocument("processingReceipt", new BsonDocument("$exists", true)), new BsonDocument("processingFence", new BsonDocument("$exists", true)) })).Limit(1).AnyAsync(ct) ||
            await outbox.Find(new BsonDocument("$nor", new BsonArray { EvidenceValidationSchemas.OutboxValidator() })).Limit(1).AnyAsync(ct)) throw Bad("outboxEvents");
        var outboxDefinition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "outboxEvents") }, ct)).SingleAsync(ct);
        var outboxOptions = outboxDefinition["options"].AsBsonDocument;
        if (outboxDefinition.GetValue("type", "") != "collection" || outboxOptions.GetValue("capped", false).AsBoolean || outboxOptions.Contains("timeseries") ||
            outboxOptions.Contains("expireAfterSeconds") || outboxOptions.Contains("encryptedFields") || outboxOptions.TryGetValue("collation", out var outboxCollation) && !InvoiceStorage.Simple(outboxCollation)) throw Bad("outboxEvents");
        var prior = outboxOptions.GetValue("validator", new BsonDocument()).AsBsonDocument;
        if (!InventorySchemas.Contains(prior, EvidenceSchemas.Validator("outboxEvents"))) throw Bad("outboxEvents");
        if (await outbox.Find(new BsonDocument("$nor", new BsonArray { new BsonDocument("$and", new BsonArray { prior, EvidenceSchemas.Validator("outboxEvents") }) })).Limit(1).AnyAsync(ct)) throw Bad("outboxEvents");
        PaymentEvidenceMigration.RequireIndexes("outboxEvents", await (await outbox.Indexes.ListAsync(ct)).ToListAsync(ct));
        var next = InventorySchemas.Contains(prior, EvidenceValidationSchemas.OutboxValidator()) ? prior : new BsonDocument("$and", new BsonArray { prior, EvidenceValidationSchemas.OutboxValidator() });
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name }, { "validator", item.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            if (item.Index) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(EvidenceValidationSchemas.Key(item.Name), new CreateIndexOptions { Unique = true, Name = "ux_" + item.Name }), cancellationToken: ct);
        }
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" }, { "validator", next }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
    }
    private static InvalidOperationException Bad(string name) => new("Evidence validation migration preflight failed: " + name);
}
