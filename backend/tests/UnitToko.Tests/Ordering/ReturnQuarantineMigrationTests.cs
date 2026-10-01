using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using Q = UnitToko.Platform.Inventory.ReturnQuarantineLedger;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class ReturnQuarantineMigrationTests(MongoFixture fixture)
{
    private static async Task<List<BsonDocument>> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    [Fact]
    public async Task Populated_48_history_and_stronger_target_validator_survive_majority_forward_upgrade_and_empty_interrupted_resume() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var (_, id, _) = await ReturnQuarantineTests.Setup(data);
        var db = data.Invoice.Order.Database; var clock = data.Invoice.Order.Prices.Catalog.Clock;
        Assert.Equal(0, await data.Invoice.Count(UnitToko.Platform.Inventory.ReturnInspectionRecords.Collection)); await db.DropCollectionAsync(UnitToko.Platform.Inventory.ReturnInspectionRecords.Collection);
        await data.Invoice.Rows("schemaMigrations").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$gte", 49))); await db.DropCollectionAsync(Q.Collection);
        var requests = await data.Invoice.Rows("afterSalesCases").Find(new BsonDocument()).ToListAsync(); var decisions = await data.Invoice.Rows("afterSalesCaseDecisions").Find(new BsonDocument()).ToListAsync();
        var guard = new BsonDocument("actorId", new BsonDocument("$gte", 1L)); await db.CreateCollectionAsync(Q.Collection, new CreateCollectionOptions<BsonDocument> { Validator = guard });
        await LegacyOrderSubmissionTestSchema.UpgradeV49Async(db, clock); var definition = Assert.Single(await Definitions(db), row => row["name"] == Q.Collection);
        Assert.True(InventorySchemas.Contains(definition["options"]["validator"].AsBsonDocument, guard)); Assert.Equal("strict", definition["options"]["validationLevel"]); Assert.Equal("error", definition["options"]["validationAction"]);
        var indexes = await (await data.Invoice.Rows(Q.Collection).Indexes.ListAsync()).ToListAsync(); Assert.Equal(3, indexes.Count); Assert.All(indexes.Where(row => row["name"] != "_id_"), row => Assert.True(row["unique"].AsBoolean));
        Assert.Equal(requests, await data.Invoice.Rows("afterSalesCases").Find(new BsonDocument()).ToListAsync()); Assert.Equal(decisions, await data.Invoice.Rows("afterSalesCaseDecisions").Find(new BsonDocument()).ToListAsync()); Assert.Equal(id, Assert.Single(decisions)["_id"].AsInt64);
        var before = await Definitions(db); await data.Invoice.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 49)); await LegacyOrderSubmissionTestSchema.UpgradeV49Async(db, clock); Assert.Equal(before, await Definitions(db));
    }
    [Theory]
    [InlineData("ttl")] [InlineData("partial")] [InlineData("orphan")] [InlineData("decision-history")] [InlineData("allocator")]
    public async Task Unsafe_target_indexes_or_incomplete_predecessor_effects_fail_before_custody_DDL(string fault) {
        await using var data = await PaymentDraftData.CreateAsync(fixture); await ReturnQuarantineTests.Setup(data); var db = data.Invoice.Order.Database;
        Assert.Equal(0, await data.Invoice.Count(UnitToko.Platform.Inventory.ReturnInspectionRecords.Collection)); await db.DropCollectionAsync(UnitToko.Platform.Inventory.ReturnInspectionRecords.Collection);
        await data.Invoice.Rows("schemaMigrations").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$gte", 49))); await db.DropCollectionAsync(Q.Collection);
        if (fault is "ttl" or "partial") {
            await db.CreateCollectionAsync(Q.Collection);
            await data.Invoice.Rows(Q.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("receivedAt", 1), fault == "ttl" ? new CreateIndexOptions { ExpireAfter = TimeSpan.FromMinutes(1) } : new CreateIndexOptions<BsonDocument> { PartialFilterExpression = new BsonDocument("state", "quarantined") }));
        }
        if (fault == "orphan") { var orphan = await data.Invoice.Rows("outboxEvents").Find(new BsonDocument("kind", "fulfillment.case.decide")).SingleAsync(); orphan["_id"] = ObjectId.GenerateNewId(); orphan["kind"] = Q.Operation + ".orphan"; orphan["sourceKey"] = Q.Operation + ":orphan"; await data.Invoice.Rows("outboxEvents").InsertOneAsync(orphan); }
        if (fault == "decision-history") await data.Invoice.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("kind", "fulfillment.case.decide"));
        if (fault == "allocator") await data.Invoice.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", Q.Collection }, { "value", long.MaxValue } });
        var before = await Definitions(db); Assert.NotNull(await Record.ExceptionAsync(() => LegacyOrderSubmissionTestSchema.UpgradeV49Async(db, data.Invoice.Order.Prices.Catalog.Clock))); Assert.Equal(before, await Definitions(db));
        Assert.False(await data.Invoice.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 49 }, { "state", "applied" } }).AnyAsync()); Assert.Equal(0, await data.Invoice.Count(Q.Collection));
    }
}
