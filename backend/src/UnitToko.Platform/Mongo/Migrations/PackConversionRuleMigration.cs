using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;
using B = UnitToko.Platform.Inventory.PackConversionRuleBson;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Empty rule-preparation owners only. Preserves complete96 predecessor
/// storage and counts; creates no active source, lot, balance, value or stock writer.</summary>
internal sealed class PackConversionRuleMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 97;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Rule preparation needs exact96 predecessor and empty safe97 owners");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(98).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(96).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying" || clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero) throw Invalid();
        await InventoryCountStockSourceStorage.RequireAsync(database, ct);
        var retained = await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct);
        var indexes = new Dictionary<string, BsonDocument[]>();
        foreach (var definition in retained.Where(row => row.GetValue("type", "") == "collection"))
            indexes.Add(definition["name"].AsString, (await (await Rows(definition["name"].AsString).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray());
        var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        foreach (var counter in counters)
            if (counter.ElementCount != 2 || !counter.GetValue("_id", BsonNull.Value).IsString || string.IsNullOrWhiteSpace(counter["_id"].AsString) ||
                !counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"].AsInt64 is < 0 or >= long.MaxValue - 1) throw Invalid();
        // Approved rule identity is its genuine proposal ID, never an independent allocator.
        if (counters.Any(row => row["_id"] == B.Rules)) throw Invalid();
        var targets = new List<(string Name, bool Exists, BsonDocument Validator, (string Name, BsonDocument Key, bool Unique)[] Missing)>();
        foreach (var name in PackConversionRuleSchemas.Collections) {
            await lease.RenewAsync(ct); var definition = retained.SingleOrDefault(row => row.GetValue("name", "") == name);
            var required = PackConversionRuleSchemas.Validator(name); var keys = PackConversionRuleSchemas.Keys(name);
            if (definition is not null && !AccountContactStorage.SafeDefinition(definition, required)) throw Invalid();
            if (await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct) || counters.Any(row => row["_id"] == name && row["value"] != 0L)) throw Invalid();
            var existing = definition is null ? Array.Empty<BsonDocument>() : indexes[name];
            if (definition is not null && !OrderStockSourceStorage.SafeIndexes(existing, keys)) throw Invalid();
            targets.Add((name, definition is not null, definition is null ? required : definition["options"]["validator"].AsBsonDocument,
                keys.Where(key => !existing.Any(index => AccountContactStorage.Matches(index, key))).ToArray()));
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            if (await Rows(table).Find(new BsonDocument(field, new BsonDocument("$in", new BsonArray { B.Propose, B.Decide }))).Limit(1).AnyAsync(ct)) throw Invalid();
        // All candidate refusals above precede target DDL. Stronger installed
        // validators and operator nonunique indexes survive empty interruption.
        foreach (var target in targets) {
            await lease.RenewAsync(ct);
            if (!target.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", target.Name }, { "validator", target.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in target.Missing) await Rows(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key,
                new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        foreach (var before in retained.Where(row => row.GetValue("type", "") == "collection")) {
            var name = before["name"].AsString; var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            if (before["info"]["uuid"] != after["info"]["uuid"] || before.GetValue("options", new BsonDocument()) != after.GetValue("options", new BsonDocument())) throw Invalid();
            var currentIndexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var addition = targets.SingleOrDefault(target => target.Name == name);
            if (currentIndexes.Count != indexes[name].Length + (addition.Name is null ? 0 : addition.Missing.Length) ||
                indexes[name].Any(index => !currentIndexes.Any(current => current == index))) throw Invalid();
        }
        var currentCounters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (counters.Count != currentCounters.Count || counters.Where((row, index) => row != currentCounters[index]).Any()) throw Invalid();
        await PackConversionRuleStorage.RequireAsync(database, ct);
    }
}
