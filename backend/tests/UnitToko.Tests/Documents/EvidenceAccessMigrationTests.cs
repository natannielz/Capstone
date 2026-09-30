using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
public sealed class EvidenceAccessMigrationTests(MongoFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private async Task Run(Func<IMongoDatabase, Task> action) {
        var name = "ut_evidence_access_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV32Async(db, Clock); await action(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonArray> History(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument[]> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
    [Fact]
    public Task Upgrade_keeps_prior_history_definitions_and_records_and_resumes_stronger_empty_installation() => Run(async db => {
        var before = await History(db); Assert.Equal(32, before.Count);
        var prior = await Definitions(db);
        var policy = EvidenceAdmissionSchemaTests.Policy(); await db.GetCollection<BsonDocument>("evidenceStorePolicies").InsertOneAsync(policy);
        var stronger = new BsonDocument("$and", new BsonArray { EvidenceAccessSchemas.Validator("evidenceReadPolicies"), new BsonDocument("maximumBytes", new BsonDocument("$lte", 100000L)) });
        await db.CreateCollectionAsync("evidenceReadPolicies", new CreateCollectionOptions<BsonDocument> { Validator = stronger });
        await new CollectionMigrations(db, Clock).ApplyAsync();
        var definitions = await Definitions(db); Assert.Equal(before, new BsonArray((await History(db)).Take(32)));
        foreach (var row in prior) Assert.Equal(row, definitions.Single(value => value["name"] == row["name"]));
        Assert.Equal(policy, await db.GetCollection<BsonDocument>("evidenceStorePolicies").Find(new BsonDocument()).SingleAsync());
        Assert.Equal(stronger, definitions.Single(row => row["name"] == "evidenceReadPolicies")["options"]["validator"]);
        foreach (var name in EvidenceAccessSchemas.Collections) {
            var options = definitions.Single(row => row["name"] == name)["options"];
            Assert.Equal("strict", options["validationLevel"]); Assert.Equal("error", options["validationAction"]);
            Assert.True(InventorySchemas.Contains(options["validator"].AsBsonDocument, EvidenceAccessSchemas.Validator(name)));
            await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(name).InsertOneAsync(new BsonDocument("_id", 1)));
        }
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(33, (await History(db)).Count);
    });
    [Theory]
    [InlineData("preexisting")] [InlineData("ttl")] [InlineData("counter")] [InlineData("wrong_unique")]
    public Task Preflight_rejects_unsafe_target_before_any_other_target_changes(string fault) => Run(async db => {
        if (fault == "preexisting") await db.GetCollection<BsonDocument>("evidenceReadAudits").InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "ttl") await db.GetCollection<BsonDocument>("evidenceReadAudits").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("createdAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "evidenceReadAudits" }, { "value", long.MaxValue } });
        if (fault == "wrong_unique") await db.GetCollection<BsonDocument>("evidenceReadAudits").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("actorId", 1), new CreateIndexOptions { Unique = true }));
        var before = await Definitions(db); var history = await History(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, Clock).ApplyAsync());
        Assert.Equal(before, await Definitions(db)); Assert.Equal(history, new BsonArray((await History(db)).Take(32)));
    });
    [Fact]
    public Task Interrupted_installation_resumes_without_rewriting_history() => Run(async db => {
        var before = await History(db); using var cancellation = new CancellationTokenSource(); var changed = false;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(value => {
            if (value.CommandName == "create") { changed = true; cancellation.Cancel(); }
        });
        using var client = new MongoClient(settings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(client.GetDatabase(db.DatabaseNamespace.DatabaseName), Clock).ApplyAsync(cancellation.Token));
        Assert.True(changed); await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(before, new BsonArray((await History(db)).Take(32)));
    });
}
