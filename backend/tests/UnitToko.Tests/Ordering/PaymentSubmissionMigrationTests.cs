using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class PaymentSubmissionMigrationTests(MongoFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private async Task Run(Func<IMongoDatabase, Task> action) {
        var name = "ut_submit_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV33Async(db, Clock); await action(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonArray> History(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument[]> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
    [Fact]
    public async Task Forward_upgrade_preserves_actual_v33_drafts_and_receipts_and_keeps_stronger_declaration_rules() {
        await using var original = await PaymentDraftData.CreateAsync(fixture);
        var saved = await original.Save(null, 0, original.Input, "predecessor-draft");
        await original.Save(saved["declarationId"].AsInt64, 1, original.Input with { DeclaredAmount = new(1234.56m) }, "predecessor-edit");
        await Run(async db => {
            var snapshots = new Dictionary<string, BsonArray>();
            foreach (var name in await (await original.Invoice.Order.Database.ListCollectionNamesAsync()).ToListAsync()) {
                if (name == "schemaMigrations" || PaymentSubmissionSchemas.Collections.Contains(name)) continue;
                var rows = await original.Invoice.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
                Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
                if (rows.Count > 0) await db.GetCollection<BsonDocument>(name).InsertManyAsync(rows);
                snapshots[name] = new(rows);
            }
            var before = await History(db);
            // A stronger existing constraint must be checked independently of the base
            // schema: accepting either one would silently adopt a known-invalid head.
            await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", PaymentDraftBson.Heads }, { "validator", new BsonDocument("$and", new BsonArray {
                PaymentDraftSchemas.Validator(PaymentDraftBson.Heads), new BsonDocument("revision", new BsonDocument("$lt", 2L)) }) } });
            var blockedDefinitions = await Definitions(db);
            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => LegacyOrderSubmissionTestSchema.UpgradeV34Async(db, Clock));
            Assert.Contains(PaymentDraftBson.Heads, blocked.Message); Assert.Equal(blockedDefinitions, await Definitions(db));
            Assert.Equal(before, new BsonArray((await History(db)).Take(33)));
            var extra = new BsonDocument("revision", new BsonDocument("$lt", 10000L));
            await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", PaymentDraftBson.Heads }, { "validator", new BsonDocument("$and", new BsonArray { PaymentDraftSchemas.Validator(PaymentDraftBson.Heads), extra }) } });
            var definitions = await Definitions(db);
            await LegacyOrderSubmissionTestSchema.UpgradeV34Async(db, Clock);
            Assert.Equal(34, (await History(db)).Count); Assert.Equal(before, new BsonArray((await History(db)).Take(33)));
            foreach (var pair in snapshots) Assert.Equal(pair.Value, new BsonArray(await db.GetCollection<BsonDocument>(pair.Key).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            var after = await Definitions(db);
            foreach (var row in definitions.Where(row => row["name"] != PaymentDraftBson.Heads)) Assert.Equal(row, after.Single(value => value["name"] == row["name"]));
            Assert.Equal(new BsonDocument("$and", new BsonArray { PaymentSubmissionSchemas.DeclarationValidator(), extra }), after.Single(row => row["name"] == PaymentDraftBson.Heads)["options"]["validator"]);
            foreach (var name in PaymentSubmissionSchemas.Collections) {
                await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(name).InsertOneAsync(new BsonDocument("_id", 1)));
                Assert.Contains(await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync(), index => index["key"].Equals(PaymentSubmissionSchemas.Key(name)) && index.GetValue("unique", false).AsBoolean);
            }
            await LegacyOrderSubmissionTestSchema.UpgradeV34Async(db, Clock); Assert.Equal(34, (await History(db)).Count);
        });
    }
    [Theory]
    [InlineData("preexisting")] [InlineData("ttl")] [InlineData("counter")] [InlineData("orphan")]
    public Task Unsafe_or_orphaned_preflight_changes_no_target_definition(string fault) => Run(async db => {
        if (fault == "preexisting") await db.GetCollection<BsonDocument>("evidencePinAudits").InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "ttl") await db.GetCollection<BsonDocument>("evidencePinAudits").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("createdAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "evidencePinAudits" }, { "value", long.MaxValue } });
        if (fault == "orphan") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", "payment.ocr.requested" } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await Definitions(db); var history = await History(db);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => LegacyOrderSubmissionTestSchema.UpgradeV34Async(db, Clock));
        Assert.StartsWith("Payment submission migration preflight failed:", failure.Message);
        Assert.Equal(before, await Definitions(db)); Assert.Equal(history, new BsonArray((await History(db)).Take(33)));
    });
    [Fact]
    public Task Interrupted_empty_installation_resumes_and_does_not_weaken_local_rules() => Run(async db => {
        var extra = new BsonDocument("declarationRevision", new BsonDocument("$lt", 10000L));
        var stronger = new BsonDocument("$and", new BsonArray { PaymentSubmissionSchemas.Validator("evidencePins"), extra });
        await db.CreateCollectionAsync("evidencePins", new CreateCollectionOptions<BsonDocument> { Validator = stronger });
        var before = await History(db); using var cancellation = new CancellationTokenSource(); var changed = false;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(value => {
            if (value.CommandName == "create") { changed = true; cancellation.Cancel(); }
        });
        using var client = new MongoClient(settings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LegacyOrderSubmissionTestSchema.UpgradeV34Async(client.GetDatabase(db.DatabaseNamespace.DatabaseName), Clock, cancellation.Token));
        Assert.True(changed); await LegacyOrderSubmissionTestSchema.UpgradeV34Async(db, Clock); Assert.Equal(before, new BsonArray((await History(db)).Take(33)));
        Assert.Equal(stronger, (await Definitions(db)).Single(row => row["name"] == "evidencePins")["options"]["validator"]);
    });
}
