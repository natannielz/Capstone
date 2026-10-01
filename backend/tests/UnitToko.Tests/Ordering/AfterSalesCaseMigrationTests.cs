using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class AfterSalesCaseMigrationTests(MongoFixture fixture)
{
    private static async Task<BsonDocument[]> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
    private async Task Run(Func<IMongoDatabase, Task> action) {
        var name = "ut_case_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV45Async(db, new SystemClock()); await LegacyOrderSubmissionTestSchema.UpgradeV46Async(db, new SystemClock()); await action(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    [Fact]
    public Task Empty_forward_install_preserves_previous_definitions_receipts_and_stronger_rules_and_resumes_interruption() => Run(async db => {
        var extra = new BsonDocument("requestKey", new BsonDocument("$ne", "operator-blocked-case"));
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", AfterSalesCaseRecords.Collection }, { "validator", extra } });
        var before = await Definitions(db); var receipts = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        await new CollectionMigrations(db, new SystemClock()).ApplyAsync(); var after = await Definitions(db);
        Assert.Equal(before.Where(row => row["name"] != AfterSalesCaseRecords.Collection), after.Where(row => row["name"] != AfterSalesCaseRecords.Collection));
        var validator = after.Single(row => row["name"] == AfterSalesCaseRecords.Collection)["options"]["validator"].AsBsonDocument;
        Assert.True(InventorySchemas.Contains(validator, extra)); Assert.True(InventorySchemas.Contains(validator, AfterSalesCaseSchemas.Validator()));
        Assert.Equal(receipts, (await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Take(46));
        // Isolated empty interrupted-install fixture; no production receipt is deleted or downgraded.
        await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 47));
        await new CollectionMigrations(db, new SystemClock()).ApplyAsync(); Assert.Equal(after, await Definitions(db));
    });
    [Theory]
    [InlineData("ttl")] [InlineData("legacy")] [InlineData("orphan")]
    public Task Unsafe_or_unconverted_case_history_blocks_before_target_DDL(string fault) => Run(async db => {
        if (fault == "ttl") { await db.CreateCollectionAsync(AfterSalesCaseRecords.Collection); await db.GetCollection<BsonDocument>(AfterSalesCaseRecords.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("requestedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) })); }
        else if (fault == "legacy") await db.GetCollection<BsonDocument>("cases").InsertOneAsync(new BsonDocument { { "_id", 99L }, { "schemaVersion", 1 } }, new InsertOneOptions { BypassDocumentValidation = true });
        else await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", AfterSalesCaseRecords.Operation }, { "payload", new BsonDocument("caseId", 99L) } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await Definitions(db); await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, new SystemClock()).ApplyAsync()); Assert.Equal(before, await Definitions(db));
        Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 47 }, { "state", "applied" } }).AnyAsync());
    });
}
