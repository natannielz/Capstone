using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Inventory;

/// <summary>Fresh actual registered102→103 unit fixtures with real catalog producers.
/// Not genuine pinned102 old-runtime acceptance. No later history or owner is removed.</summary>
[Collection("Mongo")]
public sealed class StockReleaseMigrationTests(MongoFixture fixture)
{
    private static string[] Owners => [.. StockReleaseSchemas.Collections, .. LotValueStore.Collections];
    private static BsonDocument Validator(string name) => StockReleaseSchemas.Collections.Contains(name)
        ? StockReleaseSchemas.Validator(name) : LotValueStore.Validator(name);
    private static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => StockReleaseSchemas.Collections.Contains(name)
        ? StockReleaseSchemas.Keys(name) : LotValueStore.Keys(name);

    [Fact]
    public async Task Fresh102_catalog_and_stronger_empty_owner_guards_survive103_with_UUIDs_options_indexes_counters_and_replay()
    {
        await using var data = await Fresh102Async("MIGRATION-RELEASE-12");
        await ProspectiveAsync(data.Database, StockReleaseBson.Proposals, keys: true);
        var definition = await AccountContactStorage.DefinitionAsync(data.Database, StockReleaseBson.Proposals, default); Assert.NotNull(definition);
        var stronger = new BsonDocument("$and", new BsonArray { definition["options"]["validator"].DeepClone(), new BsonDocument("_id", new BsonDocument("$gte", 1L)) });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", StockReleaseBson.Proposals }, { "validator", stronger } });
        await data.Rows(StockReleaseBson.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("reason", 1),
            new CreateIndexOptions { Name = "operator_release_reason_lookup", Unique = false }));
        await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", StockReleaseBson.Proposals }, { "value", 0L } });
        var before = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database);
        Assert.NotEmpty(before["skus"]["rows"].AsBsonArray); Assert.NotEmpty(before["auditEvents"]["rows"].AsBsonArray);
        Assert.NotEmpty(before["idempotencyRecords"]["rows"].AsBsonArray); Assert.Equal(102, history.Count);
        var gate = new MigrationGate(data.Database, data.Clock, 103); Assert.False(await gate.IsCurrentAsync(default));
        await StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 103); Assert.True(await gate.IsCurrentAsync(default));
        await StockReleaseSchemas.RequireStorageAsync(data.Database, default); await LotValueStore.RequireStorageAsync(data.Database, default);
        var after = await SnapshotAsync(data.Database);
        foreach (var name in before.Names) Assert.Equal(before[name], after[name]);
        Assert.Equal(before.ElementCount + Owners.Length - 1, after.ElementCount);
        foreach (var name in Owners) Assert.Empty(after[name]["rows"].AsBsonArray);
        Assert.Equal(stronger, (await AccountContactStorage.DefinitionAsync(data.Database, StockReleaseBson.Proposals, default))!["options"]["validator"]);
        var applied = await HistoryAsync(data.Database); Assert.Equal(103, applied.Count);
        Assert.Equal(history, new BsonArray(applied.Take(102))); Assert.Equal("applied", applied[102]["state"]);
        Assert.Equal(before["idCounters"], after["idCounters"]);
        await StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 103);
        AssertSnapshot(after, await SnapshotAsync(data.Database), "103 no-op replay"); Assert.Equal(applied, await HistoryAsync(data.Database));
        await NoPhysicalOrFinancialEffectsAsync(data);
    }

    [Theory]
    [InlineData("future-release-counter")]
    [InlineData("future-value-counter")]
    [InlineData("populated-owner")]
    public async Task Unsafe_prospective_counter_or_descriptive_value_row_refuses_before_any_target_DDL_without_retained_repair(string fault)
    {
        await using var data = await Fresh102Async("MIGRATION-UNSAFE-RELEASE-12");
        if (fault == "populated-owner") {
            await ProspectiveAsync(data.Database, LotValueStore.AvailableHeads, keys: false);
            // Explicit negative corruption only; no original source, lot, movement or value was produced.
            var key = InventoryGraphTests.Balance().Balance.Key;
            await data.Rows(LotValueStore.AvailableHeads).InsertOneAsync(new BsonDocument {
                { "_id", 999L }, { "schemaVersion", 1 }, { "kind", "inventory.lotvalue.available" }, { "poolId", 400L }, { "balanceId", 999L },
                { "key", LotValueStore.Key(key) }, { "remainingPackages", 0L }, { "remainingValue", "0.00" }, { "revision", 1L }, { "fence", 1L },
                { "lastTransitionId", 999L }, { "lastHash", new string('a', 64) }, { "updatedAt", data.Clock.UtcNow.UtcDateTime } });
        } else await data.Rows("idCounters").InsertOneAsync(new BsonDocument {
            { "_id", fault == "future-release-counter" ? StockReleaseBson.Decisions : LotValueStore.Transitions }, { "value", 1L } });
        var before = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 103));
        Assert.Equal(before, await SnapshotAsync(data.Database));
        var failed = await HistoryAsync(data.Database); Assert.Equal(103, failed.Count); Assert.Equal(history, new BsonArray(failed.Take(102)));
        Assert.Equal("applying", failed[102]["state"]);
        foreach (var name in Owners.Where(name => fault != "populated-owner" || name != LotValueStore.AvailableHeads))
            Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, name, default));
        await NoPhysicalOrFinancialEffectsAsync(data);
    }

    [Fact]
    public async Task Cancellation_after_actual_first_collection_create_preserves_partial_UUID_and_resumes_original103_lease_history()
    {
        await using var data = await Fresh102Async("MIGRATION-INTERRUPTED-RELEASE-12");
        var before = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database);
        using var cancellation = new CancellationTokenSource(); var request = 0; var observed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "StockReleaseMigration103Interrupted-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (request == 0 && command.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName && command.CommandName == "create" &&
                    command.Command.GetValue("create", BsonNull.Value) == StockReleaseBson.Proposals) request = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (request != 0 && command.RequestId == request && Interlocked.CompareExchange(ref observed, 1, 0) == 0) cancellation.Cancel();
            });
        };
        using var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StoreTestData.ApplyRegisteredThroughAsync(database, data.Clock, 103, cancellation.Token));
        Assert.Equal(1, observed);
        var partial = await AccountContactStorage.DefinitionAsync(data.Database, StockReleaseBson.Proposals, default); Assert.NotNull(partial);
        var partialHistory = await HistoryAsync(data.Database); Assert.Equal(103, partialHistory.Count);
        Assert.Equal(history, new BsonArray(partialHistory.Take(102))); Assert.Equal("applying", partialHistory[102]["state"]);
        var interrupted = await SnapshotAsync(data.Database);
        foreach (var name in before.Names) Assert.Equal(before[name], interrupted[name]);
        Assert.True(interrupted.ElementCount < before.ElementCount + Owners.Length);
        await StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 103);
        var after = await SnapshotAsync(data.Database);
        foreach (var name in before.Names) Assert.Equal(before[name], after[name]);
        var completed = await AccountContactStorage.DefinitionAsync(data.Database, StockReleaseBson.Proposals, default);
        Assert.Equal(partial["info"]["uuid"], completed!["info"]["uuid"]); Assert.Equal(partial["options"], completed["options"]);
        var applied = await HistoryAsync(data.Database); Assert.Equal(history, new BsonArray(applied.Take(102))); Assert.Equal("applied", applied[102]["state"]);
        foreach (var name in Owners) Assert.Empty(after[name]["rows"].AsBsonArray);
        await StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 103);
        AssertSnapshot(after, await SnapshotAsync(data.Database), "interrupted103 no-op replay"); Assert.Equal(applied, await HistoryAsync(data.Database));
        await NoPhysicalOrFinancialEffectsAsync(data);
    }

    private async Task<CatalogAuthoringTestData> Fresh102Async(string skuCode)
    {
        var source = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 102); CatalogAuthoringTestData data;
        try { data = await CatalogAuthoringTestData.AttachAsync(source, fixture.Client); }
        catch { await source.DisposeAsync(); throw; }
        await data.SkuAsync(12, skuCode, "RELMIG");
        var history = await HistoryAsync(data.Database); Assert.Equal(102, history.Count);
        Assert.True(MigrationHistory.IsValid(history.Select(value => value.AsBsonDocument).ToArray(), 102));
        Assert.All(history, value => Assert.Equal("applied", value["state"]));
        foreach (var name in Owners) {
            Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, name, default));
            Assert.Empty(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).ToListAsync());
        }
        return data;
    }
    private static async Task ProspectiveAsync(IMongoDatabase database, string name, bool keys)
    {
        await database.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument> {
            Validator = Validator(name), ValidationAction = DocumentValidationAction.Error, ValidationLevel = DocumentValidationLevel.Strict });
        if (keys) foreach (var key in Keys(name)) await database.GetCollection<BsonDocument>(name).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }));
    }
    private static async Task NoPhysicalOrFinancialEffectsAsync(CatalogAuthoringTestData data)
    {
        foreach (var name in new[] { "inventoryPools", "stockBalances", "lots", "movements", "journalEntries", "bankReceipts", "paymentAllocations" })
            Assert.Equal(0L, await data.CountAsync(name));
    }
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations")
        .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase database)
    {
        var result = new BsonDocument(); var definitions = await (await database.ListCollectionsAsync()).ToListAsync();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection" && row["name"] != "schemaMigrations").OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; var rows = database.GetCollection<BsonDocument>(name);
            result[name] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray(await (await rows.Indexes.ListAsync()).ToListAsync()) },
                { "rows", new BsonArray(await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
    private static void AssertSnapshot(BsonDocument expected, BsonDocument actual, string label)
    {
        var path = FirstDifference(expected, actual, "$" );
        Assert.True(path is null, label + ": " + path);
        // Preserve the original complete equality assertion after the bounded diagnosis.
        Assert.Equal(expected, actual);
    }
    private static string? FirstDifference(BsonValue expected, BsonValue actual, string path)
    {
        if (expected.BsonType != actual.BsonType) return path + $": expected {expected.BsonType}, actual {actual.BsonType}";
        if (expected.IsBsonDocument) {
            var left = expected.AsBsonDocument; var right = actual.AsBsonDocument;
            var names = left.Names.ToArray(); var actualNames = right.Names.ToArray();
            if (!names.SequenceEqual(actualNames, StringComparer.Ordinal))
                return path + ": field membership/order changed; expected " + string.Join(",", names.Take(20)) + "; actual " + string.Join(",", actualNames.Take(20));
            foreach (var name in names) {
                var difference = FirstDifference(left[name], right[name], path + "." + name); if (difference is not null) return difference;
            }
            return null;
        }
        if (expected.IsBsonArray) {
            var left = expected.AsBsonArray; var right = actual.AsBsonArray;
            if (left.Count != right.Count) return path + $": expected count {left.Count}, actual count {right.Count}";
            for (var index = 0; index < left.Count; index++) {
                var difference = FirstDifference(left[index], right[index], path + "[" + index + "]"); if (difference is not null) return difference;
            }
            return null;
        }
        if (expected == actual) return null;
        string Short(BsonValue value) { var text = value.ToJson(); return text.Length <= 240 ? text : text[..240] + "…"; }
        return path + ": expected " + Short(expected) + "; actual " + Short(actual);
    }
}
