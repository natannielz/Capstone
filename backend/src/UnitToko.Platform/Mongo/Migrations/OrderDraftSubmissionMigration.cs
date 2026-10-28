using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>F-030: creates empty draft-to-order link storage only. Existing drafts and orders are untouched; a draft checked out
/// before this version stays listed, exactly as before, until the buyer discards it.</summary>
internal sealed class OrderDraftSubmissionMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 123;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        static InvalidOperationException Invalid() => new("Order draft submission migration needs complete predecessor history and empty compatible storage");
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Invalid();
        await lease.RenewAsync(ct);
        var name = OrderDraftSubmissionSchemas.Collection;
        var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
        var indexes = definition is null ? [] : (await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
        if (definition is not null && (!AccountContactStorage.SafeDefinition(definition, OrderDraftSubmissionSchemas.Validator()) ||
            !AccountContactStorage.SafeIndexes(indexes, OrderDraftSubmissionSchemas.Keys))) throw Invalid();
        if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
        await lease.RenewAsync(ct);
        if (definition is null) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name },
            { "validator", OrderDraftSubmissionSchemas.Validator() }, { "validationLevel", "strict" }, { "validationAction", "error" },
            { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        foreach (var key in OrderDraftSubmissionSchemas.Keys.Where(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
            await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        await OrderDraftSubmissionSchemas.RequireStorageAsync(database, ct);
    }
}
