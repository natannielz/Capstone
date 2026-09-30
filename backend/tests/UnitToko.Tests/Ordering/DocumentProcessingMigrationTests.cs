using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using System.Runtime.Versioning;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class DocumentProcessingMigrationTests(MongoFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private async Task Run(Func<IMongoDatabase, Task> action) {
        var name = "ut_ocr_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV34Async(db, Clock); await action(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonArray> History(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument[]> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Existing_submitted_payment_and_pending_job_survive_actual_forward_upgrade_unchanged() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await PaymentOcrProcessorTests.Submit(data);
        await Run(async db => {
            var snapshots = new Dictionary<string, BsonArray>();
            foreach (var name in await (await data.Database.ListCollectionNamesAsync()).ToListAsync()) {
                if (name == "schemaMigrations" || DocumentProcessingSchemas.Collections.Contains(name)) continue;
                var rows = await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
                Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
                if (rows.Count > 0) await db.GetCollection<BsonDocument>(name).InsertManyAsync(rows);
                snapshots[name] = new(rows);
            }
            var history = await History(db); await new CollectionMigrations(db, Clock).ApplyAsync();
            Assert.Equal(history, new BsonArray((await History(db)).Take(34))); Assert.Equal(CollectionMigrations.CurrentVersion, (await History(db)).Count);
            foreach (var pair in snapshots) Assert.Equal(pair.Value, new BsonArray(await db.GetCollection<BsonDocument>(pair.Key).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            var job = await db.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument("kind", "payment.ocr.requested")).SingleAsync();
            Assert.Equal("pending", job["status"]); Assert.Equal(0, job["attempts"]);
        });
    }
    [Fact]
    public Task Forward_upgrade_preserves_v34_history_and_stronger_rules_and_does_not_activate_worker() => Run(async db => {
        var history = await History(db); var extra = new BsonDocument("consumerId", new BsonDocument("$ne", "unapproved-consumer"));
        await db.CreateCollectionAsync("documentProcessingPolicies", new CreateCollectionOptions<BsonDocument> { Validator = extra });
        await new CollectionMigrations(db, Clock).ApplyAsync();
        Assert.Equal(CollectionMigrations.CurrentVersion, (await History(db)).Count); Assert.Equal(history, new BsonArray((await History(db)).Take(34)));
        foreach (var name in DocumentProcessingSchemas.Collections) {
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(name).InsertOneAsync(new BsonDocument("_id", 1L)));
        }
        var definition = (await Definitions(db)).Single(row => row["name"] == "documentProcessingPolicies");
        Assert.True(InventorySchemas.Contains(definition["options"]["validator"].AsBsonDocument, extra));
        var after = await Definitions(db); await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(after, await Definitions(db));
    });
    [Theory]
    [InlineData("preexisting")] [InlineData("ttl")] [InlineData("counter")] [InlineData("orphan")]
    public Task Unsafe_preflight_preserves_all_predecessor_definitions(string fault) => Run(async db => {
        if (fault == "preexisting") await db.GetCollection<BsonDocument>("documentExtractionResults").InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "ttl") await db.GetCollection<BsonDocument>("documentExtractionReadAudits").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("createdAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "documentExtractionResults" }, { "value", long.MaxValue } });
        if (fault == "orphan") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", "payment.ocr.requested" } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await Definitions(db); var history = await History(db);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, Clock).ApplyAsync());
        Assert.StartsWith("Document processing migration preflight failed:", error.Message);
        Assert.Equal(before, await Definitions(db)); Assert.Equal(history, new BsonArray((await History(db)).Take(34)));
    });
}
