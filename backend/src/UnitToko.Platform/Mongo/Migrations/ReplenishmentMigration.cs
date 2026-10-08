using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Only the empty advisory owner. No policy values, data, grants or supplier arrivals are seeded.</summary>
internal sealed class ReplenishmentMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 108;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var rows = database.GetCollection<BsonDocument>(ReplenishmentRecords.Collection).WithReadConcern(ReadConcern.Majority);
        static InvalidOperationException Invalid() => new("Replenishment migration needs complete predecessor history and empty compatible advisory storage");
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Invalid();
        await lease.RenewAsync(ct);
        var definition = await AccountContactStorage.DefinitionAsync(database, ReplenishmentRecords.Collection, ct);
        var indexes = definition is null ? [] : (await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
        if (definition is not null && (!AccountContactStorage.SafeDefinition(definition, ReplenishmentSchemas.Validator()) ||
            !AccountContactStorage.SafeIndexes(indexes, ReplenishmentSchemas.Keys))) throw Invalid();
        if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
        var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument("_id", ReplenishmentRecords.Collection)).SingleOrDefaultAsync(ct);
        if (counter is not null && (!counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"] != 0L)) throw Invalid();
        await lease.RenewAsync(ct);
        if (definition is null) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", ReplenishmentRecords.Collection },
            { "validator", ReplenishmentSchemas.Validator() }, { "validationLevel", "strict" }, { "validationAction", "error" },
            { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        foreach (var key in ReplenishmentSchemas.Keys.Where(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
            await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        await ReplenishmentRecords.RequireStorageAsync(database, ct);
    }
}
