using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogPriceMigrationTests(MongoFixture fixture)
{
    private static async Task<BsonArray> History(CatalogPriceTestData data) =>
        new(await data.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());

    private static async Task<BsonDocument> Storage(CatalogPriceTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in CatalogPriceSchemas.Collections)
        {
            var definition = await (await data.Catalog.Database.ListCollectionsAsync(new ListCollectionsOptions {
                Filter = new BsonDocument("name", name) })).SingleOrDefaultAsync();
            if (definition is null) continue;
            result[name] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray(await (await data.Rows(name).Indexes.ListAsync()).ToListAsync()) } };
        }
        result["counters"] = new BsonArray(await data.Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }

    [Fact]
    public async Task Forward_upgrade_installs_strict_price_storage_without_rewriting_predecessors()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture, install: false);
        var before = await History(data); Assert.Equal(19, before.Count);
        var migration = new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock);
        await migration.ApplyAsync();
        var after = await History(data); Assert.Equal(CollectionMigrations.CurrentVersion, after.Count);
        Assert.Equal(before, new BsonArray(after.Take(19)));
        foreach (var name in CatalogPriceSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(data.Catalog.Database, name,
                CatalogPriceSchemas.Validator(name), CatalogPriceSchemas.Key(name), default);
        var storage = await Storage(data);
        await migration.ApplyAsync(); Assert.Equal(after, await History(data)); Assert.Equal(storage, await Storage(data));
        Assert.True(await migration.IsCurrentAsync());
        await data.ApproveAsync(await data.ProposeAsync());
    }

    [Fact]
    public async Task Foundation_price_without_independent_provenance_blocks_upgrade_before_target_DDL()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture, install: false);
        await data.Rows("priceVersions").InsertOneAsync(new BsonDocument {
            { "_id", 91000L }, { "schemaVersion", 1 }, { "skuId", data.SkuId }, { "version", 1L },
            { "currency", "IDR" }, { "unitPrice", new BsonDecimal128(12m) }, { "effectiveAt", data.Catalog.Clock.UtcNow.UtcDateTime } });
        var storage = await Storage(data); var rows = await data.StateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock).ApplyAsync());
        Assert.Equal(storage, await Storage(data)); Assert.Equal(rows, await data.StateAsync());
    }

    [Fact]
    public async Task Accepted_history_stronger_validator_and_exact_counter_bounds_survive_upgrade()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture, beforeMigration: true);
        await data.ApproveAsync(await data.ProposeAsync());
        await data.ApproveAsync(await data.ProposeAsync("second", "13000.00"), "second-review");
        var stronger = new BsonDocument("$and", new BsonArray {
            CatalogPriceSchemas.Validator(CatalogPriceBson.Proposals), new BsonDocument("input.currency", "IDR") });
        await data.Catalog.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", CatalogPriceBson.Proposals }, { "validator", stronger } });
        var counters = new[] { CatalogPriceBson.Proposals, CatalogPriceBson.Decisions, CatalogPriceBson.Versions, "auditEvents" };
        await data.Rows("idCounters").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(counters))));
        await data.Rows("personAccountBindings").DeleteManyAsync(new BsonDocument());
        await data.Rows("identities").UpdateManyAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("status", "suspended"));
        var before = await data.StateAsync();
        await new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock).ApplyAsync();
        Assert.Equal(CollectionMigrations.CurrentVersion, (await History(data)).Count); Assert.Equal(before, await data.StateAsync());
        var definition = await (await data.Catalog.Database.ListCollectionsAsync(new ListCollectionsOptions {
            Filter = new BsonDocument("name", CatalogPriceBson.Proposals) })).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
        foreach (var name in counters) {
            var highest = await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstAsync();
            Assert.Equal(highest["_id"].AsInt64 + 1, (await new IdAllocator(data.Catalog.Database).NextAsync(name)).Value);
        }
    }

    [Theory]
    [InlineData("create")] [InlineData("createIndexes")]
    public async Task Interrupted_price_install_resumes_original_lease_and_preserves_majority_DDL(string phase)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture, install: false);
        var before = await History(data); using var cancellation = new CancellationTokenSource();
        var selected = 0; var interrupted = 0; var writes = new List<BsonDocument>();
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "PriceMigrationCancel-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(e => {
                if (e.CommandName == phase && e.Command.GetElement(0).Value == CatalogPriceBson.Decisions) {
                    selected = e.RequestId; writes.Add(e.Command.DeepClone().AsBsonDocument);
                }
            });
            builder.Subscribe<CommandSucceededEvent>(e => {
                if (e.CommandName == phase && e.RequestId == selected && Interlocked.CompareExchange(ref interrupted, 1, 0) == 0) cancellation.Cancel();
            });
        };
        var db = new MongoClient(settings).GetDatabase(data.Catalog.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(db, data.Catalog.Clock).ApplyAsync(cancellation.Token));
        Assert.Equal(1, interrupted); Assert.NotEmpty(writes);
        Assert.All(writes, command => Assert.Equal("majority", command["writeConcern"]["w"].AsString));
        var partial = await History(data); Assert.Equal(before, new BsonArray(partial.Take(19)));
        Assert.Equal("applying", partial[19]["state"].AsString);
        data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddMinutes(11);
        await new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock).ApplyAsync();
        var after = await History(data); Assert.Equal(before, new BsonArray(after.Take(19)));
        Assert.Equal("applied", after[19]["state"].AsString); Assert.Equal(partial[19]["startedAt"], after[19]["startedAt"]);
        foreach (var name in CatalogPriceSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(data.Catalog.Database, name, CatalogPriceSchemas.Validator(name), CatalogPriceSchemas.Key(name), default);
    }

    [Theory]
    [InlineData("missing-audit")] [InlineData("wrong-receipt")] [InlineData("orphan-head")]
    [InlineData("missing-head")] [InlineData("wrong-price")] [InlineData("missing-definition")]
    [InlineData("counter-exhausted")] [InlineData("unsafe-unique-index")]
    public async Task Invalid_existing_graph_or_storage_never_partially_changes_target_collections(string fault)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture, beforeMigration: true);
        var accepted = await data.ApproveAsync(await data.ProposeAsync());
        if (fault == "missing-audit") await data.Rows("auditEvents").DeleteManyAsync(new BsonDocument("kind", "catalog.price.command"));
        if (fault == "wrong-receipt") await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument("key", "price-approve"), Builders<BsonDocument>.Update.Set("result.priceVersion", 2L));
        if (fault == "orphan-head") await data.Rows(CatalogPriceBson.Heads).UpdateOneAsync(new BsonDocument("_id", data.SkuId), Builders<BsonDocument>.Update.Set("priceId", 9999L));
        if (fault == "missing-head") await data.Rows(CatalogPriceBson.Heads).DeleteOneAsync(new BsonDocument("_id", data.SkuId));
        if (fault == "wrong-price") await data.Rows(CatalogPriceBson.Versions).UpdateOneAsync(new BsonDocument("_id", accepted["priceId"]), Builders<BsonDocument>.Update.Set("unitPrice", new BsonDecimal128(9m)));
        if (fault == "missing-definition") await data.Rows("variantDefinitions").DeleteManyAsync(new BsonDocument("skuId", data.SkuId));
        if (fault == "counter-exhausted") await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", CatalogPriceBson.Proposals), Builders<BsonDocument>.Update.Set("value", long.MaxValue));
        if (fault == "unsafe-unique-index") await data.Rows(CatalogPriceBson.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("skuId", 1), new CreateIndexOptions { Unique = true, Name = "wrong_additional_uniqueness" }));
        var storage = await Storage(data); var rows = await data.StateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock).ApplyAsync());
        Assert.Equal(storage, await Storage(data)); Assert.Equal(rows, await data.StateAsync());
    }
}
