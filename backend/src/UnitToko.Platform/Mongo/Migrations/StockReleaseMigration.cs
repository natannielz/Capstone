using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Adds empty release/value owners. No existing lot, acquisition, quantity or value is inferred.</summary>
internal sealed class StockReleaseMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 103;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Release/value owners require exact102 history, empty prospective storage and preserved predecessor state");
    private static BsonDocument Validator(string name) => StockReleaseSchemas.Collections.Contains(name)
        ? StockReleaseSchemas.Validator(name) : LotValueStore.Validator(name);
    private static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => StockReleaseSchemas.Collections.Contains(name)
        ? StockReleaseSchemas.Keys(name) : LotValueStore.Keys(name);
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(104).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(102).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Invalid();
        var names = StockReleaseSchemas.Collections.Concat(LotValueStore.Collections).ToArray();
        var retained = (await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct))
            .Where(row => row.GetValue("type", "") == "collection" && !names.Contains(row["name"].AsString)).ToArray();
        var indexes = new Dictionary<string, BsonDocument[]>();
        foreach (var definition in retained) indexes.Add(definition["name"].AsString,
            (await (await Rows(definition["name"].AsString).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray());
        var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        foreach (var name in names) {
            var counter = counters.SingleOrDefault(row => row["_id"] == name);
            if (counter is not null && (!counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"] != 0L) ||
                (name is LotValueStore.HeldHeads or LotValueStore.AvailableHeads) && counter is not null) throw Invalid();
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            if (definition is not null) {
                var installed = (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
                if (!AccountContactStorage.SafeDefinition(definition, Validator(name)) || !OrderStockSourceStorage.SafeIndexes(installed, Keys(name))) throw Invalid();
            }
            if (await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
        }
        foreach (var name in names) {
            await lease.RenewAsync(ct);
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            if (definition is null) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "create", name }, { "validator", Validator(name) }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
            }, cancellationToken: ct);
            var installed = (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            foreach (var key in Keys(name).Where(key => !installed.Any(index => AccountContactStorage.Matches(index, key))))
                await Rows(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key,
                    new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        foreach (var before in retained) {
            await lease.RenewAsync(ct); var name = before["name"].AsString;
            var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            var currentIndexes = (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (before["info"]["uuid"] != after["info"]["uuid"] || before.GetValue("options", new BsonDocument()) != after.GetValue("options", new BsonDocument()) ||
                indexes[name].Length != currentIndexes.Length || indexes[name].Any(index => !currentIndexes.Contains(index))) throw Invalid();
        }
        var afterCounters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (!counters.SequenceEqual(afterCounters)) throw Invalid();
        await StockReleaseSchemas.RequireStorageAsync(database, ct); await LotValueStore.RequireStorageAsync(database, ct);
    }
}
