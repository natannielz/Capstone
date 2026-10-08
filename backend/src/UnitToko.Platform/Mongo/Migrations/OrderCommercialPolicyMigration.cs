using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Adds four empty commercial-bundle stores after exact105 history.
/// No tax treatment, terms, grant, capacity, approval or financial effect is seeded.</summary>
internal sealed class OrderCommercialPolicyMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 106;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Commercial bundle owners require exact105 history, empty prospective storage and preserved predecessor state");
    private static bool Matches(BsonDocument index, BsonDocument key) => index.GetValue("key", BsonNull.Value) == key &&
        index.GetValue("unique", false).AsBoolean && !InvoiceStorage.Unsafe(index) && !index.Contains("partialFilterExpression");

    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        await lease.RenewAsync(ct);
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(107).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(105).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Invalid();
        var names = OrderCommercialPolicySchemas.Collections;
        var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        var installs = new List<(string Name, BsonDocument? Definition, BsonDocument[] Indexes, BsonDocument[] Missing)>();
        foreach (var name in names) {
            await lease.RenewAsync(ct);
            var counter = counters.SingleOrDefault(row => row["_id"] == name);
            if (counter is not null && (!counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"] != 0L)) throw Invalid();
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            var keys = OrderCommercialPolicySchemas.Keys(name);
            var indexes = definition is null ? [] : (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (definition is not null && (!AccountContactStorage.SafeDefinition(definition, OrderCommercialPolicySchemas.Validator(name)) ||
                !indexes.Any(index => index.GetValue("name", "") == "_id_" && index.GetValue("key", BsonNull.Value) == new BsonDocument("_id", 1)) ||
                indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                    index.GetValue("name", "") != "_id_" && index.GetValue("unique", false).AsBoolean && !keys.Any(key => Matches(index, key))))) throw Invalid();
            if (await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            var missing = new List<BsonDocument>();
            for (var i = 0; i < keys.Length; i++) {
                var matches = indexes.Where(index => index.GetValue("key", BsonNull.Value) == keys[i] || index.GetValue("name", "") == "ux_" + name + "_" + i).ToArray();
                if (matches.Any(index => !Matches(index, keys[i]))) throw Invalid();
                if (matches.Length == 0) missing.Add(keys[i]);
            }
            installs.Add((name, definition, indexes, missing.ToArray()));
        }
        var prefix = new BsonRegularExpression(@"^ordering\.commercial\.policy(?:[.:]|$)");
        foreach (var (name, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"),
            ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation") })
            if (await Rows(name).Find(new BsonDocument(field, prefix)).Limit(1).AnyAsync(ct)) throw Invalid();
        var retained = (await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct))
            .Where(row => row.GetValue("type", "") == "collection" && !names.Contains(row["name"].AsString)).ToArray();
        var retainedIndexes = new Dictionary<string, BsonDocument[]>();
        foreach (var definition in retained) retainedIndexes.Add(definition["name"].AsString,
            (await (await Rows(definition["name"].AsString).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray());
        // All prospective rows, counters, effects, options and indexes are checked
        // before the first DDL. Resume preserves each already-created collection.
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            if (item.Definition is null) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "create", item.Name }, { "validator", OrderCommercialPolicySchemas.Validator(item.Name) },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
            }, cancellationToken: ct);
            foreach (var key in item.Missing) await Rows(item.Name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key,
                new CreateIndexOptions { Unique = true, Name = "ux_" + item.Name + "_" + Array.FindIndex(OrderCommercialPolicySchemas.Keys(item.Name), expected => expected == key) }), cancellationToken: ct);
        }
        foreach (var before in retained) {
            await lease.RenewAsync(ct); var name = before["name"].AsString;
            var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            var indexes = (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (before["info"]["uuid"] != after["info"]["uuid"] || before.GetValue("options", new BsonDocument()) != after.GetValue("options", new BsonDocument()) ||
                retainedIndexes[name].Length != indexes.Length || retainedIndexes[name].Any(index => !indexes.Contains(index))) throw Invalid();
        }
        foreach (var item in installs) {
            var after = await AccountContactStorage.DefinitionAsync(database, item.Name, ct) ?? throw Invalid();
            if (!AccountContactStorage.SafeDefinition(after, OrderCommercialPolicySchemas.Validator(item.Name)) || item.Definition is { } before &&
                (before["info"]["uuid"] != after["info"]["uuid"] || before["options"] != after["options"])) throw Invalid();
            var indexes = (await (await Rows(item.Name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (item.Indexes.Any(index => !indexes.Contains(index)) || OrderCommercialPolicySchemas.Keys(item.Name).Any(key => !indexes.Any(index => Matches(index, key))) ||
                await Rows(item.Name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
        }
        if (!counters.SequenceEqual(await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct))) throw Invalid();
        var finalHistory = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(107).ToListAsync(ct);
        if (!MigrationHistory.IsValid(finalHistory, Version) || finalHistory.Count != Version || finalHistory[^1]["state"] != "applying" ||
            !history.Take(105).SequenceEqual(finalHistory.Take(105))) throw Invalid();
    }
}
