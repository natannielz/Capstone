using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Creates empty owner seal storage only. No seal is written here; owners seal after a full proof.</summary>
internal sealed class ProvenanceSealMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 115;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        static InvalidOperationException Invalid() => new("Provenance seal migration needs complete predecessor history and empty compatible storage");
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Invalid();
        await lease.RenewAsync(ct);
        foreach (var (name, kind) in ProvenanceSealSchemas.Stores) {
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            var indexes = definition is null ? [] : (await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (definition is not null && (!AccountContactStorage.SafeDefinition(definition, ProvenanceSealSchemas.Validator(kind)) ||
                !AccountContactStorage.SafeIndexes(indexes, ProvenanceSealSchemas.Keys))) throw Invalid();
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            await lease.RenewAsync(ct);
            if (definition is null) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name },
                { "validator", ProvenanceSealSchemas.Validator(kind) }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in ProvenanceSealSchemas.Keys.Where(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
                await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        await ProvenanceSealSchemas.RequireStorageAsync(database, ct);
    }
}
