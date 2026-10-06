using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;
/// <summary>Empty suffix rehearsals only. Populated predecessor acceptance uses separately
/// retained native producer binaries and capsules; these checks never substitute for it.</summary>
[Collection("Mongo")]
public sealed class AfterSalesCausalCoverageMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty98_suffix_rehearsal_adds_exact_causal_owners_without_rewriting_retained_storage_and_replays()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); await EmptySuffixAsync(data);
        var before = await SnapshotAsync(data.Database);
        var migrations = new CollectionMigrations(data.Database, data.Clock); await migrations.ApplyAsync(); Assert.True(await migrations.IsCurrentAsync());
        var after = await SnapshotAsync(data.Database);
        foreach (var field in before) Assert.Equal(field.Value, after[field.Name]);
        Assert.Equal(before.ElementCount + AfterSalesCausalCoverageSchemas.Collections.Length, after.ElementCount);
        foreach (var name in AfterSalesCausalCoverageSchemas.Collections) {
            Assert.Empty(after[name]["rows"].AsBsonArray);
            var definition = await AccountContactStorage.DefinitionAsync(data.Database, name, default);
            Assert.True(AccountContactStorage.SafeDefinition(definition!, AfterSalesCausalCoverageSchemas.Validator(name)));
        }
        await AfterSalesStockSourceStorage.RequireCoverageAsync(data.Database, default);
        var history = await Rows(data.Database, "schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal("applied", history.Single(row => row["_id"] == 99L)["state"]);
        await migrations.ApplyAsync(); Assert.Equal(after, await SnapshotAsync(data.Database));
        Assert.Equal(history, await Rows(data.Database, "schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    }
    [Theory]
    [InlineData("spent-counter")]
    [InlineData("unsafe-uniqueness")]
    public async Task Prospective_causal_owner_failure_is_detected_before_DDL_without_retained_repair(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); await EmptySuffixAsync(data);
        if (fault == "spent-counter") await new IdAllocator(data.Database).NextAsync(AfterSalesStockSourceRecords.CausalTransitions);
        else {
            var name = AfterSalesStockSourceRecords.CausalHeads;
            await data.Database.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument> {
                Validator = AfterSalesCausalCoverageSchemas.Validator(name), ValidationAction = DocumentValidationAction.Error, ValidationLevel = DocumentValidationLevel.Strict });
            await Rows(data.Database, name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("setupDecisionId", 1), new CreateIndexOptions { Name = "unsafe_extra_unique", Unique = true }));
        }
        var before = await SnapshotAsync(data.Database);
        Assert.NotNull(await Record.ExceptionAsync(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync()));
        Assert.Equal(before, await SnapshotAsync(data.Database));
        var history = await Rows(data.Database, "schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(99, history.Count); Assert.Equal("applying", history[^1]["state"]); Assert.True(history.Take(98).All(row => row["state"] == "applied"));
        Assert.DoesNotContain(AfterSalesStockSourceRecords.CausalTransitions, await (await data.Database.ListCollectionNamesAsync()).ToListAsync());
    }
    private static async Task EmptySuffixAsync(StoreTestData data)
    {
        foreach (var name in new[] { AfterSalesStockSourceRecords.CausalHeads, AfterSalesStockSourceRecords.CausalTransitions,
            AfterSalesCaseRecords.Collection, AfterSalesDecisionRecords.Collection, AfterSalesStockSourceRecords.Heads, AfterSalesStockSourceRecords.Transitions })
            Assert.False(await Rows(data.Database, name).Find(new BsonDocument()).AnyAsync());
        foreach (var name in AfterSalesCausalCoverageSchemas.Collections) await data.Database.DropCollectionAsync(name);
        await Rows(data.Database, "schemaMigrations").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$gte", 99L)));
        Assert.Equal(98L, await Rows(data.Database, "schemaMigrations").CountDocumentsAsync(new BsonDocument()));
    }
    private static IMongoCollection<BsonDocument> Rows(IMongoDatabase database, string name) => database.GetCollection<BsonDocument>(name);
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase database)
    {
        var result = new BsonDocument(); var definitions = await (await database.ListCollectionsAsync()).ToListAsync();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection" && row["name"] != "schemaMigrations" && row["name"] != "migrationLocks").OrderBy(row => row["name"].AsString)) {
            var name = definition["name"].AsString;
            result.Add(name, new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray(await (await Rows(database, name).Indexes.ListAsync()).ToListAsync()) },
                { "rows", new BsonArray(await Rows(database, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } });
        }
        return result;
    }
}
