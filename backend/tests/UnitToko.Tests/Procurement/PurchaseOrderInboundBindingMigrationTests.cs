using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Procurement;

// Only random databases created by the retained fixture are touched.
[Collection("Mongo")]
public sealed class PurchaseOrderInboundBindingMigrationTests(MongoFixture fixture)
{
    [WindowsStorageFact(Skip = Retired.HistoricalUpgrade)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Genuine_unbound_PO_private_custody_and_all_retained_definitions_survive93_to94_without_binding_backfill_and_replay_is_exact() {
        await using var data = await PurchaseOrderInboundBindingTestData.CreateAsync(fixture);
        await RestoreEmptyV93Async(data.Data.Database);
        var po = data.Rows(PurchaseOrderTestData.Orders); await po.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("location.shopId", 1),
            new CreateIndexOptions { Name = "operator_m94_retained_shop", Unique = false }));
        var before = await SnapshotAsync(data.Data.Database); var previous = await HistoryAsync(data.Data.Database); Assert.Equal(93, previous.Count);
        var migrations = new CollectionMigrations(data.Data.Database, data.Data.Clock); await migrations.ApplyAsync(); Assert.True(await migrations.IsCurrentAsync());
        var after = await SnapshotAsync(data.Data.Database);
        foreach (var name in before.Names) Assert.Equal(before[name], after[name]);
        Assert.Equal(before.ElementCount + 2, after.ElementCount);
        foreach (var name in PurchaseOrderInboundBindingSchemas.Collections) Assert.Empty(after[name]["rows"].AsBsonArray);
        var history = await HistoryAsync(data.Data.Database); Assert.Equal(94, history.Count); Assert.Equal(previous, new BsonArray(history.Take(93)));
        await migrations.ApplyAsync(); Assert.Equal(history, await HistoryAsync(data.Data.Database)); Assert.Equal(after, await SnapshotAsync(data.Data.Database));
        var old = data.Data.Inspection.Receiving.Proposal;
        Assert.False((await data.Service.ReadAsync(data.Orders.Maker, old["proposalId"].AsInt64)).Contains("inboundPools"));
    }
    [WindowsStorageFact(Skip = Retired.HistoricalUpgrade)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Prospective_spent_counter_or_extra_unsafe_unique_index_refuses_before_target_DDL_or_semantic_changes() {
        foreach (var unsafeIndex in new[] { false, true }) {
        await using var data = await PurchaseOrderInboundBindingTestData.CreateAsync(fixture);
        await RestoreEmptyV93Async(data.Data.Database);
        var name = PurchaseOrderInboundBindingBson.Selections;
        if (unsafeIndex) {
            await data.Data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "validator", PurchaseOrderInboundBindingSchemas.Validator(name) },
                { "validationLevel", "strict" }, { "validationAction", "error" } });
            await data.Rows(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("actorId", 1),
                new CreateIndexOptions { Name = "unsafe_m94_extra_unique", Unique = true }));
        } else await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", name }, { "value", 1L } });
        var before = await SnapshotAsync(data.Data.Database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Data.Database, data.Data.Clock).ApplyAsync());
        Assert.Equal(before, await SnapshotAsync(data.Data.Database));
        Assert.False(await data.Rows(PurchaseOrderInboundBindingBson.Decisions).Find(new BsonDocument()).AnyAsync());
        }
    }
    internal static async Task RestoreEmptyV93Async(IMongoDatabase database) {
        Assert.Matches(@"\Aunittoko_test_[0-9a-f]{32}_[0-9a-f]{8}\z", database.DatabaseNamespace.DatabaseName);
        Assert.True(CollectionMigrations.CurrentVersion >= 94, "Schema 94 or later must be installed");
        var history = await HistoryAsync(database);
        if (history.Count == 93) {
            Assert.True(MigrationHistory.IsValid(history.Select(value => value.AsBsonDocument).ToArray(), 93));
            Assert.All(history, value => Assert.Equal("applied", value["state"].AsString));
            return;
        }
        Assert.Equal(94, history.Count); Assert.True(MigrationHistory.IsValid(history.Select(value => value.AsBsonDocument).ToArray(), 94));
        Assert.All(history, value => Assert.Equal("applied", value["state"].AsString));
        // Verify all targets first; never remove one and then discover another has
        // authentic data. No retained source/head/approval/private owner is touched.
        await PurchaseOrderInboundBindingStorage.RequireAsync(database, default);
        var emptyCounters = new List<BsonDocument>();
        foreach (var name in PurchaseOrderInboundBindingSchemas.Collections) {
            Assert.False(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            var counter = await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync();
            if (counter is not null) {
                Assert.Equal(new BsonDocument { { "_id", name }, { "value", 0L } }, counter);
                emptyCounters.Add(counter);
            }
        }
        var before = await SnapshotAsync(database);
        foreach (var name in PurchaseOrderInboundBindingSchemas.Collections) {
            await database.DropCollectionAsync(name);
            before.Remove(name);
        }
        foreach (var counter in emptyCounters)
            Assert.Equal(1L, (await database.GetCollection<BsonDocument>("idCounters").DeleteOneAsync(counter)).DeletedCount);
        before["idCounters"]["rows"] = new BsonArray(before["idCounters"]["rows"].AsBsonArray
            .Where(row => !PurchaseOrderInboundBindingSchemas.Collections.Contains(row["_id"].AsString)));
        Assert.Equal(1L, (await database.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(history[93].AsBsonDocument)).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(93)), await HistoryAsync(database));
        Assert.Equal(before, await SnapshotAsync(database));
    }
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations")
        .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase database) {
        var result = new BsonDocument();
        var definitions = await (await database.ListCollectionsAsync()).ToListAsync();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection" &&
            row["name"].AsString is not ("schemaMigrations" or "migrationLocks")).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; var rows = database.GetCollection<BsonDocument>(name);
            result[name] = new BsonDocument { { "definition", definition }, { "indexes", new BsonArray(await (await rows.Indexes.ListAsync()).ToListAsync()) },
                { "rows", new BsonArray(await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
}
