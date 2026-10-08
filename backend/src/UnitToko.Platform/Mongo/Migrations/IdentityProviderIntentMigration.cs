using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Extends only the existing owner-controlled provider-intent validator. No rows, accounts,
/// counters, indexes or delivery effects are created, rewritten or inferred. Unknown stricter validators
/// are retained and refused; the exact installed target is accepted for an interrupted deployment.</summary>
internal sealed class IdentityProviderIntentMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 107;
    private const string Collection = "identityProviderIntents";
    private static InvalidOperationException Invalid() => new("Provider intent migration found incompatible retained storage");
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);

    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1))
            .Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version ||
            history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        await lease.RenewAsync(ct);
        var predecessor = IdentitySchemas.Collections()[Collection];
        var required = IdentityProviderIntentDispatcher.RequiredValidator();
        var definition = await AccountContactStorage.DefinitionAsync(database, Collection, ct) ?? throw Invalid();
        BsonDocument validator;
        try {
            if (!definition.TryGetValue("options", out var options) || !options.IsBsonDocument ||
                !options.AsBsonDocument.TryGetValue("validator", out var stored) || !stored.IsBsonDocument) throw Invalid();
            validator = stored.AsBsonDocument;
            // Intersection with an unknown blocked-only strengthening could silently disable new states;
            // replacing it would silently weaken the owner. Neither is an accepted migration.
            if (!validator.Equals(predecessor) && !validator.Equals(required) ||
                !AccountContactStorage.SafeDefinition(definition, validator)) throw Invalid();
            var indexes = await (await Rows(Collection).Indexes.ListAsync(ct)).ToListAsync(ct);
            (string Name, BsonDocument Key, bool Unique)[] keys = [
                ("ix_userId_kind_status", new BsonDocument { { "userId", 1 }, { "kind", 1 }, { "status", 1 } }, false)
            ];
            if (!AccountContactStorage.SafeIndexes(indexes, keys) ||
                keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key)))) throw Invalid();
        } catch (Exception error) when (error is InvalidCastException or FormatException or KeyNotFoundException) { throw Invalid(); }

        // Scan every retained record under both its exact current contract and the proposed contract.
        // This includes all blocked invitation/factor intents and any legitimate interrupted dispatch rows.
        if (await Rows(Collection).Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct) ||
            await Rows(Collection).Find(new BsonDocument("$nor", new BsonArray { required })).Limit(1).AnyAsync(ct)) throw Invalid();
        await lease.RenewAsync(ct);
        if (validator.Equals(required)) return;
        // Deployment execution owns quiescence; no API startup or request invokes this DDL.
        // All history, definition, index and retained-row checks finish before the sole owner mutation.
        await database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", Collection }, { "validator", required }, { "validationLevel", "strict" }, { "validationAction", "error" },
            { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
        }, cancellationToken: ct);
    }
}
