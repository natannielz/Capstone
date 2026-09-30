using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using M = UnitToko.Platform.Billing.PaymentManualReviewRecords;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class PaymentManualReviewMigrationTests(MongoFixture fixture)
{
    private async Task Run(Func<IMongoDatabase, Task> action) {
        var name = "ut_manual_review_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV41Async(db, new SystemClock()); await action(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonArray> History(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument[]> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
    [Fact]
    public Task Forward_upgrade_retains_original_schemas_history_custom_constraints_and_empty_unapproved_policies() => Run(async db => {
        var history = await History(db); Assert.Equal(41, history.Count);
        var extra = new BsonDocument("declarationId", new BsonDocument("$ne", 99L));
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", PaymentVerificationBson.Decisions }, { "validator", new BsonDocument("$and", new BsonArray { extra, PaymentVerificationSchemas.Validator(PaymentVerificationBson.Decisions) }) } });
        var before = await Definitions(db); await new CollectionMigrations(db, new SystemClock()).ApplyAsync();
        Assert.Equal(history, new BsonArray((await History(db)).Take(41))); Assert.Equal(42, (await History(db)).Count);
        var after = await Definitions(db);
        foreach (var row in before.Where(row => row["name"] != PaymentVerificationBson.Decisions)) Assert.Equal(row, after.Single(value => value["name"] == row["name"]));
        var validator = after.Single(row => row["name"] == PaymentVerificationBson.Decisions)["options"]["validator"].AsBsonDocument;
        Assert.True(InventorySchemas.Contains(validator, extra)); Assert.True(InventorySchemas.Contains(validator, PaymentManualReviewSchemas.VerificationValidator()));
        Assert.Equal(PaymentVerificationSchemas.Validator(PaymentVerificationBson.Decisions), PaymentManualReviewSchemas.VerificationValidator()["$or"][0]);
        foreach (var name in PaymentManualReviewSchemas.Collections) {
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(name).InsertOneAsync(new BsonDocument("_id", 1L)));
        }
        await new CollectionMigrations(db, new SystemClock()).ApplyAsync(); Assert.Equal(after, await Definitions(db));
        // Rehearse interruption after DDL, before its deployment receipt, with the custom constraint retained.
        await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 42));
        await new CollectionMigrations(db, new SystemClock()).ApplyAsync(); Assert.Equal(after, await Definitions(db));
        Assert.Equal(42, (await History(db)).Count);
    });
    [Theory]
    [InlineData("rows")] [InlineData("ttl")] [InlineData("unique")] [InlineData("counter")] [InlineData("orphan")] [InlineData("weakened")]
    public Task Unsafe_preflight_preserves_predecessor_definitions_and_history(string fault) => Run(async db => {
        var rows = db.GetCollection<BsonDocument>(M.Versions);
        if (fault == "rows") await rows.InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "ttl") await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "unique") await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("actorId", 1), new CreateIndexOptions { Unique = true }));
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", M.Versions }, { "value", long.MaxValue } });
        if (fault == "orphan") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", M.Operation } }, new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "weakened") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", PaymentVerificationBson.Decisions }, { "validationAction", "warn" } });
        var definitions = await Definitions(db); var history = await History(db);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, new SystemClock()).ApplyAsync());
        Assert.StartsWith("Payment manual review migration preflight failed:", failure.Message); Assert.Equal(definitions, await Definitions(db)); Assert.Equal(history, new BsonArray((await History(db)).Take(41)));
    });
}
