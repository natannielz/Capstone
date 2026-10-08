using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Preserves every original assignment and reservation byte-for-byte. Adds no identifier data.</summary>
internal sealed class CatalogIdentifierMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 110;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Invalid();
        // Complete preflight before changing either validator or index. Only the known v11
        // primary assignment graph is eligible; newer owner commands cannot run mid-upgrade.
        var graph = new Dictionary<string, Dictionary<long, BsonDocument>>();
        foreach (var name in CatalogSchemas.Collections)
        {
            await lease.RenewAsync(ct);
            var rows = await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument()).ToListAsync(ct);
            if (rows.Any(row => !CatalogSchemas.IsValid(name, row))) throw Invalid();
            graph[name] = rows.ToDictionary(row => row["_id"].AsInt64);
        }
        await CatalogMigrationGraph.ValidateAsync(database, graph, ct);
        var observedIndexes = new Dictionary<string, List<BsonDocument>>();
        foreach (var name in new[] { CatalogIdentifierRecords.Collection, CatalogIdentifierRecords.History })
        {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            var validator = definition.GetValue("options", new BsonDocument()).AsBsonDocument.GetValue("validator", BsonNull.Value);
            var known = CatalogSchemas.SemanticEquals(validator, CatalogSchemas.Validator(name)) || CatalogSchemas.SemanticEquals(validator, CatalogIdentifierSchemas.Validator(name));
            if (!known || !AccountContactStorage.SafeDefinition(definition, validator.AsBsonDocument)) throw Invalid();
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var indexes = await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (!EligibleIndexes(name, indexes, CatalogSchemas.SemanticEquals(validator, CatalogIdentifierSchemas.Validator(name)))) throw Invalid();
            observedIndexes[name] = indexes;
            var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            var maximum = graph[name].Count == 0 ? 0 : graph[name].Keys.Max();
            if (counter is null ? maximum != 0 : !counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"].AsInt64 < maximum || counter["value"].AsInt64 == long.MaxValue) throw Invalid();
        }
        foreach (var name in new[] { CatalogIdentifierRecords.Collection, CatalogIdentifierRecords.History })
        {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "collMod", name }, { "validator", CatalogIdentifierSchemas.Validator(name) }, { "validationLevel", "strict" },
                { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
            }, cancellationToken: ct);
        }
        var assignments = database.GetCollection<BsonDocument>(CatalogIdentifierRecords.Collection).WithWriteConcern(WriteConcern.WMajority);
        var oldPrimary = observedIndexes[CatalogIdentifierRecords.Collection].SingleOrDefault(index => index.GetValue("name", "") == "ux_sku_primary");
        if (oldPrimary is not null && !oldPrimary.Contains("partialFilterExpression"))
        {
            await lease.RenewAsync(ct);
            await assignments.Indexes.DropOneAsync("ux_sku_primary", ct);
        }
        // If a crash occurred after the drop, the next preflight accepts precisely this known
        // absence. The permanent reservation index is never removed or weakened.
        foreach (var key in CatalogIdentifierRecords.IdentifierKeys)
        {
            await lease.RenewAsync(ct);
            var options = new CreateIndexOptions<BsonDocument> { Name = key.Name, Unique = key.Unique };
            if (key.Name == "ux_sku_primary") options.PartialFilterExpression = CatalogIdentifierRecords.PrimaryPredicate;
            await assignments.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key, options), cancellationToken: ct);
        }
        await CatalogIdentifierRecords.RequireStorageAsync(database, ct);
    }
    private static bool EligibleIndexes(string collection, IReadOnlyList<BsonDocument> indexes, bool currentValidator)
    {
        if (collection == CatalogIdentifierRecords.History) return CatalogIdentifierRecords.SafeIndexes(collection, indexes);
        var primary = CatalogIdentifierRecords.IdentifierKeys.Single(key => key.Name == "ux_sku_primary");
        var reserved = CatalogIdentifierRecords.IdentifierKeys.Single(key => key.Name == "ux_reserved_identifier");
        var browse = CatalogIdentifierRecords.IdentifierKeys.Single(key => key.Name == "ix_identifier_sku");
        return indexes.Select(index => index.GetValue("name", "").AsString).Distinct(StringComparer.Ordinal).Count() == indexes.Count &&
            indexes.Count is >= 2 and <= 4 && (currentValidator || indexes.Any(index => AccountContactStorage.Matches(index, primary))) &&
            indexes.Count(index => index.GetValue("name", "") == "_id_" &&
                AccountContactStorage.Matches(index, ("_id_", new BsonDocument("_id", 1), index.GetValue("unique", false).AsBoolean))) == 1 &&
            indexes.Count(index => CatalogIdentifierRecords.Matches(collection, index, reserved)) == 1 &&
            indexes.All(index => index.GetValue("name", "") == "_id_" || CatalogIdentifierRecords.Matches(collection, index, reserved) ||
                CatalogIdentifierRecords.Matches(collection, index, browse) || CatalogIdentifierRecords.Matches(collection, index, primary) ||
                AccountContactStorage.Matches(index, primary));
    }
    private static InvalidOperationException Invalid() => new("Identifier migration requires the complete original catalog assignment graph and exact known registry storage; reconcile inconsistent records or indexes before retrying");
}
