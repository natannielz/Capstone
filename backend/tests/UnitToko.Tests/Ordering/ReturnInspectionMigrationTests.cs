using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using I = UnitToko.Platform.Inventory.ReturnInspectionRecords;
using Q = UnitToko.Platform.Inventory.ReturnQuarantineLedger;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class ReturnInspectionMigrationTests(MongoFixture fixture)
{
    private static async Task<List<BsonDocument>> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    [Fact]
    public async Task Populated_real_49_custody_graph_and_stronger_inspection_guard_survive_forward_upgrade_and_empty_resume() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var (_, _, _, _) = await ReturnInspectionTests.Setup(data); var db = data.Invoice.Order.Database; var clock = data.Invoice.Order.Prices.Catalog.Clock;
        Assert.Equal(0, await data.Invoice.Count(I.Collection)); Assert.Equal(0, await data.Invoice.Count(UnitToko.Platform.Fulfillment.ReturnResolutionRecords.Collection)); await db.DropCollectionAsync(UnitToko.Platform.Fulfillment.ReturnResolutionRecords.Collection);
        await data.Invoice.Rows("schemaMigrations").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$gte", 50))); await db.DropCollectionAsync(I.Collection);
        var original = await data.Invoice.Rows(Q.Collection).Find(new BsonDocument()).SingleAsync(); var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999L));
        await db.CreateCollectionAsync(I.Collection, new CreateCollectionOptions<BsonDocument> { Validator = guard });
        await LegacyOrderSubmissionTestSchema.UpgradeV50Async(db, clock); var definitions = await Definitions(db); var target = Assert.Single(definitions, row => row["name"] == I.Collection);
        Assert.True(InventorySchemas.Contains(target["options"]["validator"].AsBsonDocument, guard)); Assert.Equal("strict", target["options"]["validationLevel"]); Assert.Equal("error", target["options"]["validationAction"]);
        var indexes = await (await data.Invoice.Rows(I.Collection).Indexes.ListAsync()).ToListAsync(); Assert.Equal(2, indexes.Count); Assert.True(Assert.Single(indexes, row => row["name"] != "_id_")["unique"].AsBoolean);
        Assert.Equal(original, await data.Invoice.Rows(Q.Collection).Find(new BsonDocument()).SingleAsync());
        await data.Invoice.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 50)); await LegacyOrderSubmissionTestSchema.UpgradeV50Async(db, clock); Assert.Equal(definitions, await Definitions(db));
    }
    [Theory]
    [InlineData("ttl")] [InlineData("partial")] [InlineData("missing-custody")] [InlineData("orphan-inspection")]
    public async Task Unsafe_target_or_missing_real_custody_graph_fails_before_inspection_DDL(string fault) {
        await using var data = await PaymentDraftData.CreateAsync(fixture); await ReturnInspectionTests.Setup(data); var db = data.Invoice.Order.Database;
        Assert.Equal(0, await data.Invoice.Count(UnitToko.Platform.Fulfillment.ReturnResolutionRecords.Collection)); await db.DropCollectionAsync(UnitToko.Platform.Fulfillment.ReturnResolutionRecords.Collection);
        await data.Invoice.Rows("schemaMigrations").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$gte", 50))); await db.DropCollectionAsync(I.Collection);
        if (fault is "ttl" or "partial") {
            await db.CreateCollectionAsync(I.Collection); await data.Invoice.Rows(I.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("inspectedAt", 1),
                fault == "ttl" ? new CreateIndexOptions { ExpireAfter = TimeSpan.FromMinutes(1) } : new CreateIndexOptions<BsonDocument> { PartialFilterExpression = new BsonDocument("state", "inspected_quarantine") }));
        }
        if (fault == "missing-custody") await data.Invoice.Rows(Q.Collection).DeleteManyAsync(new BsonDocument());
        if (fault == "orphan-inspection") { var orphan = await data.Invoice.Rows("outboxEvents").Find(new BsonDocument("kind", Q.Operation)).SingleAsync(); orphan["_id"] = ObjectId.GenerateNewId(); orphan["kind"] = I.Operation + ".orphan"; orphan["sourceKey"] = I.Operation + ":orphan"; await data.Invoice.Rows("outboxEvents").InsertOneAsync(orphan); }
        var before = await Definitions(db); Assert.NotNull(await Record.ExceptionAsync(() => LegacyOrderSubmissionTestSchema.UpgradeV50Async(db, data.Invoice.Order.Prices.Catalog.Clock))); Assert.Equal(before, await Definitions(db));
        Assert.False(await data.Invoice.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 50 }, { "state", "applied" } }).AnyAsync()); Assert.Equal(0, await data.Invoice.Count(I.Collection));
    }
}
