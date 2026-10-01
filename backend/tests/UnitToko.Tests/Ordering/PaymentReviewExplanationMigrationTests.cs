using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentReviewExplanationMigrationTests(MongoFixture fixture)
{
    private static async Task<BsonDocument[]> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
    private async Task Run(Func<IMongoDatabase, Task> action) {
        var name = "ut_payment_explanation_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV45Async(db, new SystemClock()); await action(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    [Fact]
    public Task Forward_upgrade_changes_only_the_action_validator_preserves_stronger_constraints_and_resumes_empty_interruption() => Run(async db => {
        const string name = PaymentReviewActionRecords.Versions;
        var extra = new BsonDocument("requestKey", new BsonDocument("$ne", "operator-blocked-key"));
        var old = (await Definitions(db)).Single(row => row["name"] == name)["options"]["validator"];
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", new BsonDocument("$and", new BsonArray { old, extra }) } });
        var before = await Definitions(db); var receipts = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var indexes = await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync();
        await LegacyOrderSubmissionTestSchema.UpgradeV46Async(db, new SystemClock()); var after = await Definitions(db);
        Assert.Equal(before.Where(row => row["name"] != name), after.Where(row => row["name"] != name));
        var validator = after.Single(row => row["name"] == name)["options"]["validator"].AsBsonDocument;
        Assert.True(InventorySchemas.Contains(validator, extra)); Assert.True(InventorySchemas.Contains(validator, PaymentReviewExplanationSchemas.Validator()));
        Assert.Equal(indexes, await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync());
        Assert.Equal(receipts, (await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Take(45));
        // Isolated interrupted-install fixture; production receipts are never removed or downgraded.
        await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 46));
        await LegacyOrderSubmissionTestSchema.UpgradeV46Async(db, new SystemClock()); Assert.Equal(after, await Definitions(db));
    });
    [Theory]
    [InlineData("ttl")] [InlineData("orphan")] [InlineData("unexpected_v2")]
    public Task Unexplained_history_and_unsafe_indexes_stop_before_any_DDL(string fault) => Run(async db => {
        var rows = db.GetCollection<BsonDocument>(PaymentReviewActionRecords.Versions);
        if (fault == "ttl") await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        else if (fault == "unexpected_v2") await rows.InsertOneAsync(new BsonDocument { { "_id", 99L }, { "schemaVersion", 2 } }, new InsertOneOptions { BypassDocumentValidation = true });
        else await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", PaymentReviewActionRecords.Operation },
            { "payload", new BsonDocument("actionId", 99L) } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await Definitions(db); Assert.NotNull(await Record.ExceptionAsync(() => LegacyOrderSubmissionTestSchema.UpgradeV46Async(db, new SystemClock())));
        Assert.Equal(before, await Definitions(db)); Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 46 }, { "state", "applied" } }).AnyAsync());
    });
    [WindowsStorageFact]
    public async Task Populated_prior_schema_preserves_legacy_hashes_closed_history_and_completed_notification_receipts() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); await PaymentReviewActionTests.Grant(data);
        var db = data.Read.Database; var shown = await data.Service.ReadReviewActionsAsync(data.Reviewer, data.Read.DeclarationId);
        await data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 0, new(shown.Binding, "reject"), PaymentVerificationTestData.Meta("legacy-explanation-upgrade"));
        var processor = new PaymentReviewNotifications(db, new(data.Read.Catalog.Client), data.Read.Catalog.Clock); Assert.Equal("completed", await processor.RunOnceAsync());
        var tables = new[] { PaymentReviewActionRecords.Versions, "outboxEvents", "idempotencyRecords", "auditEvents", "journalEntries" };
        var before = new Dictionary<string, List<BsonDocument>>(); foreach (var table in tables) before[table] = await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        // Model the actual v45 predecessor in this isolated fixture, retaining every prior data and migration receipt.
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", PaymentReviewActionRecords.Versions }, { "validator", PaymentReviewActionSchemas.Validator() } });
        await data.Rows("schemaMigrations").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$gte", 46)));
        var receipts = await data.Rows("schemaMigrations").Find(new BsonDocument("_id", new BsonDocument("$lt", 46))).Sort(new BsonDocument("_id", 1)).ToListAsync();
        await LegacyOrderSubmissionTestSchema.UpgradeV46Async(db, data.Read.Catalog.Clock);
        foreach (var table in tables) Assert.Equal(before[table], await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        Assert.Equal(receipts, await data.Rows("schemaMigrations").Find(new BsonDocument("_id", new BsonDocument("$lt", 46))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        await new CollectionMigrations(db, data.Read.Catalog.Clock).ApplyAsync(); Assert.Equal("idle", await processor.RunOnceAsync());
        var visible = await data.Read.Payment.Service.ReadFollowUpAsync(data.Read.Actor, data.Read.DeclarationId, 1);
        Assert.Equal("rejected", visible.State); Assert.Null(visible.PublicExplanation); Assert.Equal("available", visible.Notification.State);
    }
}
