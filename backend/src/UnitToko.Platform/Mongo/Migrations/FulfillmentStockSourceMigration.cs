using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Exact91 predecessor, empty new owners only. Existing accepted setup/dispatch/receipt
/// histories are authenticated and retained, but legacy producer heads are never fabricated.
/// Those sources remain unavailable to this reader until a separately approved real handoff.</summary>
internal sealed class FulfillmentStockSourceMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 92;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Fulfillment source migration requires exact safe retained history and empty new owners");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying" || database.Client is not MongoClient client) throw Invalid();
        if (clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero) throw Invalid();
        var retained = await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct);
        var indexes = new Dictionary<string, BsonDocument[]>();
        foreach (var definition in retained.Where(row => row.GetValue("type", "") == "collection")) {
            var name = definition["name"].AsString;
            indexes.Add(name, (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray());
        }
        await FulfillmentStockSourceStorage.RequireRetainedAsync(database, ct);
        foreach (var name in StockSourceSetupSchemas.Collections) {
            await lease.RenewAsync(ct);
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { StockSourceSetupSchemas.Validator(name) })).Limit(1).AnyAsync(ct)) throw Invalid();
        }
        var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        foreach (var counter in counters)
            if (!counter.TryGetValue("_id", out var key) || !key.IsString || string.IsNullOrWhiteSpace(key.AsString) ||
                !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 1) throw Invalid();
        foreach (var name in StockSourceSetupSchemas.Collections.Where(name => name != "inventoryPoolRegistrations")
            .Concat(["shipments", ShipmentReceiptRecords.Receipts, ShipmentReceiptRecords.Challenges, DeliveryAttemptRecords.Attempts, "auditEvents"])) {
            var maximum = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
            var counter = counters.SingleOrDefault(row => row["_id"] == name);
            if (maximum is not null && (!maximum["_id"].IsInt64 || maximum["_id"].AsInt64 is <= 0 or >= long.MaxValue - 1 ||
                counter is null || counter["value"].AsInt64 < maximum["_id"].AsInt64)) throw Invalid();
        }
        var candidates = new List<(string Name, bool Exists, BsonDocument Validator, (string Name, BsonDocument Key, bool Unique)[] Missing)>();
        foreach (var name in FulfillmentStockSourceSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = retained.SingleOrDefault(row => row.GetValue("name", "") == name);
            var validator = FulfillmentStockSourceSchemas.Validator(name);
            if (definition is not null && !AccountContactStorage.SafeDefinition(definition, validator)) throw Invalid();
            if (await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            var required = FulfillmentStockSourceSchemas.Keys(name);
            BsonDocument[] existing = definition is null ? [] : indexes[name];
            if (definition is not null && !OrderStockSourceStorage.SafeIndexes(existing, required)) throw Invalid();
            var counter = counters.SingleOrDefault(row => row["_id"] == name);
            if (counter is not null && counter["value"] != 0L) throw Invalid();
            candidates.Add((name, definition is not null, definition is null ? validator : definition["options"]["validator"].AsBsonDocument,
                required.Where(key => !existing.Any(row => AccountContactStorage.Matches(row, key))).ToArray()));
        }
        // Bounded whole traversal, refusing excessive or missing authentic sources before any DDL.
        foreach (var name in new[] { "shipments", ShipmentReceiptRecords.Receipts })
            if (await Rows(name).CountDocumentsAsync(new BsonDocument(), cancellationToken: ct) > FulfillmentStockSourceRecords.MaximumRows) throw Invalid();
        var owner = new FulfillmentStockSourceEvents(database, new(database), clock);
        using (var tx = await client.StartSessionAsync(cancellationToken: ct)) {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
            await new FulfillmentStockSourceRecords(database).RequireRetainedRootsAsync(tx, ct);
            foreach (var (name, kind) in new[] { ("shipments", "dispatch"), (ShipmentReceiptRecords.Receipts, "receipt") }) {
                var sources = await Rows(name).Find(tx, new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(FulfillmentStockSourceRecords.MaximumRows + 1).ToListAsync(ct);
                if (sources.Count > FulfillmentStockSourceRecords.MaximumRows) throw Invalid();
                foreach (var source in sources) _ = await owner.ReadAsync(tx, kind, source["_id"].AsInt64, true, ct);
            }
            await tx.CommitTransactionAsync(ct);
        }
        await EvidenceMigration.EachAsync(Rows("stockSourceDecisions"), new BsonDocument("decision", "approve"), async row => {
            using var tx = await client.StartSessionAsync(cancellationToken: ct);
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
            _ = await owner.ReadAsync(tx, "setup", row["_id"].AsInt64, true, ct);
            await tx.CommitTransactionAsync(ct);
        }, lease, ct);
        foreach (var candidate in candidates) {
            await lease.RenewAsync(ct);
            if (!candidate.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", candidate.Name },
                { "validator", candidate.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in candidate.Missing) await Rows(candidate.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        foreach (var before in retained.Where(row => row.GetValue("type", "") == "collection")) {
            var name = before["name"].AsString;
            var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            if (before["info"]["uuid"] != after["info"]["uuid"] || before.GetValue("options", new BsonDocument()) != after.GetValue("options", new BsonDocument())) throw Invalid();
            var afterIndexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var added = candidates.SingleOrDefault(candidate => candidate.Name == name);
            var expectedCount = indexes[name].Length + (added.Name is null ? 0 : added.Missing.Length);
            if (afterIndexes.Count != expectedCount || indexes[name].Any(row => !afterIndexes.Any(other => other.Equals(row)))) throw Invalid();
        }
        var currentCounters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (counters.Count != currentCounters.Count || counters.Where((row, index) => row != currentCounters[index]).Any()) throw Invalid();
        await FulfillmentStockSourceStorage.RequireAsync(database, ct);
    }
}
