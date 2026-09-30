using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class PaymentCorrectionMigrationTests(MongoFixture fixture)
{
    private async Task Run(Func<IMongoDatabase, Task> action) {
        var name = "ut_correction_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV35Async(db, new SystemClock()); await action(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonArray> History(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument[]> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
    [Fact]
    public Task Forward_upgrade_preserves_history_stronger_rules_and_installs_no_business_records() => Run(async db => {
        var history = await History(db); Assert.Equal(35, history.Count);
        var extra = new BsonDocument("recordedBy", new BsonDocument("$ne", 99L));
        await db.CreateCollectionAsync(PaymentCorrectionBson.Versions, new CreateCollectionOptions<BsonDocument> { Validator = extra });
        var before = await Definitions(db); await new CollectionMigrations(db, new SystemClock()).ApplyAsync();
        Assert.Equal(history, new BsonArray((await History(db)).Take(35))); Assert.Equal(CollectionMigrations.CurrentVersion, (await History(db)).Count);
        var after = await Definitions(db);
        foreach (var row in before.Where(row => !PaymentCorrectionSchemas.Collections.Contains(row["name"].AsString)))
            Assert.Equal(row, after.Single(value => value["name"] == row["name"]));
        Assert.True(InventorySchemas.Contains(after.Single(row => row["name"] == PaymentCorrectionBson.Versions)["options"]["validator"].AsBsonDocument, extra));
        foreach (var name in PaymentCorrectionSchemas.Collections) {
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(name).InsertOneAsync(new BsonDocument("_id", 1L)));
        }
        await new CollectionMigrations(db, new SystemClock()).ApplyAsync(); Assert.Equal(after, await Definitions(db));
    });
    [Theory]
    [InlineData("rows")] [InlineData("ttl")] [InlineData("unique")] [InlineData("counter")] [InlineData("orphan")]
    public Task Unsafe_preflight_keeps_predecessor_definitions_and_records(string fault) => Run(async db => {
        var rows = db.GetCollection<BsonDocument>(PaymentCorrectionBson.Versions);
        if (fault == "rows") await rows.InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "ttl") await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "unique") await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedBy", 1), new CreateIndexOptions { Unique = true }));
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", PaymentCorrectionBson.Versions }, { "value", long.MaxValue } });
        if (fault == "orphan") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", PaymentCorrectionBson.Operation } }, new InsertOneOptions { BypassDocumentValidation = true });
        var definitions = await Definitions(db); var history = await History(db);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, new SystemClock()).ApplyAsync());
        Assert.StartsWith("Payment correction migration preflight failed:", error.Message); Assert.Equal(definitions, await Definitions(db)); Assert.Equal(history, new BsonArray((await History(db)).Take(35)));
    });
}
