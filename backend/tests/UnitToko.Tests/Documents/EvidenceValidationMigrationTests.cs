using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
public sealed class EvidenceValidationMigrationTests(MongoFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private async Task Run(Func<IMongoDatabase, Task> action) {
        var name = "ut_validation_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV31Async(db, Clock); await action(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonArray> History(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument[]> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
    [Fact]
    public Task Upgrade_retains_history_prior_rules_and_stronger_partial_installation() => Run(async db => {
        var before = await History(db); Assert.Equal(31, before.Count);
        var prior = (await Definitions(db)).Single(row => row["name"] == "outboxEvents")["options"]["validator"];
        var stronger = new BsonDocument("$and", new BsonArray { EvidenceValidationSchemas.Validator("evidenceValidationResults"), new BsonDocument("length", new BsonDocument("$lte", 100000L)) });
        await db.CreateCollectionAsync("evidenceValidationResults", new CreateCollectionOptions<BsonDocument> { Validator = stronger });
        await new CollectionMigrations(db, Clock).ApplyAsync();
        Assert.Equal(before, new BsonArray((await History(db)).Take(31)));
        var definitions = await Definitions(db);
        Assert.Equal(stronger, definitions.Single(row => row["name"] == "evidenceValidationResults")["options"]["validator"]);
        Assert.True(InventorySchemas.Contains(definitions.Single(row => row["name"] == "outboxEvents")["options"]["validator"].AsBsonDocument, prior.AsBsonDocument));
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(CollectionMigrations.CurrentVersion, (await History(db)).Count);
    });
    [Theory]
    [InlineData("preexisting")] [InlineData("ttl")] [InlineData("counter")] [InlineData("outbox_orphan")] [InlineData("outbox_validator")]
    public Task Preflight_fails_before_target_mutation(string fault) => Run(async db => {
        if (fault == "preexisting") await db.GetCollection<BsonDocument>("evidenceValidationResults").InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "ttl") await db.GetCollection<BsonDocument>("evidenceValidationAudits").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("checkedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "evidenceValidationAudits" }, { "value", long.MaxValue } });
        if (fault == "outbox_orphan") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument { { "kind", "evidence.validation.requested" }, { "processingFence", 1L } }, new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "outbox_validator") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" }, { "validator", new BsonDocument("schemaVersion", 1) } });
        var before = await Definitions(db); var history = await History(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, Clock).ApplyAsync());
        Assert.Equal(before, await Definitions(db)); Assert.Equal(history, new BsonArray((await History(db)).Take(31)));
    });
    [Fact]
    public Task Interrupted_ddl_resumes_without_rewriting_applied_history() => Run(async db => {
        var before = await History(db); using var cancellation = new CancellationTokenSource(); var changed = false;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(value => {
            if (value.CommandName == "create") { changed = true; cancellation.Cancel(); }
        });
        using var client = new MongoClient(settings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(client.GetDatabase(db.DatabaseNamespace.DatabaseName), Clock).ApplyAsync(cancellation.Token));
        Assert.True(changed); await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(before, new BsonArray((await History(db)).Take(31)));
    });
}
