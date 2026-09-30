using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class PaymentNotificationMigrationTests(MongoFixture fixture)
{
    private async Task Run(Func<IMongoDatabase, Task> action) {
        var name = "ut_payment_notice_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV44Async(db, new SystemClock()); await action(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonDocument[]> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
    [Fact]
    public Task Forward_upgrade_preserves_previous_receipts_and_stronger_rules_and_resumes_interrupted_empty_DDL() => Run(async db => {
        var definition = (await Definitions(db)).Single(row => row["name"] == "outboxEvents");
        var extra = new BsonDocument("sourceKey", new BsonDocument("$ne", "operator-blocked-source"));
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" }, { "validator", new BsonDocument("$and", new BsonArray { definition["options"]["validator"], extra }) } });
        var before = await Definitions(db); var receipts = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        await new CollectionMigrations(db, new SystemClock()).ApplyAsync(); var after = await Definitions(db);
        Assert.Equal(before.Where(row => row["name"] != "outboxEvents"), after.Where(row => row["name"] != "outboxEvents"));
        var validator = after.Single(row => row["name"] == "outboxEvents")["options"]["validator"].AsBsonDocument;
        Assert.True(InventorySchemas.Contains(validator, extra)); Assert.True(InventorySchemas.Contains(validator, PaymentNotificationSchemas.OutboxValidator()));
        Assert.Equal(receipts, (await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Take(44));
        // Isolated interrupted-install fixture; no production history is deleted or downgraded.
        await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 45));
        await new CollectionMigrations(db, new SystemClock()).ApplyAsync(); Assert.Equal(after, await Definitions(db));
    });
    [Theory]
    [InlineData("ttl")] [InlineData("orphan")] [InlineData("unexplained_completion")]
    public Task Unsafe_or_unexplained_prior_queue_state_stops_before_DDL(string fault) => Run(async db => {
        var rows = db.GetCollection<BsonDocument>("outboxEvents");
        if (fault == "ttl") await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("createdAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        else await rows.InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", PaymentReviewActionRecords.Operation },
            { "status", fault == "orphan" ? "pending" : "completed" }, { "attempts", 0 }, { "createdAt", DateTime.UtcNow }, { "payload", new BsonDocument("actionId", 99L) } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await Definitions(db); await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, new SystemClock()).ApplyAsync());
        Assert.Equal(before, await Definitions(db)); Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 45 }, { "state", "applied" } }).AnyAsync());
    });
}
