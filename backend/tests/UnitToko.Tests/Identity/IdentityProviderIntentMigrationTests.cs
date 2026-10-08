using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class IdentityProviderIntentMigrationTests(MongoFixture fixture)
{
    private const string Collection = "identityProviderIntents";

    [Fact]
    public async Task Actual_106_upgrade_preserves_every_retained_row_and_predecessor_then_resumes_without_ddl()
    {
        await using var data = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 106);
        await ProduceAsync(data);
        var prefix = await PredecessorsAsync(data.Database); Assert.Equal(106, prefix.Count);
        Assert.All(prefix, row => Assert.Equal("applied", row["state"]));
        var others = await OthersAsync(data.Database);
        var rows = await RowsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
        var definition = await DefinitionAsync(data.Database);
        Assert.Equal(IdentitySchemas.Collections()[Collection], definition["options"]["validator"]);
        var gate = new MigrationGate(data.Database, data.Clock, 107);
        var lease = await gate.AcquireAsync(107, default) ?? throw new InvalidOperationException("Expected real v107 lease");
        await new IdentityProviderIntentMigration(data.Database).ApplyAsync(lease, default);
        var upgraded = await DefinitionAsync(data.Database);
        var expected = definition.DeepClone().AsBsonDocument;
        expected["options"]["validator"] = IdentityProviderIntentDispatcher.RequiredValidator();
        Assert.Equal(expected.ToBson(), upgraded.ToBson());
        Assert.Equal(Bytes(rows), Bytes(await RowsAsync(data.Database)));
        Assert.Equal(Bytes(indexes), Bytes(await IndexesAsync(data.Database)));
        Assert.Equal(others.ToBson(), (await OthersAsync(data.Database)).ToBson());
        Assert.Equal(Bytes(prefix), Bytes(await PredecessorsAsync(data.Database)));

        // Actual lease release/reacquisition rehearses DDL committed before deployment completion.
        await lease.ReleaseAsync();
        var resumed = await gate.AcquireAsync(107, default) ?? throw new InvalidOperationException("Expected interrupted lease");
        await new IdentityProviderIntentMigration(data.Database).ApplyAsync(resumed, default);
        Assert.Equal(upgraded.ToBson(), (await DefinitionAsync(data.Database)).ToBson());
        await resumed.CompleteAsync(default);
        Assert.True(await gate.IsCurrentAsync(default));
        Assert.Equal(Bytes(prefix), Bytes(await PredecessorsAsync(data.Database)));
        Assert.Equal(others.ToBson(), (await OthersAsync(data.Database)).ToBson());
        Assert.Equal(Bytes(rows), Bytes(await RowsAsync(data.Database)));
    }

    [Fact]
    public async Task Unknown_strengthened_validators_unsafe_indexes_and_malformed_retained_row_refuse_without_mutation()
    {
        await using var data = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 106);
        var intentId = await ProduceAsync(data);
        var db = data.Database; var canonical = IdentitySchemas.Collections()[Collection];
        foreach (var known in new[] { canonical, IdentityProviderIntentDispatcher.RequiredValidator() }) {
            await SetValidatorAsync(db, new BsonDocument("$and", new BsonArray { known,
                new BsonDocument("_id", new BsonDocument("$ne", 999_999L)) }));
            await RefusesAsync(data);
        }
        await SetValidatorAsync(db, canonical);
        await db.GetCollection<BsonDocument>(Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("neverExpiresAt", 1), new CreateIndexOptions { Name = "unsafe_expiry", ExpireAfter = TimeSpan.Zero }));
        await RefusesAsync(data);
        await db.GetCollection<BsonDocument>(Collection).Indexes.DropOneAsync("unsafe_expiry");
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", Collection },
            { "index", new BsonDocument { { "name", "ix_userId_kind_status" }, { "hidden", true } } } });
        await RefusesAsync(data);
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", Collection },
            { "index", new BsonDocument { { "name", "ix_userId_kind_status" }, { "hidden", false } } } });
        var intents = db.GetCollection<BsonDocument>(Collection);
        await intents.UpdateOneAsync(new BsonDocument("_id", intentId), Builders<BsonDocument>.Update.Set("securityVersion", 0L),
            new UpdateOptions { BypassDocumentValidation = true });
        await RefusesAsync(data);
    }

    [Fact]
    public async Task Capped_collection_keeps_real_106_history_and_exact_validator_but_refuses_upgrade()
    {
        await using var data = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 106);
        await ProduceAsync(data); var retained = await RowsAsync(data.Database);
        // Recreate only this private fixture collection; never manufacture applied migration records.
        await data.Database.DropCollectionAsync(Collection);
        await data.Database.CreateCollectionAsync(Collection, new CreateCollectionOptions<BsonDocument> {
            Capped = true, MaxSize = 1_048_576, Validator = IdentitySchemas.Collections()[Collection],
            ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error
        });
        await data.Database.GetCollection<BsonDocument>(Collection).InsertManyAsync(retained);
        await data.Database.GetCollection<BsonDocument>(Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { { "userId", 1 }, { "kind", 1 }, { "status", 1 } }, new CreateIndexOptions { Name = "ix_userId_kind_status" }));
        await RefusesAsync(data);
    }

    [Fact]
    public async Task Corrupted_actual_predecessor_history_refuses_before_validator_change()
    {
        await using var data = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 106);
        await ProduceAsync(data);
        var gate = new MigrationGate(data.Database, data.Clock, 107);
        var lease = await gate.AcquireAsync(107, default) ?? throw new InvalidOperationException("Expected real v107 lease");
        var history = data.Database.GetCollection<BsonDocument>("schemaMigrations");
        var actual = await history.Find(new BsonDocument("_id", 106)).SingleAsync();
        await history.UpdateOneAsync(new BsonDocument("_id", 106), Builders<BsonDocument>.Update.Set("state", "applying"));
        var before = await OwnerAsync(data.Database); var others = await OthersAsync(data.Database);
        try {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new IdentityProviderIntentMigration(data.Database).ApplyAsync(lease, default));
            Assert.Equal(before.ToBson(), (await OwnerAsync(data.Database)).ToBson());
            Assert.Equal(others.ToBson(), (await OthersAsync(data.Database)).ToBson());
        } finally {
            await history.ReplaceOneAsync(new BsonDocument("_id", 106), actual);
            await lease.ReleaseAsync();
        }
    }

    private static async Task<long> ProduceAsync(StoreTestData data)
    {
        var requester = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.RecoveryInitiate);
        var target = await data.AddActorAsync(2001, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        await data.Database.GetCollection<BsonDocument>("identities").UpdateManyAsync(new BsonDocument(),
            Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var unit = new MongoUnitOfWork((MongoClient)data.Database.Client);
        var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
        var sessions = new MongoIdentitySessionStore(data.Database, unit, data.Clock,
            new SessionPolicy("https://approved.example/", "approved-db", "v1"), protector);
        var producer = new AccountRecoveryService(data.Database, data.Store, new(data.Database), sessions, protector, data.Clock);
        var password = await producer.InitiateAsync(requester, target.UserId, 1, AccountRecoveryKind.Password,
            new AuthorizationCommandMetadata("migration-password", "Owner reviewed recovery", "migration-107"));
        await producer.InitiateAsync(requester, target.UserId, 2, AccountRecoveryKind.Factor,
            new AuthorizationCommandMetadata("migration-factor", "Owner reviewed factor recovery", "migration-107"));
        return password.Id;
    }

    private static async Task RefusesAsync(StoreTestData data)
    {
        var before = await OwnerAsync(data.Database); var others = await OthersAsync(data.Database);
        var prefix = await PredecessorsAsync(data.Database); Assert.Equal(106, prefix.Count);
        var gate = new MigrationGate(data.Database, data.Clock, 107);
        var lease = await gate.AcquireAsync(107, default) ?? throw new InvalidOperationException("Expected real migration lease");
        try {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new IdentityProviderIntentMigration(data.Database).ApplyAsync(lease, default));
            Assert.Equal(before.ToBson(), (await OwnerAsync(data.Database)).ToBson());
            Assert.Equal(others.ToBson(), (await OthersAsync(data.Database)).ToBson());
            Assert.Equal(Bytes(prefix), Bytes(await PredecessorsAsync(data.Database)));
        } finally { await lease.ReleaseAsync(); }
    }
    private static Task<BsonDocument> SetValidatorAsync(IMongoDatabase db, BsonDocument validator) => db.RunCommandAsync<BsonDocument>(
        new BsonDocument { { "collMod", Collection }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
    private static byte[] Bytes(IEnumerable<BsonDocument> rows) => new BsonDocument("rows", new BsonArray(rows)).ToBson();
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db) =>
        await AccountContactStorage.DefinitionAsync(db, Collection, default) ?? throw new InvalidOperationException("Expected owner");
    private static Task<List<BsonDocument>> RowsAsync(IMongoDatabase db) => db.GetCollection<BsonDocument>(Collection)
        .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
    private static async Task<List<BsonDocument>> IndexesAsync(IMongoDatabase db) =>
        (await (await db.GetCollection<BsonDocument>(Collection).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal).ToList();
    private static Task<List<BsonDocument>> PredecessorsAsync(IMongoDatabase db) => db.GetCollection<BsonDocument>("schemaMigrations")
        .Find(new BsonDocument("_id", new BsonDocument("$lte", 106))).Sort(new BsonDocument("_id", 1)).ToListAsync();
    private static async Task<BsonDocument> OwnerAsync(IMongoDatabase db) => new() {
        { "definition", await DefinitionAsync(db) }, { "indexes", new BsonArray(await IndexesAsync(db)) }, { "rows", new BsonArray(await RowsAsync(db)) }
    };
    private static async Task<BsonDocument> OthersAsync(IMongoDatabase db)
    {
        var result = new BsonDocument();
        foreach (var definition in (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; if (name is Collection or "schemaMigrations" or "migrationLocks") continue;
            result[name] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray((await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) },
                { "rows", new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
}
