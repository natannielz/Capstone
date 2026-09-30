using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class PaymentReviewActionMigrationTests(MongoFixture fixture)
{
    private async Task Run(Func<IMongoDatabase, Task> action) {
        var name = "ut_review_actions_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV42Async(db, new SystemClock()); await action(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonDocument[]> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
    [Fact]
    public Task Forward_empty_installation_preserves_predecessors_and_custom_constraints_and_resumes_after_DDL_interruption() => Run(async db => {
        var before = await Definitions(db); var history = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var extra = new BsonDocument("declarationId", new BsonDocument("$ne", 99L));
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", PaymentReviewActionRecords.Versions }, { "validator", extra } });
        await LegacyOrderSubmissionTestSchema.UpgradeV43Async(db, new SystemClock()); var after = await Definitions(db);
        foreach (var row in before) Assert.Equal(row, after.Single(value => value["name"] == row["name"]));
        Assert.Equal(history, (await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Take(42));
        var validator = after.Single(row => row["name"] == PaymentReviewActionRecords.Versions)["options"]["validator"].AsBsonDocument;
        Assert.True(InventorySchemas.Contains(validator, extra)); Assert.True(InventorySchemas.Contains(validator, PaymentReviewActionSchemas.Validator()));
        await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(PaymentReviewActionRecords.Versions).InsertOneAsync(new BsonDocument("_id", 1L)));
        await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 43));
        await LegacyOrderSubmissionTestSchema.UpgradeV43Async(db, new SystemClock()); Assert.Equal(after, await Definitions(db));
        Assert.Equal(43, await db.GetCollection<BsonDocument>("schemaMigrations").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await db.GetCollection<BsonDocument>(PaymentReviewActionRecords.Versions).CountDocumentsAsync(new BsonDocument()));
    });
    [Theory]
    [InlineData("rows")] [InlineData("ttl")] [InlineData("unique")] [InlineData("orphan")]
    public Task Unsafe_new_storage_does_not_mutate_predecessors_or_issue_a_deployment_receipt(string fault) => Run(async db => {
        var rows = db.GetCollection<BsonDocument>(PaymentReviewActionRecords.Versions);
        if (fault == "rows") await rows.InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "ttl") await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "unique") await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("actorId", 1), new CreateIndexOptions { Unique = true }));
        if (fault == "orphan") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", PaymentReviewActionRecords.Operation } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await Definitions(db); await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, new SystemClock()).ApplyAsync());
        Assert.Equal(before, await Definitions(db)); Assert.Equal(42, await db.GetCollection<BsonDocument>("schemaMigrations").CountDocumentsAsync(new BsonDocument("state", "applied")));
        Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 43 }, { "state", "applied" } }).AnyAsync());
    });
}
