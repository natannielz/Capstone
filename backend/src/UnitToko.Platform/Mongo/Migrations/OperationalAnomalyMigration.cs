using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Operations;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Empty advisory owners only. No grants, thresholds, business data or policy approvals are seeded.</summary>
internal sealed class OperationalAnomalyMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 107;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        static InvalidOperationException Invalid() => new("Anomaly migration requires exact predecessor history and empty compatible prospective owners");
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Invalid();
        // Preflight all candidates before any DDL; retained owner rows and metadata are never rewritten.
        foreach (var name in OperationalAnomalySchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            var indexes = definition is null ? [] : (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (definition is not null && (!AccountContactStorage.SafeDefinition(definition, OperationalAnomalySchemas.Validator(name)) ||
                !AccountContactStorage.SafeIndexes(indexes, OperationalAnomalySchemas.Keys(name)))) throw Invalid();
            if (await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"] != 0L)) throw Invalid();
        }
        foreach (var name in OperationalAnomalySchemas.Collections) {
            await lease.RenewAsync(ct);
            if (await AccountContactStorage.DefinitionAsync(database, name, ct) is null)
                await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "validator", OperationalAnomalySchemas.Validator(name) },
                    { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            foreach (var key in OperationalAnomalySchemas.Keys(name).Where(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
                await Rows(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key,
                    new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        await OperationalAnomalyRecords.RequireStorageAsync(database, ct);
    }
}
