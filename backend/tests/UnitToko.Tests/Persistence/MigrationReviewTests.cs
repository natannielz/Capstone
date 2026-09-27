using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Persistence;

[Collection("Mongo")]
public sealed class MigrationReviewTests(MongoFixture fixture)
{
    private readonly IClock clock = new SystemClock();

    [Fact]
    public async Task Repeated_version_preserves_applied_time_and_stronger_validator()
    {
        var name = "ut_mig_repeat_" + Guid.NewGuid().ToString("N");
        var db = fixture.Client.GetDatabase(name);
        try
        {
            var migrations = new CollectionMigrations(db, clock);
            Assert.False(await migrations.IsCurrentAsync());
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            var history = db.GetCollection<BsonDocument>("schemaMigrations");
            var before = await history.Find(new BsonDocument("_id", 1)).FirstAsync();
            await db.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "collMod", "orders" },
                { "validator", new BsonDocument("$jsonSchema", new BsonDocument
                {
                    { "bsonType", "object" },
                    { "required", new BsonArray { "schemaVersion", "protected" } }
                }) },
                { "validationLevel", "strict" }, { "validationAction", "error" }
            });
            await migrations.ApplyAsync();
            var after = await history.Find(new BsonDocument("_id", 1)).FirstAsync();
            Assert.Equal(before["appliedAt"], after["appliedAt"]);
            await Assert.ThrowsAsync<MongoWriteException>(() =>
                db.GetCollection<BsonDocument>("orders").InsertOneAsync(new BsonDocument
                { { "schemaVersion", 1 } }));
        }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }

    [Fact]
    public async Task Unknown_newer_version_refuses_changes_and_reports_not_current()
    {
        var name = "ut_mig_future_" + Guid.NewGuid().ToString("N");
        var db = fixture.Client.GetDatabase(name);
        try
        {
            var history = db.GetCollection<BsonDocument>("schemaMigrations");
            await history.InsertOneAsync(new BsonDocument
            { { "_id", CollectionMigrations.CurrentVersion + 1 }, { "state", "applied" }, { "appliedAt", clock.UtcNow.UtcDateTime } });
            var migrations = new CollectionMigrations(db, clock);
            Assert.False(await migrations.IsCurrentAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => migrations.ApplyAsync());
            Assert.Equal(1, await history.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            var names = await (await db.ListCollectionNamesAsync()).ToListAsync();
            Assert.Equal(new[] { "schemaMigrations" }, names);
        }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }

    [Fact]
    public async Task Active_migration_lease_blocks_a_competing_runner_without_ddl()
    {
        var name = "ut_mig_busy_" + Guid.NewGuid().ToString("N");
        var db = fixture.Client.GetDatabase(name);
        try
        {
            await db.GetCollection<BsonDocument>("schemaMigrations").InsertOneAsync(new BsonDocument
            {
                { "_id", 1 }, { "state", "applying" }, { "owner", "other-deployer" },
                { "leaseUntil", clock.UtcNow.UtcDateTime.AddMinutes(5) }
            });
            var migrations = new CollectionMigrations(db, clock);
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => migrations.ApplyAsync());
            Assert.Equal("migration_in_progress", error.Code);
            Assert.False(await migrations.IsCurrentAsync());
            var names = await (await db.ListCollectionNamesAsync()).ToListAsync();
            Assert.Equal(new[] { "schemaMigrations" }, names);
        }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    [Fact]
    public async Task Expired_incomplete_migration_resumes_and_marks_ready()
    {
        var name = "ut_mig_resume_" + Guid.NewGuid().ToString("N");
        var db = fixture.Client.GetDatabase(name);
        try
        {
            await db.GetCollection<BsonDocument>("schemaMigrations").InsertOneAsync(new BsonDocument
            {
                { "_id", 1 }, { "state", "applying" }, { "owner", "lost-process" },
                { "leaseUntil", clock.UtcNow.UtcDateTime.AddMinutes(-1) }
            });
            await db.CreateCollectionAsync("orders");
            var migrations = new CollectionMigrations(db, clock);
            Assert.False(await migrations.IsCurrentAsync());
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            await Assert.ThrowsAsync<MongoWriteException>(() =>
                db.GetCollection<BsonDocument>("orders").InsertOneAsync(new BsonDocument("bad", true)));
        }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
}
