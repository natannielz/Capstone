using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Additive authenticated membership for original bound and unbound counts.
/// No predecessor observation, custom receipt, source history or physical state is rewritten.</summary>
internal sealed class InventoryCountCoverageMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 101;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Inventory count coverage needs exact100 history and authentic retained count membership");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying" || clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero) throw Invalid();
        await InventoryCountStockSourceStorage.RequireAsync(database, ct);
        var definitions = await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct);
        var indexes = new Dictionary<string, BsonDocument[]>(); var retainedHashes = new Dictionary<string, string>();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection")) {
            var name = definition["name"].AsString;
            indexes[name] = (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (name != "schemaMigrations" && !InventoryCountCoverageSchemas.Collections.Contains(name)) retainedHashes[name] = await RowHashAsync(name, lease, ct);
        }
        var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        foreach (var counter in counters)
            if (counter.ElementCount != 2 || !counter.GetValue("_id", BsonNull.Value).IsString ||
                !counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"].AsInt64 < 0 || counter["value"].AsInt64 >= long.MaxValue - 1 ||
                InventoryCountCoverageSchemas.Collections.Contains(counter["_id"].AsString) && counter["value"] != 0L) throw Invalid();
        var client = database.Client as MongoClient ?? throw Invalid(); var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database);
        var counts = new StockCountService(database, unit, ids, clock); var sources = new StockSourceSetupService(database, unit, ids, clock);
        var plan = await unit.ExecuteAsync(async (tx, token) => {
            var observations = await Rows(StockCountBson.Collection).Find(tx, new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(token);
            await InventoryCountCoverage.AuthenticateAsync(database, tx, counts, sources, observations, token, lease.RenewAsync);
            var members = observations.Select(row => InventoryCountCoverage.Member(row, "retained")).ToArray();
            var organizations = members.Select(row => row["organizationId"].AsInt64).ToHashSet();
            foreach (var pool in await Rows("inventoryPools").Find(tx, new BsonDocument()).ToListAsync(token)) {
                InventoryGraph.RequirePool(pool); organizations.Add(pool["location"]["organizationId"].AsInt64);
            }
            var heads = organizations.Order().Select(organization => InventoryCountCoverage.Head(organization,
                members.Where(member => member["organizationId"] == organization))).ToArray();
            return new Dictionary<string, BsonDocument[]> { [InventoryCountCoverage.Heads] = heads, [InventoryCountCoverage.Members] = members };
        }, ct);
        var targets = new List<(string Name, bool Exists, BsonDocument Validator, (string Name, BsonDocument Key, bool Unique)[] Missing)>();
        foreach (var name in InventoryCountCoverageSchemas.Collections) {
            await lease.RenewAsync(ct); var definition = definitions.SingleOrDefault(row => row.GetValue("name", "") == name);
            var required = InventoryCountCoverageSchemas.Validator(name); var keys = InventoryCountCoverageSchemas.Keys(name);
            if (definition is not null && !AccountContactStorage.SafeDefinition(definition, required)) throw Invalid();
            var existingIndexes = definition is null ? Array.Empty<BsonDocument>() : indexes[name];
            if (definition is not null && !OrderStockSourceStorage.SafeIndexes(existingIndexes, keys)) throw Invalid();
            var existingRows = await Rows(name).Find(new BsonDocument()).ToListAsync(ct);
            if (existingRows.Any(row => !plan[name].Any(expected => expected == row))) throw Invalid();
            targets.Add((name, definition is not null, definition is null ? required : definition["options"]["validator"].AsBsonDocument,
                keys.Where(key => !existingIndexes.Any(index => AccountContactStorage.Matches(index, key))).ToArray()));
        }
        // Authentication, target rows/indexes and all counter refusals precede target DDL.
        // Neutral unrelated legacy count organizations are retained, never globally refused.
        foreach (var target in targets) {
            await lease.RenewAsync(ct);
            if (!target.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", target.Name }, { "validator", target.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in target.Missing) await Rows(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key,
                new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        foreach (var (name, expectedRows) in plan) {
            foreach (var expected in expectedRows) {
                await lease.RenewAsync(ct);
                if (!await Rows(name).Find(new BsonDocument("_id", expected["_id"])).AnyAsync(ct))
                    await Rows(name).WithWriteConcern(WriteConcern.WMajority).InsertOneAsync(expected, cancellationToken: ct);
            }
            var actual = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
            if (actual.Count != expectedRows.Length || expectedRows.Any(expected => !actual.Any(row => row == expected))) throw Invalid();
        }
        foreach (var before in definitions.Where(row => row.GetValue("type", "") == "collection")) {
            var name = before["name"].AsString; var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            if (before["info"]["uuid"] != after["info"]["uuid"] || before.GetValue("options", new BsonDocument()) != after.GetValue("options", new BsonDocument())) throw Invalid();
            var actual = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct); var target = targets.SingleOrDefault(value => value.Name == name);
            if (actual.Count != indexes[name].Length + (target.Name is null ? 0 : target.Missing.Length) ||
                indexes[name].Any(index => !actual.Any(row => row == index))) throw Invalid();
            if (retainedHashes.TryGetValue(name, out var digest) && digest != await RowHashAsync(name, lease, ct)) throw Invalid();
        }
        var finalCounters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (counters.Count != finalCounters.Count || counters.Where((row, index) => row != finalCounters[index]).Any()) throw Invalid();
        var finalHistory = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(finalHistory, Version) || finalHistory.Count != Version || !history.Take(Version - 1).SequenceEqual(finalHistory.Take(Version - 1)) ||
            finalHistory[^1]["state"] != "applying") throw Invalid();
        await InventoryCountStockSourceStorage.RequireCoverageAsync(database, ct);
    }
    private async Task<string> RowHashAsync(string name, MigrationLease lease, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var cursor = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) {
            await lease.RenewAsync(ct); foreach (var row in cursor.Current) hash.AppendData(row.ToBson());
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
