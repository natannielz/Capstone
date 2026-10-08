using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderViewMigrationTests(MongoFixture fixture)
{
    private async Task<CatalogPriceTestData> Predecessor() {
        var data = await CatalogPriceTestData.CreateAsync(fixture, install: false);
        try {
            // Install the actual registered predecessor prefix, never synthetic migration markers.
            var migrations = new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock);
            var steps = migrations.RegisteredSteps().Where(step => step.Version <= 111).OrderBy(step => step.Version).ToArray();
            Assert.Equal(Enumerable.Range(1, 111), steps.Select(step => step.Version));
            foreach (var step in steps) {
                var lease = await new MigrationGate(data.Catalog.Database, data.Catalog.Clock, 112).AcquireAsync(step.Version, default);
                if (lease is null) continue; await step.ApplyAsync(lease, default); await lease.CompleteAsync(default);
            }
            return data;
        } catch {await data.DisposeAsync(); throw;}
    }
    private static async Task<BsonArray> History(CatalogPriceTestData data) => new(await data.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    [Fact]
    public async Task Exact_predecessor111_and_empty_partial_DDL_resume_without_rewriting_history_or_business_rows() {
        await using var data = await Predecessor(); var before = await History(data); Assert.Equal(111, before.Count);
        var stronger = new BsonDocument("$and", new BsonArray {OrderViewSchemas.Validator(OrderViewBson.Heads), new BsonDocument("createdBy", new BsonDocument("$gt", 0L))});
        await data.Catalog.Database.CreateCollectionAsync(OrderViewBson.Heads, new CreateCollectionOptions<BsonDocument> {Validator = stronger});
        var lease = await new MigrationGate(data.Catalog.Database, data.Catalog.Clock, 112).AcquireAsync(112, default); Assert.NotNull(lease);
        await new OrderViewMigration(data.Catalog.Database).ApplyAsync(lease, default);
        // A failure after DDL but before lease completion must be exactly resumable.
        await new OrderViewMigration(data.Catalog.Database).ApplyAsync(lease, default); await lease.CompleteAsync(default);
        Assert.Equal(before, new BsonArray((await History(data)).Take(111)));
        foreach (var table in OrderViewSchemas.Collections) {
            BsonDocument[] keys = table == OrderViewBson.Versions ? [OrderViewSchemas.Key(table), OrderViewSchemas.RequestKey()] : [OrderViewSchemas.Key(table)];
            await InventoryRepository.RequireStorageAsync(data.Catalog.Database, table, OrderViewSchemas.Validator(table), keys, default);
        }
        var definition = await (await data.Catalog.Database.ListCollectionsAsync(new ListCollectionsOptions {Filter = new BsonDocument("name", OrderViewBson.Heads)})).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]); Assert.Equal(0, await data.Catalog.CountAsync(OrderViewBson.Heads));
    }
    [Theory]
    [InlineData("data")] [InlineData("retention")] [InlineData("request-index")] [InlineData("counter")]
    public async Task Complete_preflight_refuses_ambiguous_existing_storage_before_mutating_any_other_target(string fault) {
        await using var data = await Predecessor(); await data.Catalog.Database.CreateCollectionAsync(OrderViewBson.Versions);
        if (fault == "data") await data.Rows(OrderViewBson.Versions).InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "retention") await data.Rows(OrderViewBson.Versions).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions {ExpireAfter = TimeSpan.FromHours(1)}));
        if (fault == "request-index") await data.Rows(OrderViewBson.Versions).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(OrderViewSchemas.RequestKey(), new CreateIndexOptions {Unique = false, Name = "ux_order_view_request"}));
        if (fault == "counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { {"_id", OrderViewBson.Heads}, {"value", long.MaxValue} });
        var before = await History(data); var lease = await new MigrationGate(data.Catalog.Database, data.Catalog.Clock, 112).AcquireAsync(112, default); Assert.NotNull(lease);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OrderViewMigration(data.Catalog.Database).ApplyAsync(lease, default));
        Assert.Equal(before, new BsonArray((await History(data)).Take(111)));
        Assert.False(await (await data.Catalog.Database.ListCollectionNamesAsync(new ListCollectionNamesOptions {Filter = new BsonDocument("name", OrderViewBson.Heads)})).AnyAsync());
    }
}
