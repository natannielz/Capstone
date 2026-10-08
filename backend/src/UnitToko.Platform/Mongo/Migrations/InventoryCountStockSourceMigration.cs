using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Empty count-source owners only. Existing counts, catalog provenance and their
/// exact custom receipts survive unchanged; no old count is assigned to a pool by this migration.</summary>
internal sealed class InventoryCountStockSourceMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 96;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    internal static InvalidOperationException Invalid() => new("Inventory count source migration requires exact safe retained history and empty new owners");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying" || database.Client is not MongoClient client || clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero) throw Invalid();
        await AfterSalesStockSourceStorage.RequireAsync(database, ct, ShipmentDispatchSchemas.LegacyValidator());
        await RequireCountStorageAsync(database, ct, requireBound: false);
        var retained = await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct);
        var indexes = new Dictionary<string, BsonDocument[]>();
        foreach (var definition in retained.Where(row => row.GetValue("type", "") == "collection"))
            indexes.Add(definition["name"].AsString, (await (await Rows(definition["name"].AsString).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray());
        var countTargets = new Dictionary<string, BsonDocument>();
        foreach (var name in new[] { StockCountBson.Collection, "idempotencyRecords" }) {
            var definition = retained.Single(row => row.GetValue("name", "") == name);
            var next = InventoryCountStockSourceSchemas.ExtendCount(name, definition["options"]["validator"].AsBsonDocument);
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { next })).Limit(1).AnyAsync(ct)) throw Invalid();
            countTargets.Add(name, next);
        }
        var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        foreach (var counter in counters)
            if (counter.ElementCount != 2 || !counter.GetValue("_id", BsonNull.Value).IsString || string.IsNullOrWhiteSpace(counter["_id"].AsString) ||
                !counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"].AsInt64 is < 0 or >= long.MaxValue - 1) throw Invalid();
        foreach (var name in new[] { StockCountBson.Collection, "auditEvents" }) {
            var maximum = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
            var counter = counters.SingleOrDefault(row => row["_id"] == name);
            // A sequence below actual retained IDs is unavailable. Never repair or advance it.
            if (maximum is not null && (!maximum["_id"].IsInt64 || maximum["_id"].AsInt64 is <= 0 or >= long.MaxValue - 1 ||
                counter is null || counter["value"].AsInt64 < maximum["_id"].AsInt64)) throw Invalid();
        }
        var candidates = new List<(string Name, bool Exists, BsonDocument Validator, (string Name, BsonDocument Key, bool Unique)[] Missing)>();
        foreach (var name in InventoryCountStockSourceSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = retained.SingleOrDefault(row => row.GetValue("name", "") == name);
            var validator = InventoryCountStockSourceSchemas.Validator(name);
            if (definition is not null && !AccountContactStorage.SafeDefinition(definition, validator) ||
                await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            var counter = counters.SingleOrDefault(row => row["_id"] == name);
            if (counter is not null && counter["value"] != 0L) throw Invalid();
            var required = InventoryCountStockSourceSchemas.Keys(name);
            BsonDocument[] existing = definition is null ? [] : indexes[name];
            if (definition is not null && !OrderStockSourceStorage.SafeIndexes(existing, required)) throw Invalid();
            candidates.Add((name, definition is not null, definition is null ? validator : definition["options"]["validator"].AsBsonDocument,
                required.Where(key => !existing.Any(row => AccountContactStorage.Matches(row, key))).ToArray()));
        }
        var retainedRows = new Dictionary<string, string>();
        foreach (var name in indexes.Keys.Where(name => name != "schemaMigrations"))
            retainedRows.Add(name, await RowHashAsync(name, lease, ct));
        // This is the maintained old fingerprint/body/catalog/audit/custom-receipt graph,
        // including reverse orphan and duplicate-link checks. It creates no association.
        foreach (var name in StockCountSchemas.Collections)
            if (await Rows(name).Find(new BsonDocument("$and", new BsonArray { StockCountSchemas.Selection(name),
                new BsonDocument("$nor", new BsonArray { StockCountSchemas.Validator(name) }) })).Limit(1).AnyAsync(ct)) throw Invalid();
        await StockCountMigrationGraph.ValidateAsync(database, lease, ct);
        await EvidenceMigration.EachAsync(Rows("stockSourceDecisions"), new BsonDocument("decision", "approve"), async row => {
            using var tx = await client.StartSessionAsync(cancellationToken: ct);
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
            _ = await new OrderStockSourceRecords(database).SetupAsync(tx, row["_id"].AsInt64, true, ct);
            await tx.CommitTransactionAsync(ct);
        }, lease, ct);
        foreach (var (name, digest) in retainedRows)
            if (digest != await RowHashAsync(name, lease, ct)) throw Invalid();
        // All predecessor shapes, actual records/receipts, sequence bounds and candidates pass
        // BEFORE the first target DDL. Existing operator conjunctions remain byte-exact.
        foreach (var (name, validator) in countTargets) {
            await lease.RenewAsync(ct);
            var previous = retained.Single(row => row.GetValue("name", "") == name)["options"]["validator"].AsBsonDocument;
            if (previous != validator) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "collMod", name }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        }
        foreach (var candidate in candidates) {
            await lease.RenewAsync(ct);
            if (!candidate.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", candidate.Name },
                { "validator", candidate.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in candidate.Missing) await Rows(candidate.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        foreach (var before in retained.Where(row => row.GetValue("type", "") == "collection")) {
            var name = before["name"].AsString; var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            var expectedOptions = before.GetValue("options", new BsonDocument()).DeepClone().AsBsonDocument;
            if (countTargets.TryGetValue(name, out var next)) expectedOptions["validator"] = next;
            if (before["info"]["uuid"] != after["info"]["uuid"] || expectedOptions != after.GetValue("options", new BsonDocument())) throw Invalid();
            var actual = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct); var added = candidates.SingleOrDefault(candidate => candidate.Name == name);
            if (actual.Count != indexes[name].Length + (added.Name is null ? 0 : added.Missing.Length) || indexes[name].Any(row => !actual.Any(other => other == row))) throw Invalid();
            if (name != "schemaMigrations" && retainedRows[name] != await RowHashAsync(name, lease, ct)) throw Invalid();
        }
        var actualCounters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (!counters.SequenceEqual(actualCounters)) throw Invalid();
        var finalHistory = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(finalHistory, Version) || finalHistory.Count != Version ||
            !history.Take(Version - 1).SequenceEqual(finalHistory.Take(Version - 1)) || finalHistory[^1]["state"] != "applying") throw Invalid();
        await RequireCountStorageAsync(database, ct);
        foreach (var name in InventoryCountStockSourceSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            if (!AccountContactStorage.SafeDefinition(definition, InventoryCountStockSourceSchemas.Validator(name)) ||
                await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            var actual = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct); var required = InventoryCountStockSourceSchemas.Keys(name);
            if (!OrderStockSourceStorage.SafeIndexes(actual, required) || required.Any(key => !actual.Any(row => AccountContactStorage.Matches(row, key)))) throw Invalid();
        }
    }
    private async Task<string> RowHashAsync(string name, MigrationLease lease, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var cursor = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) {
            await lease.RenewAsync(ct);
            foreach (var row in cursor.Current) hash.AppendData(row.ToBson());
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    internal static async Task RequireCountStorageAsync(IMongoDatabase database, CancellationToken ct, bool requireBound = true)
    {
        foreach (var name in StockCountSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            var successor = InventoryCountStockSourceSchemas.CountValidator(name);
            if (!AccountContactStorage.SafeDefinition(definition, successor) &&
                (requireBound || !AccountContactStorage.SafeDefinition(definition, StockCountSchemas.Validator(name)))) throw Invalid();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            bool Safe(BsonDocument index) => !index.GetValue("sparse", false).AsBoolean && !index.GetValue("hidden", false).AsBoolean &&
                !index.Contains("expireAfterSeconds") && (!index.TryGetValue("collation", out var collation) ||
                    collation.IsBsonDocument && collation.AsBsonDocument == new BsonDocument("locale", "simple"));
            if (!indexes.Any(index => index.GetValue("name", "") == "_id_" && index.GetValue("key", BsonNull.Value) == new BsonDocument("_id", 1) && Safe(index)) ||
                indexes.Any(index => index.Contains("expireAfterSeconds"))) throw Invalid();
            if (name == StockCountBson.Collection) {
                var key = new BsonDocument { { "actorId", 1 }, { "requestKey", 1 } };
                bool Request(BsonDocument index) => index.GetValue("key", BsonNull.Value) == key && index.GetValue("unique", false).AsBoolean && Safe(index) && !index.Contains("partialFilterExpression");
                if (!indexes.Any(Request) || indexes.Any(index => !Safe(index) || index.Contains("partialFilterExpression") ||
                    index.GetValue("unique", false).AsBoolean && index.GetValue("name", "") != "_id_" && !Request(index))) throw Invalid();
                var location = new BsonDocument { { "location.organizationId", 1 }, { "location.shopId", 1 },
                    { "location.channelId", 1 }, { "location.warehouseId", 1 }, { "_id", 1 } };
                if (!indexes.Any(index => index.GetValue("key", BsonNull.Value) == location && !index.GetValue("unique", false).AsBoolean && Safe(index) && !index.Contains("partialFilterExpression"))) throw Invalid();
            } else if (name == "auditEvents") {
                if (!indexes.Any(index => index.GetValue("key", BsonNull.Value) == new BsonDocument("observationId", 1) &&
                    index.GetValue("unique", false).AsBoolean && Safe(index) &&
                    CatalogSchemas.SemanticEquals(index.GetValue("partialFilterExpression", BsonNull.Value), new BsonDocument("kind", StockCountBson.AuditKind)))) throw Invalid();
            } else {
                var key = new BsonDocument { { "actorId", 1 }, { "operation", 1 }, { "key", 1 } };
                var legacy = new BsonDocument("$and", new BsonArray(key.Names.Select(field => new BsonDocument(field, new BsonDocument("$exists", true)))));
                if (!indexes.Any(index => index.GetValue("key", BsonNull.Value) == key && index.GetValue("unique", false).AsBoolean && Safe(index) &&
                    (!index.TryGetValue("partialFilterExpression", out var partial) || CatalogSchemas.SemanticEquals(partial, legacy)))) throw Invalid();
            }
            var required = requireBound ? successor : StockCountSchemas.Validator(name);
            if (await database.GetCollection<BsonDocument>(name).Find(new BsonDocument("$and", new BsonArray { StockCountSchemas.Selection(name),
                new BsonDocument("$nor", new BsonArray { required }) })).Limit(1).AnyAsync(ct)) throw Invalid();
        }
    }
}
