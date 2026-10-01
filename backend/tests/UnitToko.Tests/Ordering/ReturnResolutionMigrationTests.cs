using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using P = UnitToko.Platform.Fulfillment.ReturnResolutionRecords;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class ReturnResolutionMigrationTests(MongoFixture fixture)
{
    private static async Task<List<BsonDocument>> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task PrepareV50Async(PaymentDraftData data) {
        foreach (var table in PaymentAllocationCorrectionSchemas.Collections) { Assert.Equal(0, await data.Invoice.Count(table)); await data.Invoice.Order.Database.DropCollectionAsync(table); }
        await data.Invoice.Rows("schemaMigrations").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$gte", 51))); await data.Invoice.Order.Database.DropCollectionAsync(P.Collection);
    }
    [Fact]
    public async Task Actual_populated_inspection_and_stronger_proposal_rules_survive_forward_upgrade_and_empty_resume() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); await ReturnResolutionTests.Setup(data); var db = data.Invoice.Order.Database; var clock = data.Invoice.Order.Prices.Catalog.Clock;
        Assert.Equal(0, await data.Invoice.Count(P.Collection)); await PrepareV50Async(data);
        var original = await data.Invoice.Rows(ReturnInspectionRecords.Collection).Find(new BsonDocument()).SingleAsync(); var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999L));
        await db.CreateCollectionAsync(P.Collection, new CreateCollectionOptions<BsonDocument> { Validator = guard }); await LegacyOrderSubmissionTestSchema.UpgradeV51Async(db, clock);
        var definitions = await Definitions(db); var target = Assert.Single(definitions, row => row["name"] == P.Collection); Assert.True(InventorySchemas.Contains(target["options"]["validator"].AsBsonDocument, guard));
        Assert.Equal("strict", target["options"]["validationLevel"]); Assert.Equal("error", target["options"]["validationAction"]); Assert.Equal(original, await data.Invoice.Rows(ReturnInspectionRecords.Collection).Find(new BsonDocument()).SingleAsync());
        var indexes = await (await data.Invoice.Rows(P.Collection).Indexes.ListAsync()).ToListAsync(); Assert.Equal(2, indexes.Count); Assert.True(Assert.Single(indexes, row => row["name"] != "_id_")["unique"].AsBoolean);
        await data.Invoice.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 51)); await LegacyOrderSubmissionTestSchema.UpgradeV51Async(db, clock); Assert.Equal(definitions, await Definitions(db));
    }
    [Theory]
    [InlineData("ttl")] [InlineData("partial")] [InlineData("missing-inspection")] [InlineData("orphan-proposal")]
    public async Task Unsafe_targets_and_missing_inspection_or_orphan_proposal_effects_fail_before_new_DDL(string fault) {
        await using var data = await PaymentDraftData.CreateAsync(fixture); await ReturnResolutionTests.Setup(data); var db = data.Invoice.Order.Database;
        await PrepareV50Async(data);
        if (fault is "ttl" or "partial") {
            await db.CreateCollectionAsync(P.Collection); await data.Invoice.Rows(P.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("requestedAt", 1), fault == "ttl" ? new CreateIndexOptions { ExpireAfter = TimeSpan.FromMinutes(1) } : new CreateIndexOptions<BsonDocument> { PartialFilterExpression = new BsonDocument("state", "resolution_requested") }));
        }
        if (fault == "missing-inspection") await data.Invoice.Rows(ReturnInspectionRecords.Collection).DeleteManyAsync(new BsonDocument());
        if (fault == "orphan-proposal") { var orphan = await data.Invoice.Rows("outboxEvents").Find(new BsonDocument("kind", ReturnInspectionRecords.Operation)).SingleAsync(); orphan["_id"] = ObjectId.GenerateNewId(); orphan["kind"] = P.Operation + ".orphan"; orphan["sourceKey"] = P.Operation + ":orphan"; await data.Invoice.Rows("outboxEvents").InsertOneAsync(orphan); }
        var before = await Definitions(db); Assert.NotNull(await Record.ExceptionAsync(() => LegacyOrderSubmissionTestSchema.UpgradeV51Async(db, data.Invoice.Order.Prices.Catalog.Clock))); Assert.Equal(before, await Definitions(db));
        Assert.False(await data.Invoice.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 51 }, { "state", "applied" } }).AnyAsync());
    }
}
