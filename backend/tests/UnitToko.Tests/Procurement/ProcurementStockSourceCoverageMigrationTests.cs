using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;

namespace UnitToko.Tests.Procurement;

// Explicit empty-suffix100 rehearsal in a random synthetic fixture. These facts do
// not claim a genuine old-binary predecessor, restore exercise or production cutover.
[Collection("Mongo")]
public sealed class ProcurementStockSourceCoverageMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty100_rehearsal_preserves_genuine_unbound_demand_every_retained_row_schema_index_counter_and_replay() {
        await using var data = await ProcurementRequestTestData.CreateAsync(fixture);
        var request = await data.SubmitAsync("coverage-retained-unbound-demand");
        await EmptySuffixAsync(data, keepHeads: true);
        var definition = await AccountContactStorage.DefinitionAsync(data.Database, ProcurementStockSourceCoverage.Heads, default); Assert.NotNull(definition);
        var stronger = new BsonDocument("$and", new BsonArray { definition["options"]["validator"].DeepClone(), new BsonDocument("_id", new BsonDocument("$gte", 1L)) });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", ProcurementStockSourceCoverage.Heads }, { "validator", stronger } });
        await data.Rows(ProcurementStockSourceCoverage.Heads).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("updatedAt", 1),
            new CreateIndexOptions { Name = "operator_procurement_coverage_time", Unique = false }));
        var before = await SnapshotAsync(data); var prefix = await HistoryAsync(data);
        var migrations = new CollectionMigrations(data.Database, data.Clock); Assert.False(await migrations.IsCurrentAsync());
        await migrations.ApplyAsync(); Assert.True(await migrations.IsCurrentAsync());
        await ProcurementStockSourceCoverage.RequireStorageAsync(data.Database, default);
        var after = await SnapshotAsync(data);
        foreach (var name in before.Names) Assert.Equal(before[name], after[name]);
        foreach (var name in ProcurementStockSourceCoverageSchemas.Collections) Assert.Empty(after[name]["rows"].AsBsonArray);
        Assert.Equal(request["requestId"], Assert.Single(after[ProcurementRequestBson.Requests]["rows"].AsBsonArray)["_id"]);
        var history = await HistoryAsync(data); Assert.Equal(prefix, new BsonArray(history.Take(99))); Assert.Equal("applied", history[99]["state"].AsString);
        Assert.Equal(stronger, after[ProcurementStockSourceCoverage.Heads]["definition"]["options"]["validator"]);
        await migrations.ApplyAsync(); Assert.Equal(after, await SnapshotAsync(data)); Assert.Equal(history, await HistoryAsync(data));
    }

    [Fact]
    public async Task Nonzero_prospective_counter_refuses_before_coverage_DDL_and_preserves_all_old_business_work() {
        await using var data = await ProcurementRequestTestData.CreateAsync(fixture);
        await data.SubmitAsync("coverage-bad-counter-retained-demand"); await EmptySuffixAsync(data, keepHeads: false);
        await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", ProcurementStockSourceCoverage.Transitions }, { "value", 1L } });
        var before = await SnapshotAsync(data); var prefix = await HistoryAsync(data);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync());
        Assert.Equal(before, await SnapshotAsync(data));
        var failed = await HistoryAsync(data); Assert.Equal(prefix, new BsonArray(failed.Take(99))); Assert.Equal("applying", failed[99]["state"].AsString);
        foreach (var name in ProcurementStockSourceCoverageSchemas.Collections) Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, name, default));
    }

    [Fact]
    public async Task Missing_actual_command_receipt_refuses_before_DDL_without_repair_or_reclassification() {
        await using var data = await ProcurementRequestTestData.CreateAsync(fixture);
        await data.SubmitAsync("coverage-orphan-demand"); await EmptySuffixAsync(data, keepHeads: false);
        var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", ProcurementRequestBson.Prefix + "submit" },
            { "key", "coverage-orphan-demand" } }).SingleAsync();
        Assert.Equal(1L, (await data.Rows("idempotencyRecords").DeleteOneAsync(receipt)).DeletedCount);
        var before = await SnapshotAsync(data); var prefix = await HistoryAsync(data);
        await Assert.ThrowsAnyAsync<Exception>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync());
        Assert.Equal(before, await SnapshotAsync(data));
        var failed = await HistoryAsync(data); Assert.Equal(prefix, new BsonArray(failed.Take(99))); Assert.Equal("applying", failed[99]["state"].AsString);
        foreach (var name in ProcurementStockSourceCoverageSchemas.Collections) Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, name, default));
    }

    private static async Task EmptySuffixAsync(ProcurementRequestTestData data, bool keepHeads) {
        var history = await HistoryAsync(data); Assert.True(CollectionMigrations.CurrentVersion >= 100);
        Assert.Equal(CollectionMigrations.CurrentVersion, history.Count); Assert.True(MigrationHistory.IsValid(history.Select(row => row.AsBsonDocument).ToArray(), history.Count));
        Assert.All(history, row => Assert.Equal("applied", row["state"].AsString));
        foreach (var name in ProcurementStockSourceCoverageSchemas.Collections) {
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        // Only the specifically known empty101/102 suffix is permitted. Preserve its actual
        // collection definitions/indexes; only this fixture's empty step history is rehearsed.
        var currentVersion = CollectionMigrations.CurrentVersion;
        Assert.InRange(currentVersion, 100, 102);
        if (currentVersion >= 101)
            foreach (var name in new[] { "inventoryCountCoverageHeads", "inventoryCountCoverageMembers" }) {
                Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
                Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
            }
        if (currentVersion >= 102)
            foreach (var name in StockSourceActivationSchemas.Collections) {
                Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
                Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
            }
        foreach (var name in ProcurementStockSourceCoverageSchemas.Collections.Where(name => !keepHeads || name != ProcurementStockSourceCoverage.Heads))
            await data.Database.DropCollectionAsync(name);
        foreach (var row in history.Where(row => row["_id"].AsInt32 >= 100).Reverse())
            Assert.Equal(1L, (await data.Rows("schemaMigrations").DeleteOneAsync(row.AsBsonDocument)).DeletedCount);
    }
    private static async Task<BsonArray> HistoryAsync(ProcurementRequestTestData data) =>
        new(await data.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> SnapshotAsync(ProcurementRequestTestData data) {
        var result = new BsonDocument(); var definitions = await (await data.Database.ListCollectionsAsync()).ToListAsync();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection" && row["name"] != "schemaMigrations").OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; var rows = data.Rows(name);
            result[name] = new BsonDocument { { "definition", definition }, { "indexes", new BsonArray(await (await rows.Indexes.ListAsync()).ToListAsync()) },
                { "rows", new BsonArray(await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
}
