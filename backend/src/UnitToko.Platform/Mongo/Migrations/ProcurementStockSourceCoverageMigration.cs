using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Empty causal owners, exact retained business history. Genuine legacy work
/// remains upgradeable but has no new pool membership/authority inferred by this step.</summary>
internal sealed class ProcurementStockSourceCoverageMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 100;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Procurement coverage requires authenticated retained history and empty safe new owners");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying" || database.Client is not MongoClient client || clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero) throw Invalid();
        await ProcurementStockSourceStorage.RequireAsync(database, ct);
        var retained = await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct);
        var indexes = new Dictionary<string, BsonDocument[]>(); var hashes = new Dictionary<string, string>();
        foreach (var definition in retained.Where(row => row.GetValue("type", "") == "collection")) {
            var name = definition["name"].AsString;
            indexes.Add(name, (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray());
            if (name != "schemaMigrations") hashes.Add(name, await RowHashAsync(name, lease, ct));
        }
        var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        foreach (var counter in counters)
            if (counter.ElementCount != 2 || !counter.GetValue("_id", BsonNull.Value).IsString || string.IsNullOrWhiteSpace(counter["_id"].AsString) ||
                !counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"].AsInt64 is < 0 or >= long.MaxValue - 1) throw Invalid();
        var targets = new List<(string Name, bool Exists, BsonDocument Validator, (string Name, BsonDocument Key, bool Unique)[] Missing)>();
        foreach (var name in ProcurementStockSourceCoverageSchemas.Collections) {
            await lease.RenewAsync(ct); var definition = retained.SingleOrDefault(row => row.GetValue("name", "") == name);
            var validator = ProcurementStockSourceCoverageSchemas.Validator(name); var required = ProcurementStockSourceCoverageSchemas.Keys(name);
            if (definition is not null && !AccountContactStorage.SafeDefinition(definition, validator) ||
                await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct) || counters.Any(row => row["_id"] == name && row["value"] != 0L)) throw Invalid();
            var existing = definition is null ? Array.Empty<BsonDocument>() : indexes[name];
            if (definition is not null && !OrderStockSourceStorage.SafeIndexes(existing, required)) throw Invalid();
            targets.Add((name, definition is not null, definition is null ? validator : definition["options"]["validator"].AsBsonDocument,
                required.Where(key => !existing.Any(row => AccountContactStorage.Matches(row, key))).ToArray()));
        }
        foreach (var (kind, name) in ProcurementStockSourceCoverage.Sources) {
            await lease.RenewAsync(ct);
            if (await Rows(name).CountDocumentsAsync(new BsonDocument(), cancellationToken: ct) > ProcurementStockSourceCoverage.MaximumRows ||
                await Rows(name).Find(new BsonDocument("$nor", new BsonArray { ProcurementStockSourceCoverage.SourceValidator(kind) })).Limit(1).AnyAsync(ct)) throw Invalid();
            var maximum = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
            var counter = counters.SingleOrDefault(row => row["_id"] == name);
            if (maximum is not null && (!maximum["_id"].IsInt64 || maximum["_id"].AsInt64 is <= 0 or >= long.MaxValue - 1 ||
                counter is null || counter["value"].AsInt64 < maximum["_id"].AsInt64)) throw Invalid();
        }
        using (var tx = await client.StartSessionAsync(cancellationToken: ct)) {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
            var owner = new ProcurementStockSourceCoverage(database, new(database), clock);
            // Maintained PO/request/arrival/inspection/private-pin/claim owners authenticate
            // their real command graph; unbound legacy records are not rebound or erased.
            var sources = await owner.ReadSourcesAsync(tx, null, ct);
            await owner.RequireEffectsMembershipAsync(tx, sources, ct);
            await new ProcurementStockSourceEvents(database, new(database), clock).PreflightAsync(tx, ct);
            await tx.CommitTransactionAsync(ct);
        }
        foreach (var (name, hash) in hashes) if (hash != await RowHashAsync(name, lease, ct)) throw Invalid();
        // Every candidate refusal precedes DDL. Existing stronger validators and safe
        // operator indexes survive an empty interrupted application.
        foreach (var target in targets) {
            await lease.RenewAsync(ct);
            if (!target.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", target.Name }, { "validator", target.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in target.Missing) await Rows(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        foreach (var before in retained.Where(row => row.GetValue("type", "") == "collection")) {
            var name = before["name"].AsString; var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            if (before["info"]["uuid"] != after["info"]["uuid"] || before.GetValue("options", new BsonDocument()) != after.GetValue("options", new BsonDocument())) throw Invalid();
            var afterIndexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var added = targets.SingleOrDefault(target => target.Name == name);
            if (afterIndexes.Count != indexes[name].Length + (added.Name is null ? 0 : added.Missing.Length) ||
                indexes[name].Any(row => !afterIndexes.Any(other => other == row)) ||
                name != "schemaMigrations" && hashes[name] != await RowHashAsync(name, lease, ct)) throw Invalid();
        }
        var afterCounters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (counters.Count != afterCounters.Count || counters.Where((row, index) => row != afterCounters[index]).Any()) throw Invalid();
        await ProcurementStockSourceCoverage.RequireStorageAsync(database, ct);
    }
    private async Task<string> RowHashAsync(string name, MigrationLease lease, CancellationToken ct) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var cursor = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) {
            await lease.RenewAsync(ct); foreach (var row in cursor.Current) hash.AppendData(row.ToBson());
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
