using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Organizations;

namespace UnitToko.Platform.Mongo.Migrations;

// Deployment pauses organization/address writers. No address, profile or authority is seeded.
internal sealed class OrganizationAddressMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 58;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct); await RequireHistoryAsync(ct);
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing)>();
        foreach (var name in OrganizationAddressSchemas.Collections) {
            await lease.RenewAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad();
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
            var keys = OrganizationAddressSchemas.Keys(name); var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                    index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !keys.Any(key => index["key"] == key)) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1))) throw Bad();
            var missing = new List<BsonDocument>();
            for (var i = 0; i < keys.Length; i++) {
                var matching = indexes.Where(index => index["key"] == keys[i] || index["name"] == "ux_" + name + "_" + i).ToArray();
                if (matching.Any(index => index["key"] != keys[i] || !index.GetValue("unique", false).AsBoolean)) throw Bad();
                if (matching.Length == 0) missing.Add(keys[i]);
            }
            var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 1)) throw Bad();
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = OrganizationAddressSchemas.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current :
                new BsonDocument("$and", new BsonArray { current, required }), missing.ToArray()));
        }
        var prefix = new BsonRegularExpression(@"^organization\.address(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"),
            ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation") })
            if (await database.GetCollection<BsonDocument>(table).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument(field, prefix)).Limit(1).AnyAsync(ct)) throw Bad();
        await OrganizationMasterDataService.PreflightAddressSourcesAsync(database, lease, ct);
        // No DDL precedes exact retained profile/name/hierarchy and original approval proof.
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name },
                { "validator", item.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in item.Missing) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = "ux_" + item.Name + "_" +
                    Array.FindIndex(OrganizationAddressSchemas.Keys(item.Name), expected => expected == key) }), cancellationToken: ct);
        }
    }
    private async Task RequireHistoryAsync(CancellationToken ct) {
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(59).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(57).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Bad();
    }
    private static InvalidOperationException Bad() => new("Organization address migration preflight failed");
}
