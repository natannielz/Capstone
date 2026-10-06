using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Adds an empty mechanical organization fence. No legacy source attribution,
/// business acceptance, active pool or changed predecessor document is produced.</summary>
internal sealed class StockSourceTopologyMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 98;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Stock topology requires exact97 predecessor and a safe empty fence owner");

    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(99).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version ||
            history.Take(97).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        var definitions = await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct);
        var indexes = new Dictionary<string, BsonDocument[]>();
        foreach (var row in definitions.Where(row => row.GetValue("type", "") == "collection"))
            indexes.Add(row["name"].AsString, (await (await Rows(row["name"].AsString).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray());
        var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        // Fence IDs reuse authentic organization IDs; there is no independent allocator.
        if (counters.Any(row => row.GetValue("_id", BsonNull.Value) == StockSourceTopologyFence.Collection)) throw Invalid();
        var existing = definitions.SingleOrDefault(row => row.GetValue("name", "") == StockSourceTopologyFence.Collection);
        var required = StockSourceTopologySchemas.Validator();
        if (existing is not null && !AccountContactStorage.SafeDefinition(existing, required)) throw Invalid();
        if (await Rows(StockSourceTopologyFence.Collection).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
        if (existing is not null && (indexes[StockSourceTopologyFence.Collection].Any(index =>
            index.GetValue("name", "") != "_id_" && index.GetValue("unique", false) == true) ||
            !indexes[StockSourceTopologyFence.Collection].Any(index => index.GetValue("name", "") == "_id_" && index["key"] == new BsonDocument("_id", 1)))) throw Invalid();
        await lease.RenewAsync(ct);
        if (existing is null) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "create", StockSourceTopologyFence.Collection }, { "validator", required }, { "validationLevel", "strict" },
            { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
        }, cancellationToken: ct);
        foreach (var before in definitions.Where(row => row.GetValue("type", "") == "collection"))
        {
            await lease.RenewAsync(ct);
            var name = before["name"].AsString;
            var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            var currentIndexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (before["info"]["uuid"] != after["info"]["uuid"] || before.GetValue("options", new BsonDocument()) != after.GetValue("options", new BsonDocument()) ||
                indexes[name].Length != currentIndexes.Count || indexes[name].Any(index => !currentIndexes.Any(current => current == index))) throw Invalid();
        }
        var currentCounters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (counters.Count != currentCounters.Count || counters.Where((row, index) => row != currentCounters[index]).Any()) throw Invalid();
        await InventoryRepository.RequireStorageAsync(database, StockSourceTopologyFence.Collection, required, new BsonDocument("_id", 1), ct);
    }
}
