using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Authorization;
using B = UnitToko.Platform.Inventory.PackConversionRuleBson;

namespace UnitToko.Tests.Inventory;

// Fresh registered96→97 migration unit fixtures. These are not populated preserved-binary
// predecessor acceptance; no later migration history or produced owner is removed.
[Collection("Mongo")]
public sealed class PackConversionRuleMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty97_rehearsal_retains_real_catalog_rows_stronger_operator_guards_UUIDs_indexes_counters_and_replay()
    {
        await using var data = await Fresh96Async();
        await data.SkuAsync(12, "MIGRATION-RULE-12", "MIGRULE");
        await data.Database.CreateCollectionAsync(B.Proposals, new CreateCollectionOptions<BsonDocument> {
            Validator = PackConversionRuleSchemas.Validator(B.Proposals), ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error });
        foreach (var key in PackConversionRuleSchemas.Keys(B.Proposals))
            await data.Rows(B.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }));
        var definition = await AccountContactStorage.DefinitionAsync(data.Database, B.Proposals, default); Assert.NotNull(definition);
        var stronger = new BsonDocument("$and", new BsonArray { definition["options"]["validator"].DeepClone(), new BsonDocument("_id", new BsonDocument("$gte", 1L)) });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", B.Proposals }, { "validator", stronger } });
        await data.Rows(B.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("reason", 1),
            new CreateIndexOptions { Name = "operator_pack_rule_reason", Unique = false }));
        var retained = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database);
        var gate = new MigrationGate(data.Database, data.Clock, 97); Assert.False(await gate.IsCurrentAsync(default));
        await StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 97); Assert.True(await gate.IsCurrentAsync(default)); await PackConversionRuleStorage.RequireAsync(data.Database, default);
        var after = await SnapshotAsync(data.Database);
        foreach (var name in retained.Names) Assert.Equal(retained[name], after[name]);
        var applied = await HistoryAsync(data.Database); Assert.Equal(97, applied.Count);
        Assert.Equal(history, new BsonArray(applied.Take(96))); Assert.Equal("applied", applied[96]["state"].AsString);
        foreach (var name in PackConversionRuleSchemas.Collections) Assert.Equal(0L, await data.CountAsync(name));
        Assert.Equal(stronger, (await AccountContactStorage.DefinitionAsync(data.Database, B.Proposals, default))!["options"]["validator"]);
        await StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 97); Assert.Equal(after, await SnapshotAsync(data.Database)); Assert.Equal(applied, await HistoryAsync(data.Database));
        foreach (var name in new[] { "inventoryPools", "stockBalances", "lots", "movements", "journalEntries", "bankReceipts" }) Assert.Equal(0L, await data.CountAsync(name));
    }

    [Fact]
    public async Task Prospective_nonzero_owner_counter_refuses_before_any_target_DDL_or_retained_semantic_change()
    {
        await using var data = await Fresh96Async();
        await data.SkuAsync(12, "MIGRATION-UNSAFE-RULE-12", "BADRULE");
        // Negative corruption only: a future allocator cannot be adopted as an empty owner.
        await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", B.Decisions }, { "value", 1L } });
        var retained = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 97));
        Assert.Equal(retained, await SnapshotAsync(data.Database));
        var failed = await HistoryAsync(data.Database); Assert.Equal(history, new BsonArray(failed.Take(96))); Assert.Equal("applying", failed[96]["state"].AsString);
        foreach (var name in PackConversionRuleSchemas.Collections) Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, name, default));
    }

    private async Task<CatalogAuthoringTestData> Fresh96Async()
    {
        var source = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 96);
        // Native Mongo TTL uses wall time even when the business fixture clock is fixed.
        // Issue these fresh catalog sessions before capturing retained storage bytes.
        source.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        CatalogAuthoringTestData data;
        try { data = await CatalogAuthoringTestData.AttachAsync(source, fixture.Client); }
        catch { await source.DisposeAsync(); throw; }
        var history = await data.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(96, history.Count); Assert.True(MigrationHistory.IsValid(history, 96)); Assert.All(history, row => Assert.Equal("applied", row["state"].AsString));
        Assert.False(await new MigrationGate(data.Database, data.Clock, 97).IsCurrentAsync(default));
        foreach (var name in PackConversionRuleSchemas.Collections) {
            Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, name, default));
            Assert.Empty(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).ToListAsync());
        }
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            Assert.Empty(await data.Rows(table).Find(new BsonDocument(field, new BsonDocument("$in", new BsonArray { B.Propose, B.Decide }))).ToListAsync());
        return data;
    }
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations")
        .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase database) {
        var result = new BsonDocument(); var definitions = await (await database.ListCollectionsAsync()).ToListAsync();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection" && row["name"] != "schemaMigrations").OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; var rows = database.GetCollection<BsonDocument>(name);
            result[name] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray(await (await rows.Indexes.ListAsync()).ToListAsync()) },
                { "rows", new BsonArray(await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
}
