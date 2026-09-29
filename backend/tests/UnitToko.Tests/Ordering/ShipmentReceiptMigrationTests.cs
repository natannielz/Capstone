using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentReceiptMigrationTests(MongoFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private async Task Run(Func<IMongoDatabase, Task> work) {
        var name = "ut_receipt_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV25Async(db, Clock); await work(db); } finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonArray> History(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    [Fact]
    public Task Forward_upgrade_preserves_prior_history_stricter_schema_and_counter_without_inventing_receipts() => Run(async db => {
        var before = await History(db); Assert.Equal(25, before.Count);
        var stronger = new BsonDocument("$and", new BsonArray { ShipmentReceiptSchemas.Validator(ShipmentReceiptRecords.Challenges), new BsonDocument("recipientName", new BsonDocument("$ne", "blocked")) });
        await db.CreateCollectionAsync(ShipmentReceiptRecords.Challenges, new CreateCollectionOptions<BsonDocument> { Validator = stronger });
        await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", ShipmentReceiptRecords.Receipts }, { "value", 9007199254740993L } });
        await new CollectionMigrations(db, Clock).ApplyAsync(); var after = await History(db);
        Assert.Equal(CollectionMigrations.CurrentVersion, after.Count); Assert.Equal(before, new BsonArray(after.Take(25)));
        await new ShipmentReceiptRecords(db).StorageAsync(default);
        var rangeIndexes = await (await db.GetCollection<BsonDocument>(ShipmentReceiptRecords.Challenges).Indexes.ListAsync()).ToListAsync();
        Assert.Contains(rangeIndexes, index => index["key"].Equals(ShipmentReceiptSchemas.ChallengeTimeKey()) && !index.GetValue("unique", false).AsBoolean);
        foreach (var name in ShipmentReceiptSchemas.Collections) Assert.Equal(0, await db.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
        var definition = await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", ShipmentReceiptRecords.Challenges) })).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
        Assert.Equal(9007199254740993L, (await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", ShipmentReceiptRecords.Receipts)).SingleAsync())["value"].AsInt64);
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(after, await History(db));
    });
    [Theory]
    [InlineData("data")] [InlineData("operation")] [InlineData("audit")] [InlineData("outbox")]
    [InlineData("retention")] [InlineData("counter")] [InlineData("index")]
    [InlineData("range-index")]
    public Task Preflight_of_both_collections_rejects_unsafe_state_before_any_owner_DDL(string fault) => Run(async db => {
        // Put the fault in the second collection: the first must not be installed early.
        const string name = ShipmentReceiptRecords.Receipts;
        if (fault == "data") await db.GetCollection<BsonDocument>(name).InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "operation") await db.GetCollection<BsonDocument>("idempotencyRecords").InsertOneAsync(new BsonDocument("operation", ShipmentReceiptRecords.Confirm), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "audit") await db.GetCollection<BsonDocument>("auditEvents").InsertOneAsync(new BsonDocument("kind", ShipmentReceiptRecords.Issue), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "outbox") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument("sourceKey", ShipmentReceiptRecords.Confirm + ":7"), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "retention") await db.GetCollection<BsonDocument>(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", name }, { "value", long.MaxValue } });
        if (fault == "index") await db.GetCollection<BsonDocument>(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(ShipmentReceiptSchemas.Key(name), new CreateIndexOptions { Unique = false }));
        if (fault == "range-index") await db.GetCollection<BsonDocument>(ShipmentReceiptRecords.Challenges).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recipientId", 1), new CreateIndexOptions { Name = "ix_receipt_challenge_shipment_time" }));
        var before = await History(db); var definitions = await (await db.ListCollectionsAsync()).ToListAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, Clock).ApplyAsync());
        Assert.Equal(before, new BsonArray((await History(db)).Take(25)));
        Assert.Equal(definitions.OrderBy(row => row["name"].AsString), (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString));
        Assert.False(await new CollectionMigrations(db, Clock).IsCurrentAsync());
    });
}
