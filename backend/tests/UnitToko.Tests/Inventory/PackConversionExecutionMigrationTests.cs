using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Inventory;

/// <summary>Fresh registered103 fixtures only. Genuine pinned103 old-runtime
/// migration acceptance is a separate producer/consumer check; no history is rewound.</summary>
[Collection("Mongo")]
public sealed class PackConversionExecutionMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Registered104_keeps_real_catalog_rows_UUIDs_options_indexes_counters_and_replays_without_stock_value_effects() {
        await using var source = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 103);
        source.Clock.UseSystemClock = true;
        var catalog = await CatalogAuthoringTestData.AttachAsync(source, fixture.Client);
        await catalog.SkuAsync(12, "CONVERSION-MIGRATION-12", "CONVMIG");
        Assert.Null(await AccountContactStorage.DefinitionAsync(source.Database, PackConversionBson.Conversions, default));
        var before = await Snapshot(source.Database); var oldHistory = await History(source.Database); Assert.Equal(103, oldHistory.Count);
        await StoreTestData.ApplyRegisteredThroughAsync(source.Database, source.Clock, 104);
        var after = await Snapshot(source.Database);
        foreach (var name in before.Names) Assert.Equal(before[name], after[name]);
        Assert.Empty(after[PackConversionBson.Conversions]["rows"].AsBsonArray);
        var history = await History(source.Database); Assert.Equal(104, history.Count);
        Assert.Equal(oldHistory, new BsonArray(history.Take(103))); Assert.Equal("applied", history[103]["state"]);
        await StoreTestData.ApplyRegisteredThroughAsync(source.Database, source.Clock, 104);
        Assert.Equal(after, await Snapshot(source.Database)); Assert.Equal(history, await History(source.Database));
        foreach (var table in new[] { "movements", "stockBalances", "lots", "lotValueTransitions", "journals", "cashMovements" })
            Assert.Empty(await source.Database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).ToListAsync());
    }
    [Fact]
    public async Task Nonzero_prospective_counter_refuses_before_any_conversion_DDL_or_movement_rewrite() {
        await using var source = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 103);
        await source.Database.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new() { { "_id", PackConversionBson.Conversions }, { "value", 1L } });
        var before = await Snapshot(source.Database); var oldHistory = await History(source.Database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoreTestData.ApplyRegisteredThroughAsync(source.Database, source.Clock, 104));
        Assert.Equal(before, await Snapshot(source.Database));
        Assert.Null(await AccountContactStorage.DefinitionAsync(source.Database, PackConversionBson.Conversions, default));
        var history = await History(source.Database); Assert.Equal(104, history.Count);
        Assert.Equal(oldHistory, new BsonArray(history.Take(103))); Assert.Equal("applying", history[103]["state"]);
    }
    private static async Task<BsonArray> History(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations")
        .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> Snapshot(IMongoDatabase database) {
        var result = new BsonDocument();
        var definitions = await (await database.ListCollectionsAsync()).ToListAsync();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection" && row["name"] != "schemaMigrations").OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; var rows = database.GetCollection<BsonDocument>(name);
            result.Add(name, new BsonDocument { { "definition", definition }, { "indexes", new BsonArray(await (await rows.Indexes.ListAsync()).ToListAsync()) },
                { "rows", new BsonArray(await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } });
        }
        return result;
    }
}
