using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Documents;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Adds a separate image revalidation owner. The first scan, lifecycle and existing leases are not rewritten.</summary>
internal sealed class EvidenceImageRevalidationMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 81;
    private IMongoCollection<BsonDocument> Rows(string name) =>
        database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Image revalidation migration found incompatible retained storage");

    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1))
            .Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version ||
            history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        try { await CatalogPhotoStorage.RequireAsync(database, ct); }
        catch (Exception error) when (error is InvalidOperationException or PersistenceConflictException or DomainValidationException or AuthorizationDeniedException) { throw Invalid(); }

        var changes = new List<(string Name, bool Exists, (string Name, BsonDocument Key, bool Unique)[] Missing)>();
        foreach (var name in EvidenceImageRevalidationSchemas.Collections)
        {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions {
                Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && (definition.GetValue("type", "") != "collection" ||
                options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") ||
                options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.GetValue("validationLevel", "") != "strict" || options.GetValue("validationAction", "") != "error" ||
                options.GetValue("validator", BsonNull.Value) != EvidenceImageRevalidationSchemas.Validator(name))) throw Invalid();
            // A new owner has no sanctioned pre-81 writer or historical backfill.
            if (await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            List<BsonDocument> indexes = definition is null ? [] : await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (definition is not null && !indexes.Any(row => row.GetValue("name", "") == "_id_" &&
                row.GetValue("key", BsonNull.Value) == new BsonDocument("_id", 1))) throw Invalid();
            var required = EvidenceImageRevalidationSchemas.Keys(name);
            if (indexes.Any(row => row.GetValue("name", "") != "_id_" &&
                (!required.Any(key => row.GetValue("name", "") == key.Name && row.GetValue("key", BsonNull.Value) == key.Key &&
                    row.GetValue("unique", false).AsBoolean == key.Unique) || row.Contains("partialFilterExpression") ||
                 row.Contains("expireAfterSeconds")))) throw Invalid();
            var missing = required.Where(key => !indexes.Any(row => row.GetValue("name", "") == key.Name)).ToArray();
            changes.Add((name, definition is not null, missing));
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            // No pre-81 owner exists. A positive allocator is retained evidence of
            // a deleted owner, even when every business row has disappeared.
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 ||
                value.AsInt64 != 0L)) throw Invalid();
        }
        foreach (var change in changes)
        {
            await lease.RenewAsync(ct);
            if (!change.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "create", change.Name }, { "validator", EvidenceImageRevalidationSchemas.Validator(change.Name) },
                { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var index in change.Missing)
                await Rows(change.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(index.Key, new CreateIndexOptions { Name = index.Name, Unique = index.Unique }),
                    cancellationToken: ct);
        }
    }
}
