using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Adds self-declared contact details without importing provider facts or changing existing owners.</summary>
internal sealed class AccountContactMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 82;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Account contact migration found incompatible retained storage");

    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1))
            .Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version ||
            history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        // These predecessor owners remain immutable; no self-declared field can weaken verified identity/session storage.
        foreach (var name in new[] { "identities", "identitySessions", "accountProfiles" })
        {
            await lease.RenewAsync(ct);
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            if (!AccountContactStorage.SafeDefinition(definition, IdentitySchemas.Collections()[name])) throw Invalid();
            if (name == "identities" && !AccountContactStorage.SafeDefinition(definition, AccountContactSchemas.PriorIdentityValidator())) throw Invalid();
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { IdentitySchemas.Collections()[name] }))
                .Limit(1).AnyAsync(ct)) throw Invalid();
        }
        var changes = new List<(string Name, bool Exists, (string Name, BsonDocument Key, bool Unique)[] Missing)>();
        foreach (var name in AccountContactSchemas.Collections)
        {
            await lease.RenewAsync(ct);
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            if (definition is not null && !AccountContactStorage.SafeDefinition(definition, AccountContactSchemas.Validator(name))) throw Invalid();
            // No pre-82 writer or sanctioned backfill exists, including an apparently deleted original owner.
            if (await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            List<BsonDocument> indexes = definition is null ? [] : await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var required = AccountContactSchemas.Keys(name);
            if (definition is not null && !AccountContactStorage.SafeIndexes(indexes, required)) throw Invalid();
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 != 0)) throw Invalid();
            changes.Add((name, definition is not null, required.Where(key => !indexes.Any(row => AccountContactStorage.Matches(row, key))).ToArray()));
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("authorizationHistory", "operation"),
            ("auditEvents", "operation"), ("outboxEvents", "kind") })
            if (await Rows(table).Find(new BsonDocument(field, new BsonDocument("$in", new BsonArray {
                AccountContactRecords.Operation, "identity.accountcontact.save" }))).Limit(1).AnyAsync(ct)) throw Invalid();
        // All candidates pass before any collection or index is created.
        foreach (var change in changes)
        {
            await lease.RenewAsync(ct);
            if (!change.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "create", change.Name }, { "validator", AccountContactSchemas.Validator(change.Name) },
                { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in change.Missing)
                await Rows(change.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
    }
}
