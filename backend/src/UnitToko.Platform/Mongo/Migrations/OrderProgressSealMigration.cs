using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Creates empty order-progress checkpoint seal storage only. Ordering seals after a full replay.</summary>
internal sealed class OrderProgressSealMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 117;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        static InvalidOperationException Invalid() => new("Order progress seal migration needs complete predecessor history and empty compatible storage");
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Invalid();
        await lease.RenewAsync(ct);
        var name = OrderProgressSealSchemas.Collection;
        var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
        var indexes = definition is null ? [] : (await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
        if (definition is not null && (!AccountContactStorage.SafeDefinition(definition, OrderProgressSealSchemas.Validator()) ||
            !AccountContactStorage.SafeIndexes(indexes, ProvenanceSealSchemas.Keys))) throw Invalid();
        if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
        await lease.RenewAsync(ct);
        if (definition is null) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name },
            { "validator", OrderProgressSealSchemas.Validator() }, { "validationLevel", "strict" }, { "validationAction", "error" },
            { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        foreach (var key in ProvenanceSealSchemas.Keys.Where(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
            await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        await OrderProgressSealSchemas.RequireStorageAsync(database, ct);
    }
}
