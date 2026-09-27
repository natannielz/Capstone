using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class IdentityMigrationTests(MongoFixture fixture)
{
    private readonly IClock clock = new SystemClock();

    [Fact]
    public async Task V3_upgrade_keeps_approved_accounts_and_history_and_raises_reserved_sequences_without_seeding()
    {
        await InDatabase(async database =>
        {
            await ApplyV3(database);
            const long imported = 9_007_199_254_740_993L;
            var identity = Identity(imported);
            await database.GetCollection<BsonDocument>("identities").InsertOneAsync(identity);
            var invitation = Invitation(1L, imported + 5);
            await database.GetCollection<BsonDocument>("accountInvitations").InsertOneAsync(invitation);
            var historyBefore = await History(database);
            var originalRules = await Validator(database, "identities");
            var stronger = new BsonDocument("$and", new BsonArray { originalRules, new BsonDocument("subject", new BsonDocument("$ne", "forbidden-fixture")) });
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "identities" }, { "validator", stronger } });

            var migrations = new CollectionMigrations(database, clock);
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            Assert.Equal(historyBefore, (await History(database)).Take(3));
            Assert.Equal(identity, await database.GetCollection<BsonDocument>("identities").Find(new BsonDocument("_id", imported)).SingleAsync());
            Assert.Equal(invitation, await database.GetCollection<BsonDocument>("accountInvitations").Find(new BsonDocument("_id", 1L)).SingleAsync());
            Assert.Equal(imported + 6, (await new IdAllocator(database).NextAsync("identities")).Value);
            Assert.Equal(801L, (await new IdAllocator(database).NextAsync("memberships")).Value);
            Assert.Equal(901L, (await new IdAllocator(database).NextAsync("authorizationGrants")).Value);
            foreach (var name in new[] { "identitySessions", "oidcTransactions", "accountProfiles", "identityProviderIntents", "identityReadEvents" })
                Assert.Equal(0, await database.GetCollection<BsonDocument>(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            var denied = Identity(imported + 1); denied["subject"] = "forbidden-fixture";
            await Invalid(database, "identities", denied);
            var finalHistory = await History(database);
            var finalRules = await Validator(database, "identities");
            await migrations.ApplyAsync();
            Assert.Equal(finalHistory, await History(database));
            Assert.Equal(finalRules, await Validator(database, "identities"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preflight_rejects_invalid_epoch_or_duplicate_reserved_account_before_changing_validators(bool duplicate)
    {
        await InDatabase(async database =>
        {
            await ApplyV3(database);
            var original = await Validator(database, "identities");
            if (duplicate)
                await database.GetCollection<BsonDocument>("accountInvitations").InsertManyAsync([Invitation(1L, 10), Invitation(2L, 10)]);
            else
            {
                var row = Identity(10); row["securityVersion"] = 1;
                await database.GetCollection<BsonDocument>("identities").InsertOneAsync(row);
            }
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(database, clock).ApplyAsync());
            Assert.Contains(duplicate ? "duplicate userId" : "identities", failure.Message);
            Assert.Equal(original, await Validator(database, "identities"));
            Assert.DoesNotContain("identitySessions", await (await database.ListCollectionNamesAsync()).ToListAsync());
            Assert.False(await new CollectionMigrations(database, clock).IsCurrentAsync());
        });
    }

    [Fact]
    public async Task Session_schema_requires_exact_numbers_and_lifecycle_evidence_and_secrets_stay_out_of_extra_fields()
    {
        await InDatabase(async database =>
        {
            await new CollectionMigrations(database, clock).ApplyAsync();
            await database.GetCollection<BsonDocument>("identitySessions").InsertOneAsync(Session('a'));
            foreach (var mutate in new Action<BsonDocument>[]
            {
                row => row["userId"] = 1,
                row => row["securityVersion"] = 0L,
                row => row["grantVersion"] = 0L,
                row => row["schemaVersion"] = 1L,
                row => row["status"] = "revoked",
                row => row["accessToken"] = "must-not-persist-plaintext",
                row => row["factorType"] = "remembered-browser",
                row => row["providerAuthenticatedAt"] = "timestamp",
                row => row["familyId"] = new string('A', 64)
            })
            {
                var row = Session('b'); mutate(row);
                await Invalid(database, "identitySessions", row);
            }
            var revoked = Session('c'); revoked["status"] = "revoked"; revoked["revokedAt"] = clock.UtcNow.UtcDateTime;
            revoked["revocationReason"] = "owner_logout";
            await database.GetCollection<BsonDocument>("identitySessions").InsertOneAsync(revoked);
            var indexes = await (await database.GetCollection<BsonDocument>("identitySessions").Indexes.ListAsync()).ToListAsync();
            Assert.Contains(indexes, row => row["key"].AsBsonDocument.Contains("absoluteExpiresAt") && row["expireAfterSeconds"].ToInt64() == 0);
            var invitationIndexes = await (await database.GetCollection<BsonDocument>("accountInvitations").Indexes.ListAsync()).ToListAsync();
            Assert.Contains(invitationIndexes, row => row["key"].AsBsonDocument.Contains("secretHash") && row["unique"].AsBoolean && row.Contains("partialFilterExpression"));
            var profileIndexes = await (await database.GetCollection<BsonDocument>("accountProfiles").Indexes.ListAsync()).ToListAsync();
            Assert.DoesNotContain(profileIndexes, row => row["key"].AsBsonDocument.Contains("verifiedEmail") && row.GetValue("unique", false).AsBoolean);
        });
    }

    private async Task InDatabase(Func<IMongoDatabase, Task> action)
    {
        var name = "ut_identity_migration_" + Guid.NewGuid().ToString("N");
        try { await action(fixture.Client.GetDatabase(name)); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private async Task ApplyV3(IMongoDatabase database)
    {
        var durable = database.WithWriteConcern(WriteConcern.WMajority);
        IMigrationStep[] steps = [new FoundationMigration(durable), new AuthorizationMigration(durable), new AuthorizationLifecycleMigration(durable)];
        var gate = new MigrationGate(database, clock, 3);
        foreach (var step in steps)
        {
            var lease = await gate.AcquireAsync(step.Version, CancellationToken.None);
            await step.ApplyAsync(lease!, CancellationToken.None);
            await lease!.CompleteAsync(CancellationToken.None);
        }
    }
    private static Task<List<BsonDocument>> History(IMongoDatabase database) => database.GetCollection<BsonDocument>("schemaMigrations")
        .Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
    private static async Task<BsonDocument> Validator(IMongoDatabase database, string name) =>
        (await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync())["options"]["validator"].AsBsonDocument;
    private static async Task Invalid(IMongoDatabase database, string name, BsonDocument row)
    {
        var error = await Assert.ThrowsAsync<MongoWriteException>(() => database.GetCollection<BsonDocument>(name).InsertOneAsync(row));
        Assert.Equal(121, error.WriteError.Code);
    }
    private static BsonDocument Identity(long id) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "issuer", "https://fixture.example/" }, { "subject", "subject-" + id },
        { "status", "active" }, { "grantVersion", 1L }, { "fence", 0L }
    };
    private BsonDocument Invitation(long id, long userId) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "userId", userId }, { "membershipId", 800L }, { "grantId", 900L },
        { "intendedEmail", "fixture@example.test" }, { "scope", new BsonDocument("kind", "system") },
        { "role", new BsonDocument { { "templateId", "finance" }, { "templateVersion", 1 },
            { "permissions", new BsonArray() }, { "delegablePermissions", new BsonArray() } } },
        { "requestedBy", 2L }, { "requesterGrantVersion", 1L }, { "requesterSecurityVersion", 1L },
        { "providerIssuer", "https://fixture.example/" }, { "providerConnectionId", "fixture-connection" },
        { "reason", "synthetic reviewed invitation" }, { "createdAt", clock.UtcNow.UtcDateTime },
        { "expiresAt", clock.UtcNow.AddDays(1).UtcDateTime }, { "accessExpiresAt", BsonNull.Value },
        { "status", "pending" }, { "version", 1L }
    };
    private BsonDocument Session(char id) => new()
    {
        { "_id", new string(id, 64) }, { "schemaVersion", 1 }, { "familyId", new string(id, 64) }, { "userId", 1L },
        { "issuer", "https://fixture.example/" }, { "subject", "fixture" }, { "connectionId", "fixture-connection" },
        { "grantVersion", 1L }, { "securityVersion", 1L }, { "status", "active" }, { "version", 1L }, { "fence", 0L },
        { "createdAt", clock.UtcNow.UtcDateTime }, { "lastUsedAt", clock.UtcNow.UtcDateTime },
        { "idleExpiresAt", clock.UtcNow.AddMinutes(30).UtcDateTime }, { "absoluteExpiresAt", clock.UtcNow.AddHours(8).UtcDateTime },
        { "providerAuthenticatedAt", clock.UtcNow.UtcDateTime }, { "factorCompletedAt", clock.UtcNow.UtcDateTime },
        { "factorType", "otp" }, { "assurancePolicyVersion", "fixture-v1" }, { "protectedPayload", "opaque-protected-fixture" }
    };
}
