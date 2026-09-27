using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class MigrationUpgradeTests(MongoFixture fixture)
{
    private readonly IClock clock = new SystemClock();

    [Fact]
    public async Task Actual_v1_database_upgrades_without_replaying_or_rewriting_v1()
    {
        var name = "ut_auth_upgrade_" + Guid.NewGuid().ToString("N");
        var database = fixture.Client.GetDatabase(name);
        try
        {
            await ApplyVersionOneAsync(database);
            var before = await ReadVersionOneAsync(database);
            await ProtectOrdersAsync(database);
            var migrations = new CollectionMigrations(database, clock);
            Assert.False(await migrations.IsCurrentAsync());
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            Assert.Equal(before, await ReadVersionOneAsync(database));
            await AssertOrdersStillProtectedAsync(database);
            var history = await database.GetCollection<BsonDocument>("schemaMigrations")
                .Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
            Assert.Equal(Enumerable.Range(1, CollectionMigrations.CurrentVersion), history.Select(item => item["_id"].AsInt32));
            Assert.All(history, item => Assert.Equal("applied", item["state"].AsString));

            // The old deployment refuses this database even though v1 aggregate formats stay valid.
            var oldReleaseGate = new MigrationGate(database, clock, 1);
            Assert.False(await oldReleaseGate.IsCurrentAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                oldReleaseGate.AcquireAsync(1, CancellationToken.None));
            await database.GetCollection<BsonDocument>("orders").InsertOneAsync(new BsonDocument
                { { "schemaVersion", 1 }, { "protected", true } });
            await Assert.ThrowsAsync<MongoWriteException>(() =>
                database.GetCollection<BsonDocument>("idempotencyRecords").InsertOneAsync(new BsonDocument
                {
                    { "schemaVersion", 2 }, { "actorId", 1L }, { "operation", "test" }, { "key", "key" },
                    { "bodyHash", new string('a', 64) }, { "result", new BsonDocument() },
                    { "createdAt", clock.UtcNow.UtcDateTime }
                }));
        }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }

    [Fact]
    public async Task Upgrade_preserves_stronger_rules_on_the_identity_collection_it_changes()
    {
        var name = "ut_auth_stricter_" + Guid.NewGuid().ToString("N");
        var database = fixture.Client.GetDatabase(name);
        try
        {
            await ApplyVersionOneAsync(database);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "collMod", "identities" },
                { "validator", new BsonDocument("$jsonSchema", new BsonDocument
                    { { "bsonType", "object" }, { "required", new BsonArray { "reviewMarker" } } }) },
                { "validationLevel", "strict" }, { "validationAction", "error" }
            });
            await new CollectionMigrations(database, clock).ApplyAsync();
            var identity = new BsonDocument
            {
                { "_id", 1L }, { "schemaVersion", 1 }, { "issuer", "fixture" }, { "subject", "reviewed" },
                { "status", "active" }, { "grantVersion", 1L }, { "fence", 0L }
            };
            var collection = database.GetCollection<BsonDocument>("identities");
            await Assert.ThrowsAsync<MongoWriteException>(() => collection.InsertOneAsync(identity));
            identity["reviewMarker"] = "approved-fixture";
            await collection.InsertOneAsync(identity);
            identity["_id"] = 2L;
            identity["subject"] = "missing-fence";
            identity.Remove("fence");
            await Assert.ThrowsAsync<MongoWriteException>(() => collection.InsertOneAsync(identity));
        }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }

    [Fact]
    public async Task Expired_v2_resumes_only_v2_and_preserves_v1_history()
    {
        var name = "ut_auth_resume_" + Guid.NewGuid().ToString("N");
        var database = fixture.Client.GetDatabase(name);
        try
        {
            await ApplyVersionOneAsync(database);
            var before = await ReadVersionOneAsync(database);
            await ProtectOrdersAsync(database);
            await database.GetCollection<BsonDocument>("schemaMigrations").InsertOneAsync(new BsonDocument
            {
                { "_id", 2 }, { "state", "applying" }, { "owner", "interrupted-v2-process" },
                { "leaseUntil", clock.UtcNow.UtcDateTime.AddMinutes(-1) },
                { "startedAt", clock.UtcNow.UtcDateTime.AddMinutes(-11) }
            });
            await database.CreateCollectionAsync("authorizationScopes");
            var migrations = new CollectionMigrations(database, clock);
            Assert.False(await migrations.IsCurrentAsync());
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            Assert.Equal(before, await ReadVersionOneAsync(database));
            await AssertOrdersStillProtectedAsync(database);
            await Assert.ThrowsAsync<MongoWriteException>(() =>
                database.GetCollection<BsonDocument>("authorizationScopes")
                    .InsertOneAsync(new BsonDocument("schemaVersion", 1)));
        }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }

    [Fact]
    public async Task Cancelled_v2_ddl_releases_lease_and_retries_without_replaying_v1()
    {
        var name = "ut_auth_cancel_" + Guid.NewGuid().ToString("N");
        var database = fixture.Client.GetDatabase(name);
        using var cancellation = new CancellationTokenSource();
        var observed = new ConcurrentBag<BsonDocument>();
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(item => observed.Add(item.Command.DeepClone().AsBsonDocument));
            builder.Subscribe<CommandSucceededEvent>(item =>
            {
                if (item.CommandName == "collMod") cancellation.Cancel();
            });
        };
        var observingDatabase = new MongoClient(settings).GetDatabase(name);
        try
        {
            await ApplyVersionOneAsync(database);
            var before = await ReadVersionOneAsync(database);
            await ProtectOrdersAsync(database);
            var migrations = new CollectionMigrations(observingDatabase, clock);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migrations.ApplyAsync(cancellation.Token));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.False(await new CollectionMigrations(database, clock).IsCurrentAsync());
            var incomplete = await database.GetCollection<BsonDocument>("schemaMigrations")
                .Find(new BsonDocument("_id", 2)).SingleAsync();
            Assert.Equal("applying", incomplete["state"].AsString);
            Assert.True(incomplete["leaseUntil"].ToUniversalTime() <= clock.UtcNow.UtcDateTime);
            await new CollectionMigrations(database, clock).ApplyAsync();
            Assert.Equal(before, await ReadVersionOneAsync(database));
            await AssertOrdersStillProtectedAsync(database);
            Assert.DoesNotContain(observed, command => command.TryGetValue("collMod", out var target) && target == "orders");
            Assert.True(await new CollectionMigrations(database, clock).IsCurrentAsync());
        }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }

    [Fact]
    public async Task Incompatible_legacy_identity_stops_v2_without_inventing_authorization()
    {
        var name = "ut_auth_legacy_" + Guid.NewGuid().ToString("N");
        var database = fixture.Client.GetDatabase(name);
        try
        {
            await ApplyVersionOneAsync(database);
            var before = await ReadVersionOneAsync(database);
            var identities = database.GetCollection<BsonDocument>("identities");
            var legacy = new BsonDocument
                { { "schemaVersion", 1 }, { "issuer", "synthetic-legacy" }, { "subject", "unapproved" } };
            await identities.InsertOneAsync(legacy);
            var migrations = new CollectionMigrations(database, clock);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => migrations.ApplyAsync());
            Assert.Contains("identities", error.Message);
            Assert.Contains("reconcile", error.Message);
            Assert.DoesNotContain("unapproved", error.Message);
            Assert.False(await migrations.IsCurrentAsync());
            Assert.Equal(before, await ReadVersionOneAsync(database));
            Assert.Equal(legacy, await identities.Find(new BsonDocument("_id", legacy["_id"])).SingleAsync());
            Assert.DoesNotContain("authorizationGrants", await (await database.ListCollectionNamesAsync()).ToListAsync());

            // A fixture-only correction demonstrates retry; deployment never invents this decision.
            await identities.DeleteOneAsync(new BsonDocument("_id", legacy["_id"]));
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            Assert.Empty(await identities.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
        }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }

    [Fact]
    public async Task Active_v2_lease_blocks_another_runner_after_skipping_applied_v1()
    {
        var name = "ut_auth_busy_" + Guid.NewGuid().ToString("N");
        var database = fixture.Client.GetDatabase(name);
        try
        {
            await ApplyVersionOneAsync(database);
            var before = await ReadVersionOneAsync(database);
            var lease = await new MigrationGate(database, clock, 2).AcquireAsync(2, CancellationToken.None);
            Assert.NotNull(lease);
            try
            {
                var migrations = new CollectionMigrations(database, clock);
                var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => migrations.ApplyAsync());
                Assert.Equal("migration_in_progress", error.Code);
                Assert.False(await migrations.IsCurrentAsync());
                Assert.Equal(before, await ReadVersionOneAsync(database));
                var names = await (await database.ListCollectionNamesAsync()).ToListAsync();
                Assert.DoesNotContain("authorizationScopes", names);
            }
            finally { await lease!.ReleaseAsync(); }
            await new CollectionMigrations(database, clock).ApplyAsync();
        }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("incomplete-earlier")]
    [InlineData("unknown-newer")]
    [InlineData("untyped-id")]
    [InlineData("missing-applied-time")]
    public async Task Inconsistent_history_refuses_all_ddl(string fixtureKind)
    {
        var name = "ut_auth_history_" + Guid.NewGuid().ToString("N");
        var database = fixture.Client.GetDatabase(name);
        try
        {
            BsonDocument Applied(BsonValue id) => new()
                { { "_id", id }, { "state", "applied" }, { "appliedAt", clock.UtcNow.UtcDateTime } };
            BsonDocument[] records = fixtureKind switch
            {
                "gap" => [Applied(2)],
                "incomplete-earlier" =>
                [
                    new BsonDocument { { "_id", 1 }, { "state", "applying" }, { "owner", "lost" },
                        { "leaseUntil", clock.UtcNow.UtcDateTime.AddMinutes(-1) } }, Applied(2)
                ],
                "unknown-newer" => Enumerable.Range(1, CollectionMigrations.CurrentVersion + 1).Select(id => Applied(id)).ToArray(),
                "untyped-id" => [Applied("1")],
                _ => [new BsonDocument { { "_id", 1 }, { "state", "applied" } }]
            };
            var history = database.GetCollection<BsonDocument>("schemaMigrations");
            await history.InsertManyAsync(records);
            var migrations = new CollectionMigrations(database, clock);
            Assert.False(await migrations.IsCurrentAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => migrations.ApplyAsync());
            Assert.Equal(records.Length, await history.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            Assert.Equal(new[] { "schemaMigrations" }, await (await database.ListCollectionNamesAsync()).ToListAsync());
        }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }

    private async Task ApplyVersionOneAsync(IMongoDatabase database)
    {
        var lease = await new MigrationGate(database, clock, 1).AcquireAsync(1, CancellationToken.None);
        Assert.NotNull(lease);
        await new FoundationMigration(database.WithWriteConcern(WriteConcern.WMajority))
            .ApplyAsync(lease!, CancellationToken.None);
        await lease!.CompleteAsync(CancellationToken.None);
    }

    private static Task<BsonDocument> ReadVersionOneAsync(IMongoDatabase database) =>
        database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument("_id", 1)).SingleAsync();

    private static Task<BsonDocument> ProtectOrdersAsync(IMongoDatabase database) =>
        database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", "orders" },
            { "validator", new BsonDocument("$jsonSchema", new BsonDocument
            {
                { "bsonType", "object" }, { "required", new BsonArray { "schemaVersion", "protected" } }
            }) }, { "validationLevel", "strict" }, { "validationAction", "error" }
        });

    private static Task AssertOrdersStillProtectedAsync(IMongoDatabase database) =>
        Assert.ThrowsAsync<MongoWriteException>(() => database.GetCollection<BsonDocument>("orders")
            .InsertOneAsync(new BsonDocument("schemaVersion", 1)));
}
