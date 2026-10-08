using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Empty arrival-review stores. Never infers an ETA from invoice, payment or notice dates.</summary>
internal sealed class PurchaseArrivalMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 109;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        static InvalidOperationException Invalid() => new("Arrival migration requires exact predecessor history and empty compatible prospective stores");
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        foreach (var name in PurchaseArrivalSchemas.Collections) {
            await lease.RenewAsync(ct); var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            var indexes = definition is null ? [] : (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (definition is not null && (!AccountContactStorage.SafeDefinition(definition, PurchaseArrivalSchemas.Validator(name)) ||
                !AccountContactStorage.SafeIndexes(indexes, PurchaseArrivalSchemas.Keys(name)))) throw Invalid();
            if (await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"] != 0L)) throw Invalid();
        }
        foreach (var name in PurchaseArrivalSchemas.Collections) {
            await lease.RenewAsync(ct);
            if (await AccountContactStorage.DefinitionAsync(database, name, ct) is null)
                await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "validator", PurchaseArrivalSchemas.Validator(name) },
                    { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            foreach (var key in PurchaseArrivalSchemas.Keys(name).Where(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
                await Rows(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key,
                    new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        await PurchaseArrivalRecords.RequireStorageAsync(database, ct);
    }
}
