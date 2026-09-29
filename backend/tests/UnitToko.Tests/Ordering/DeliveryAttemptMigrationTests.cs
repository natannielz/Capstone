using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class DeliveryAttemptMigrationTests(MongoFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private async Task Run(Func<IMongoDatabase, Task> work) {
        var name = "ut_visit_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV27Async(db, Clock); await work(db); } finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonArray> History(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    [Fact]
    public Task Upgrade_preserves_27_accepted_migrations_stronger_validators_and_allocator() => Run(async db => {
        var before = await History(db); Assert.Equal(27, before.Count);
        var stronger = new BsonDocument("$and", new BsonArray { DeliveryAttemptSchemas.Validator("deliveryAttempts"), new BsonDocument("sequence", new BsonDocument("$lte", 100L)) });
        await db.CreateCollectionAsync("deliveryAttempts", new CreateCollectionOptions<BsonDocument> { Validator = stronger });
        await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "deliveryAttempts" }, { "value", 9007199254740993L } });
        await new CollectionMigrations(db, Clock).ApplyAsync(); var after = await History(db);
        Assert.Equal(before, new BsonArray(after.Take(27))); Assert.Equal(CollectionMigrations.CurrentVersion, after.Count);
        await new DeliveryAttemptRecords(db).StorageAsync(default);
        var definition = await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "deliveryAttempts") })).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
        Assert.Equal(9007199254740993L, (await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", "deliveryAttempts")).SingleAsync())["value"].AsInt64);
        foreach (var name in DeliveryAttemptSchemas.Collections) Assert.Equal(0, await db.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(after, await History(db));
    });
    [Theory]
    [InlineData("attempt")] [InlineData("revocation")] [InlineData("receipt")] [InlineData("audit")] [InlineData("outbox")]
    [InlineData("retention")] [InlineData("counter")] [InlineData("sequence-index")] [InlineData("lookup-index")]
    public Task Preflight_checks_both_collections_and_all_effects_before_DDL(string fault) => Run(async db => {
        if (fault == "attempt") await db.GetCollection<BsonDocument>("deliveryAttempts").InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "revocation") await db.GetCollection<BsonDocument>("receiptChallengeRevocations").InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "receipt") await db.GetCollection<BsonDocument>("idempotencyRecords").InsertOneAsync(new BsonDocument("operation", DeliveryAttemptRecords.Operation), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "audit") await db.GetCollection<BsonDocument>("auditEvents").InsertOneAsync(new BsonDocument("kind", DeliveryAttemptRecords.Operation), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "outbox") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument("sourceKey", DeliveryAttemptRecords.Operation + ":7"), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "retention") await db.GetCollection<BsonDocument>("receiptChallengeRevocations").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "deliveryAttempts" }, { "value", long.MaxValue } });
        if (fault == "sequence-index") await db.GetCollection<BsonDocument>("deliveryAttempts").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(DeliveryAttemptSchemas.Key("deliveryAttempts"), new CreateIndexOptions { Unique = false }));
        if (fault == "lookup-index") await db.GetCollection<BsonDocument>("receiptChallengeRevocations").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("attemptId", 1), new CreateIndexOptions { Name = "ix_receipt_revocation_shipment" }));
        var before = await History(db); var definitions = await (await db.ListCollectionsAsync()).ToListAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, Clock).ApplyAsync());
        var failedHistory = await History(db);
        Assert.Equal(before, new BsonArray(failedHistory.Take(27)));
        Assert.Equal("applying", failedHistory.Last()["state"].AsString);
        Assert.Equal(definitions.OrderBy(row => row["name"].AsString), (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString));
        Assert.False(await new CollectionMigrations(db, Clock).IsCurrentAsync());
    });
    [Fact]
    public Task Interrupted_first_collection_install_resumes_without_replaying_history() => Run(async db => {
        var before = await History(db); using var cancellation = new CancellationTokenSource(); var changed = false;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(value => { if (value.CommandName == "create") { changed = true; cancellation.Cancel(); } });
        using var client = new MongoClient(settings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(client.GetDatabase(db.DatabaseNamespace.DatabaseName), Clock).ApplyAsync(cancellation.Token));
        Assert.True(changed); await new CollectionMigrations(db, Clock).ApplyAsync();
        Assert.Equal(before, new BsonArray((await History(db)).Take(27))); await new DeliveryAttemptRecords(db).StorageAsync(default);
    });
}
