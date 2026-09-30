using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class PaymentReplacementMigrationTests(MongoFixture fixture)
{
    private async Task Run(Func<IMongoDatabase, Task> action) {
        var name = "ut_payment_replacement_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV43Async(db, new SystemClock()); await action(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonDocument[]> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
    [Fact]
    public Task Only_the_owning_version_schema_is_extended_with_custom_constraints_and_interrupted_DDL_is_recoverable() => Run(async db => {
        var extra = new BsonDocument("recordedBy", new BsonDocument("$ne", 99L));
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", PaymentDraftBson.Versions }, { "validator", new BsonDocument("$and", new BsonArray { PaymentDraftSchemas.Validator(PaymentDraftBson.Versions), extra }) } });
        var before = await Definitions(db); var receipts = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        await LegacyOrderSubmissionTestSchema.UpgradeV44Async(db, new SystemClock()); var after = await Definitions(db);
        foreach (var row in before.Where(row => row["name"] != PaymentDraftBson.Versions)) Assert.Equal(row, after.Single(value => value["name"] == row["name"]));
        var validator = after.Single(row => row["name"] == PaymentDraftBson.Versions)["options"]["validator"].AsBsonDocument;
        Assert.True(InventorySchemas.Contains(validator, extra)); Assert.True(InventorySchemas.Contains(validator, PaymentReplacementSchemas.Validator()));
        Assert.Equal(receipts, (await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Take(43));
        var indexes = await (await db.GetCollection<BsonDocument>(PaymentDraftBson.Versions).Indexes.ListAsync()).ToListAsync();
        var index = Assert.Single(indexes, row => row["key"].Equals(PaymentReplacementSchemas.Key())); Assert.True(index["unique"].AsBoolean); Assert.Equal(PaymentReplacementSchemas.Partial(), index["partialFilterExpression"]);
        await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 44));
        await LegacyOrderSubmissionTestSchema.UpgradeV44Async(db, new SystemClock()); Assert.Equal(after, await Definitions(db));
        Assert.Equal(indexes, await (await db.GetCollection<BsonDocument>(PaymentDraftBson.Versions).Indexes.ListAsync()).ToListAsync());
        Assert.Equal(44, await db.GetCollection<BsonDocument>("schemaMigrations").CountDocumentsAsync(new BsonDocument("state", "applied")));
    });
    [Theory]
    [InlineData("ttl")] [InlineData("unique")] [InlineData("partial")] [InlineData("orphan")] [InlineData("counter")]
    public Task Unsafe_index_or_orphan_effect_or_counter_does_not_issue_a_migration_receipt_or_change_storage(string fault) => Run(async db => {
        var rows = db.GetCollection<BsonDocument>(PaymentDraftBson.Versions);
        if (fault == "ttl") await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "unique") await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedBy", 1), new CreateIndexOptions { Unique = true }));
        if (fault == "partial") await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(PaymentReplacementSchemas.Key(), new CreateIndexOptions<BsonDocument> { Name = PaymentReplacementSchemas.IndexName, Unique = true, PartialFilterExpression = new BsonDocument("schemaVersion", 1) }));
        if (fault == "orphan") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", PaymentDraftBson.ReplacementOperation } }, new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", PaymentDraftBson.Versions }, { "value", 0 } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await Definitions(db); await Assert.ThrowsAsync<InvalidOperationException>(() => LegacyOrderSubmissionTestSchema.UpgradeV44Async(db, new SystemClock()));
        Assert.Equal(before, await Definitions(db)); Assert.Equal(43, await db.GetCollection<BsonDocument>("schemaMigrations").CountDocumentsAsync(new BsonDocument("state", "applied")));
        Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 44 }, { "state", "applied" } }).AnyAsync());
    });
}
