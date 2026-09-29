using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderDraftMigrationTests(MongoFixture fixture)
{
    private async Task<CatalogPriceTestData> Predecessor() {
        var data = await CatalogPriceTestData.CreateAsync(fixture, install: false);
        try {
            var lease = await new MigrationGate(data.Catalog.Database, data.Catalog.Clock, 20).AcquireAsync(20, default);
            Assert.NotNull(lease); await new CatalogPriceMigration(data.Catalog.Database).ApplyAsync(lease, default); await lease.CompleteAsync(default); return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    private static async Task<BsonArray> History(CatalogPriceTestData data) => new(await data.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    [Fact]
    public async Task Forward_draft_storage_preserves_all_twenty_predecessors_and_resumes_empty_partial_DDL() {
        await using var data = await Predecessor(); var before = await History(data); Assert.Equal(20, before.Count);
        var stronger = new BsonDocument("$and", new BsonArray { OrderDraftSchemas.Validator(OrderDraftBson.Heads), new BsonDocument("context.organizationId", 10L) });
        await data.Catalog.Database.CreateCollectionAsync(OrderDraftBson.Heads, new CreateCollectionOptions<BsonDocument> { Validator = stronger });
        Assert.False(await new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock).IsCurrentAsync());
        await new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock).ApplyAsync();
        var after = await History(data); Assert.Equal(CollectionMigrations.CurrentVersion, after.Count); Assert.Equal(before, new BsonArray(after.Take(20)));
        foreach (var name in OrderDraftSchemas.Collections) await InventoryRepository.RequireStorageAsync(data.Catalog.Database, name, OrderDraftSchemas.Validator(name), OrderDraftSchemas.Key(name), default);
        var definition = await (await data.Catalog.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", OrderDraftBson.Heads) })).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
        await new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock).ApplyAsync(); Assert.Equal(after, await History(data));
        Assert.True(await new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock).IsCurrentAsync());
    }
    [Theory]
    [InlineData("data")] [InlineData("retention")] [InlineData("uniqueness")] [InlineData("counter")]
    public async Task Unexpected_draft_storage_blocks_migration_before_installing_any_other_target(string fault) {
        await using var data = await Predecessor(); var before = await History(data);
        await data.Catalog.Database.CreateCollectionAsync(OrderDraftBson.Heads);
        if (fault == "data") await data.Rows(OrderDraftBson.Heads).InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "retention") await data.Rows(OrderDraftBson.Heads).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("createdAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromHours(1) }));
        if (fault == "uniqueness") await data.Rows(OrderDraftBson.Heads).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("createdBy", 1), new CreateIndexOptions { Unique = true }));
        if (fault == "counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", OrderDraftBson.Heads }, { "value", long.MaxValue } });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock).ApplyAsync());
        Assert.Equal(before, new BsonArray((await History(data)).Take(20)));
        Assert.False(await (await data.Catalog.Database.ListCollectionNamesAsync(new ListCollectionNamesOptions { Filter = new BsonDocument("name", OrderDraftBson.Versions) })).AnyAsync());
        Assert.Equal(fault == "data" ? 1 : 0, await data.Catalog.CountAsync(OrderDraftBson.Heads));
        Assert.False(await new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock).IsCurrentAsync());
    }
}
