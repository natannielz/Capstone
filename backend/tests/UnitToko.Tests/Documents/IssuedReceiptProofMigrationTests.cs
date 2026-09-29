using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
public sealed class IssuedReceiptProofMigrationTests(MongoFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private async Task Run(Func<IMongoDatabase, Task> work) {
        var name = "ut_proof_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV26Async(db, Clock); await work(db); } finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonArray> History(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    [Fact]
    public Task Upgrade_keeps_all_prior_history_and_stronger_empty_contract_without_inventing_issued_documents() => Run(async db => {
        var before = await History(db); Assert.Equal(26, before.Count);
        var stronger = new BsonDocument("$and", new BsonArray { IssuedReceiptProofSchemas.Validator(), new BsonDocument("byteLength", new BsonDocument("$lte", 500000L)) });
        await db.CreateCollectionAsync("issuedReceiptProofs", new CreateCollectionOptions<BsonDocument> { Validator = stronger });
        await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "issuedReceiptProofs" }, { "value", 9007199254740993L } });
        await new CollectionMigrations(db, Clock).ApplyAsync(); var after = await History(db);
        Assert.Equal(before, new BsonArray(after.Take(26))); Assert.Equal(CollectionMigrations.CurrentVersion, after.Count);
        await new IssuedDocumentStore(db, receiptProof: true).StorageAsync(default);
        var definition = await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "issuedReceiptProofs") })).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
        Assert.Equal(0, await db.GetCollection<BsonDocument>("issuedReceiptProofs").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(9007199254740993L, (await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", "issuedReceiptProofs")).SingleAsync())["value"].AsInt64);
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(after, await History(db));
    });
    [Theory]
    [InlineData("data")] [InlineData("receipt")] [InlineData("audit")] [InlineData("outbox")]
    [InlineData("retention")] [InlineData("counter")] [InlineData("index")]
    public Task Preflight_rejects_unaccepted_data_or_unsafe_storage_before_installation(string fault) => Run(async db => {
        if (fault == "data") await db.GetCollection<BsonDocument>("issuedReceiptProofs").InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "receipt") await db.GetCollection<BsonDocument>("idempotencyRecords").InsertOneAsync(new BsonDocument("operation", IssuedDocumentStore.ProofOperation), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "audit") await db.GetCollection<BsonDocument>("auditEvents").InsertOneAsync(new BsonDocument("kind", IssuedDocumentStore.ProofKind + ".issued"), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "outbox") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument("sourceKey", IssuedDocumentStore.ProofKind + ".issued:7"), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "retention") await db.GetCollection<BsonDocument>("issuedReceiptProofs").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("issuedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "issuedReceiptProofs" }, { "value", long.MaxValue } });
        if (fault == "index") await db.GetCollection<BsonDocument>("issuedReceiptProofs").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(IssuedDocumentSchemas.Key(), new CreateIndexOptions { Unique = false }));
        var before = await History(db); var definitions = await (await db.ListCollectionsAsync()).ToListAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, Clock).ApplyAsync());
        Assert.Equal(before, new BsonArray((await History(db)).Take(26)));
        Assert.Equal(definitions.OrderBy(row => row["name"].AsString), (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString));
        Assert.False(await new CollectionMigrations(db, Clock).IsCurrentAsync());
    });
    [Fact]
    public Task Interrupted_creation_resumes_without_replaying_prior_migrations() => Run(async db => {
        var before = await History(db); using var cancellation = new CancellationTokenSource(); var changed = false;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(value => {
            if (value.CommandName == "create") { changed = true; cancellation.Cancel(); }
        });
        using var client = new MongoClient(settings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(client.GetDatabase(db.DatabaseNamespace.DatabaseName), Clock).ApplyAsync(cancellation.Token));
        Assert.True(changed); await new CollectionMigrations(db, Clock).ApplyAsync();
        Assert.Equal(before, new BsonArray((await History(db)).Take(26))); await new IssuedDocumentStore(db, receiptProof: true).StorageAsync(default);
    });
}
