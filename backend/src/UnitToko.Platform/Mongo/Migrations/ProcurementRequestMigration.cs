using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Organizations;

namespace UnitToko.Platform.Mongo.Migrations;

// Installs empty requests/decisions. Retained catalog and organization sources must already be genuine.
internal sealed class ProcurementRequestMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 61;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct); await RequireHistoryAsync(ct);
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing)>();
        foreach (var name in ProcurementRequestSchemas.Collections) {
            await lease.RenewAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad();
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
            var keys = ProcurementRequestSchemas.Keys(name); var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                    index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !keys.Any(key => index["key"] == key)) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1))) throw Bad();
            var missing = new List<BsonDocument>();
            for (var i = 0; i < keys.Length; i++) {
                var matching = indexes.Where(index => index["key"] == keys[i] || index["name"] == "ux_" + name + "_" + i).ToArray();
                if (matching.Any(index => index["key"] != keys[i] || !index.GetValue("unique", false).AsBoolean)) throw Bad();
                if (matching.Length == 0) missing.Add(keys[i]);
            }
            await CounterAsync(name, ct);
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = ProcurementRequestSchemas.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current :
                new BsonDocument("$and", new BsonArray { current, required }), missing.ToArray()));
        }
        var prefix = new BsonRegularExpression(@"^procurement\.request(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"),
            ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation") })
            if (await database.GetCollection<BsonDocument>(table).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument(field, prefix)).Limit(1).AnyAsync(ct)) throw Bad();
        var retained = new Dictionary<string, Dictionary<long, BsonDocument>>(); var maxima = new Dictionary<string, long>();
        foreach (var name in CatalogSchemas.Collections) {
            await lease.RenewAsync(ct); var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw Bad();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
                options.Contains("encryptedFields") || options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation) ||
                !options.TryGetValue("validator", out var validator) || !validator.IsBsonDocument || !InventorySchemas.Contains(validator.AsBsonDocument, CatalogSchemas.Validator(name))) throw Bad();
            var indexes = await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => index.Contains("expireAfterSeconds")) || !indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1) && !InvoiceStorage.Unsafe(index) && !index.Contains("partialFilterExpression"))) throw Bad();
            var all = await rows.Find(new BsonDocument()).ToListAsync(ct);
            if (all.Any(row => !CatalogSchemas.IsValid(name, row)) || await rows.Find(new BsonDocument("$nor", new BsonArray { CatalogSchemas.Validator(name) })).Limit(1).AnyAsync(ct)) throw Bad();
            retained.Add(name, all.ToDictionary(row => row["_id"].AsInt64)); maxima.Add(name, all.Select(row => row["_id"].AsInt64).DefaultIfEmpty(0).Max());
        }
        await CatalogMigrationGraph.ValidateAsync(database, retained, ct);
        foreach (var name in new[] { "auditEvents", "idempotencyRecords", "outboxEvents" }) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw Bad();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
                options.Contains("encryptedFields") || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
            PaymentEvidenceMigration.RequireIndexes(name, await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct));
        }
        var audits = await database.GetCollection<BsonDocument>("auditEvents").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument()).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (audits.Any(row => !row.TryGetValue("_id", out var id) || !id.IsInt64 || id.AsInt64 is <= 0 or >= long.MaxValue - 1)) throw Bad();
        maxima.Add("auditEvents", audits.Select(row => row["_id"].AsInt64).DefaultIfEmpty(0).Max());
        foreach (var (name, maximum) in maxima) { if (maximum >= long.MaxValue - 1) throw Bad(); await CounterAsync(name, ct); }
        await OrganizationMasterDataService.PreflightAddressSourcesAsync(database, lease, ct);
        // Every retained source, graph, storage and counter check precedes DDL or high-water updates.
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name },
                { "validator", item.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in item.Missing) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = "ux_" + item.Name + "_" +
                    Array.FindIndex(ProcurementRequestSchemas.Keys(item.Name), expected => expected == key) }), cancellationToken: ct);
        }
        foreach (var (name, maximum) in maxima) if (maximum > 0) {
            await lease.RenewAsync(ct);
            await database.GetCollection<BsonDocument>("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(new BsonDocument("_id", name),
                Builders<BsonDocument>.Update.Max("value", maximum), new UpdateOptions { IsUpsert = true }, ct);
        }
    }
    private async Task CounterAsync(string name, CancellationToken ct) {
        var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
        if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 1)) throw Bad();
    }
    private async Task RequireHistoryAsync(CancellationToken ct) {
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(62).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(60).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Bad();
    }
    private static InvalidOperationException Bad() => new("Procurement request migration preflight failed");
}
