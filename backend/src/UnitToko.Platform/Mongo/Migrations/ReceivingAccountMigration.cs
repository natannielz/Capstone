using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class ReceivingAccountMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 37;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, bool Index)>();
        foreach (var name in ReceivingAccountSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority); if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad(name);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new(); RequireOptions(definition, options, name);
            var key = ReceivingAccountSchemas.Key(name); var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") || index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key)) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Bad(name);
            var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == "ux_" + name).ToArray();
            if (matching.Any(index => !index["key"].Equals(key) || index["name"] != "_id_" && !index.GetValue("unique", false).AsBoolean)) throw Bad(name);
            var counter = await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Bad(name);
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = ReceivingAccountSchemas.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current : new("$and", new BsonArray { current, required }), matching.Length == 0 && !key.Equals(new BsonDocument("_id", 1))));
        }
        var prefix = new BsonRegularExpression(@"^billing\.receiving-account(?:[.:]|$)");
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new("operation", prefix)),
            ("auditEvents", new("$or", new BsonArray { new BsonDocument("kind", prefix), new BsonDocument("operation", prefix) })),
            ("outboxEvents", new("$or", new BsonArray { new BsonDocument("kind", prefix), new BsonDocument("sourceKey", prefix) }))
        }) if (await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority).Find(filter).Limit(1).AnyAsync(ct)) throw Bad(name);
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name }, { "validator", item.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            if (item.Index) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(ReceivingAccountSchemas.Key(item.Name), new CreateIndexOptions { Name = "ux_" + item.Name, Unique = true }), cancellationToken: ct);
        }
    }
    private static void RequireOptions(BsonDocument? definition, BsonDocument options, string name) {
        if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
            options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad(name);
    }
    private static InvalidOperationException Bad(string name) => new("Receiving account migration preflight failed: " + name);
}
