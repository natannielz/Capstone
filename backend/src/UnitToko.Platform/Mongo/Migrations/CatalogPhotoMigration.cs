using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Adds the isolated primary-photo owner; never changes legacy imageAssetIds schemas.</summary>
internal sealed class CatalogPhotoMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 80;
    private sealed record Target(string Name, bool Exists, BsonDocument Validator,
        (string Name, BsonDocument Key, bool Unique)[] Missing);
    private sealed record ReadTarget(string Name, BsonDocument Before, BsonDocument After);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Catalog primary-photo migration found incompatible retained storage");

    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1))
            .Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version ||
            history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        var planned = new List<Target>();
        var readTargets = new List<ReadTarget>();
        foreach (var name in new[] { CatalogEvidenceReadSchemas.Leases, CatalogEvidenceReadSchemas.Audits }) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct))
                .SingleOrDefaultAsync(ct) ?? throw Invalid();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.GetValue("validationLevel", "") != "strict" || options.GetValue("validationAction", "") != "error" ||
                !options.TryGetValue("validator", out var raw) || !raw.IsBsonDocument) throw Invalid();
            var before = raw.AsBsonDocument;
            BsonDocument after;
            try { after = PaymentManualReviewMigration.ExtendValidator(before,
                CatalogEvidenceReadSchemas.PreviousValidator(name), CatalogEvidenceReadSchemas.Validator(name), name); }
            catch (InvalidOperationException) { throw Invalid(); }
            if (name == CatalogEvidenceReadSchemas.Leases &&
                await Rows(name).Find(new BsonDocument("target.kind", "CatalogSku")).Limit(1).AnyAsync(ct)) throw Invalid();
            // An installed financial row must still satisfy its original exact
            // branch. A pre-80 CatalogSku lease has no sanctioned writer.
            if (!before.Equals(after) && await Rows(name).Find(new BsonDocument("$nor", new BsonArray {
                    CatalogEvidenceReadSchemas.PreviousValidator(name) })).Limit(1).AnyAsync(ct)) throw Invalid();
            try { await InventoryRepository.RequireStorageAsync(database, name, before, EvidenceAccessSchemas.Key(name), ct); }
            catch (Exception error) when (error is InvalidOperationException or PersistenceConflictException or DomainValidationException) { throw Invalid(); }
            readTargets.Add(new(name, before, after));
        }
        foreach (var name in CatalogSchemas.Collections)
        {
            var existing = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct))
                .SingleOrDefaultAsync(ct) ?? throw Invalid();
            var options = existing.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (existing.GetValue("type", "") != "collection" || options.GetValue("validationLevel", "strict") != "strict" ||
                options.GetValue("validationAction", "error") != "error" || !options.TryGetValue("validator", out var validator) ||
                !validator.IsBsonDocument || !InventorySchemas.Contains(validator.AsBsonDocument, CatalogSchemas.Validator(name))) throw Invalid();
        }
        foreach (var name in CatalogPhotoSchemas.Collections)
        {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct))
                .SingleOrDefaultAsync(ct);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.GetValue("validationLevel", "strict") != "strict" ||
                options.GetValue("validationAction", "error") != "error" ||
                options.GetValue("validator", BsonNull.Value) != CatalogPhotoSchemas.Validator(name))) throw Invalid();
            var rows = Rows(name);
            // This is a new owner with no predecessor writer or sanctioned backfill.
            // Retained rows before installation need explicit owner review, not a guessed upgrade.
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            List<BsonDocument> indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (definition is not null && !indexes.Any(index => index.GetValue("name", "") == "_id_" &&
                index.GetValue("key", BsonNull.Value) == new BsonDocument("_id", 1))) throw Invalid();
            var required = CatalogPhotoSchemas.Keys(name);
            if (indexes.Any(index => index.GetValue("name", "") != "_id_" &&
                (!required.Any(key => key.Name == index["name"].AsString && key.Key == index["key"] &&
                    key.Unique == index.GetValue("unique", false).AsBoolean) || index.Contains("partialFilterExpression") ||
                    index.Contains("expireAfterSeconds")))) throw Invalid();
            var missing = required.Where(key => !indexes.Any(index => index.GetValue("name", "") == key.Name)).ToArray();
            planned.Add(new(name, definition is not null, CatalogPhotoSchemas.Validator(name), missing));
        }
        foreach (var name in new[] { CatalogPhotoRecords.Photos, CatalogPhotoRecords.Audits }) {
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 ||
                value.AsInt64 is < 0 or >= long.MaxValue - 1)) throw Invalid();
        }
        // All existing rows and conflicting definitions are examined before the first DDL change.
        foreach (var target in readTargets.Where(row => !row.Before.Equals(row.After))) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", target.Name },
                { "validator", target.After }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        }
        foreach (var target in planned)
        {
            await lease.RenewAsync(ct);
            if (!target.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", target.Name },
                { "validator", target.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var index in target.Missing)
                await Rows(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(index.Key, new CreateIndexOptions { Name = index.Name, Unique = index.Unique }),
                    cancellationToken: ct);
        }
    }

}
