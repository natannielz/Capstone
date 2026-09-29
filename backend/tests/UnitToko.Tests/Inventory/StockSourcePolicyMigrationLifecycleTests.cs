using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourcePolicyMigrationTests
{
    [Fact]
    public async Task Stronger_validation_equivalent_indexes_and_inactive_historical_participants_are_preserved()
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        var stronger = new BsonDocument("$and", new BsonArray { StockSourcePolicySchemas.Validator(StockSourcePolicyBson.Proposals), new BsonDocument("_id", new BsonDocument("$ne", 999999L)) });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", StockSourcePolicyBson.Proposals }, { "validator", stronger } });
        var lookup = StockSourcePolicySchemas.LookupIndexes()[0];
        await Rows(data, lookup.Collection).Indexes.DropOneAsync(lookup.Name);
        await Rows(data, lookup.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(lookup.Key, new CreateIndexOptions { Name = "reviewed_policy_lookup" }));
        await Rows(data, "personAccountBindings").DeleteManyAsync(new BsonDocument());
        foreach (var name in new[] { "identities", "authorizationScopes", "memberships", "authorizationGrants" })
            await Rows(data, name).UpdateManyAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("status", "suspended"));
        var before = await Business(data); var writes = new List<BsonDocument>(); await Observed(data, writes).ApplyAsync();
        Assert.NotEmpty(writes); Assert.All(writes, command => Assert.Equal("majority", command["writeConcern"]["w"].AsString));
        Assert.Equal(before, await Business(data));
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", StockSourcePolicyBson.Proposals) })).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
        Assert.Contains(await (await Rows(data, StockSourcePolicyBson.Policies).Indexes.ListAsync()).ToListAsync(), index => index["name"] == "reviewed_policy_unique");
        Assert.Contains(await (await Rows(data, lookup.Collection).Indexes.ListAsync()).ToListAsync(), index => index["name"] == "reviewed_policy_lookup");
        var history = await History(data); writes.Clear(); await Observed(data, writes).ApplyAsync();
        Assert.Empty(writes); Assert.Equal(history, await History(data));
    }
    [Theory]
    [InlineData("create")] [InlineData("createIndexes")]
    public async Task Interrupted_policy_ddl_resumes_original_migration_history_with_majority_writes(string phase)
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        var before = await History(data); using var cancellation = new CancellationTokenSource();
        var selected = 0; var interrupted = 0; var writes = new List<BsonDocument>();
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "PolicyMigrationCancel-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == phase && e.Command.GetElement(0).Value == StockSourcePolicyBson.Decisions) { selected = e.RequestId; writes.Add(e.Command.DeepClone().AsBsonDocument); }
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.CommandName == phase && e.RequestId == selected && Interlocked.CompareExchange(ref interrupted, 1, 0) == 0) cancellation.Cancel();
            });
        };
        var db = new MongoClient(settings).GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(db, data.Clock).ApplyAsync(cancellation.Token));
        Assert.Equal(1, interrupted); Assert.Single(writes); Assert.Equal("majority", writes[0]["writeConcern"]["w"].AsString);
        var partial = await History(data); Assert.Equal(before, new BsonArray(partial.Take(18))); Assert.Equal("applying", partial[18]["state"].AsString);
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(11); writes.Clear(); await Observed(data, writes).ApplyAsync();
        Assert.All(writes, command => Assert.Equal("majority", command["writeConcern"]["w"].AsString));
        var after = await History(data); Assert.Equal(before, new BsonArray(after.Take(18))); Assert.Equal("applied", after[18]["state"].AsString);
        Assert.Equal(partial[18]["startedAt"], after[18]["startedAt"]); Assert.True(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
        foreach (var name in StockSourcePolicySchemas.Collections)
            await InventoryRepository.RequireStorageAsync(data.Database, name, StockSourcePolicySchemas.Validator(name), StockSourcePolicySchemas.Key(name), default);
    }
    [Fact]
    public async Task Competing_live_policy_migration_lease_prevents_target_changes()
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        var lease = await new MigrationGate(data.Database, data.Clock, 19).AcquireAsync(19, default); Assert.NotNull(lease);
        try
        {
            var before = await History(data); var writes = new List<BsonDocument>();
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => Observed(data, writes).ApplyAsync());
            Assert.Equal("migration_in_progress", error.Code); Assert.Empty(writes); Assert.Equal(before, await History(data));
            foreach (var name in StockSourcePolicySchemas.Collections)
                Assert.False(await (await data.Database.ListCollectionNamesAsync(new ListCollectionNamesOptions { Filter = new BsonDocument("name", name) })).AnyAsync());
        }
        finally { await lease.ReleaseAsync(); }
    }
    [Theory]
    [InlineData("hidden")] [InlineData("sparse")] [InlineData("partial")] [InlineData("nonunique")]
    public async Task Unsafe_policy_version_index_is_not_silently_replaced(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        await Rows(data, StockSourcePolicyBson.Policies).Indexes.DropOneAsync("reviewed_policy_unique");
        var options = new CreateIndexOptions<BsonDocument> {
            Name = "altered_policy_version", Unique = fault != "nonunique", Hidden = fault == "hidden", Sparse = fault == "sparse"
        };
        if (fault == "partial") options.PartialFilterExpression = new BsonDocument("poolId", new BsonDocument("$gt", 0L));
        await Rows(data, StockSourcePolicyBson.Policies).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(StockSourcePolicySchemas.Key(StockSourcePolicyBson.Policies), options));
        await Reject(data);
    }
    [Fact]
    public async Task Populated_exact_lots_stock_and_accounting_survive_policy_upgrade()
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data);
        await UnitToko.Tests.Accounting.AccountingTestDocuments.ConfigureAsync(data.Database);
        await Rows(data, "journalEntries").InsertOneAsync(UnitToko.Tests.Accounting.AccountingTestDocuments.Journal());
        await InventoryGraphTests.InstallBalances(data); await Rows(data, "lots").InsertOneAsync(InventoryGraphTests.Lot());
        var balance = InventoryBson.Balance(new InventoryBalanceDocument(7000, actors.Pool,
            new(new(Owner, new(200), new(201), new(202)), 9007199254740993, 2, 3, 4, 1)));
        await Rows(data, "stockBalances").InsertOneAsync(balance);
        var before = await Business(data); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await Business(data)); Assert.Equal(balance, await Rows(data, "stockBalances").Find(new BsonDocument("_id", 7000L)).SingleAsync());
    }
    [Theory]
    [InlineData("auditEvents")] [InlineData("idempotencyRecords")]
    public async Task Unrelated_policy_named_fields_do_not_become_inventory_records(string collection)
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        var row = collection == "auditEvents" ? new BsonDocument {
            { "_id", 99000L }, { "schemaVersion", 1 }, { "kind", "synthetic.other.policy.audit" }, { "operation", "synthetic.other.policy" },
            { "actorId", 1100L }, { "actorGrantVersion", 1L }, { "createdAt", data.Clock.UtcNow.UtcDateTime },
            { "record", new BsonDocument { { "policyRevision", 1L }, { "scope", "unrelated" } } }
        } : new BsonDocument {
            { "schemaVersion", 1 }, { "actorId", 1100L }, { "operation", "synthetic.other.policy" }, { "key", "unrelated" },
            { "bodyHash", new string('a', 64) }, { "createdAt", data.Clock.UtcNow.UtcDateTime },
            { "result", new BsonDocument { { "policyId", 88000L }, { "policyRevision", 1L } } }
        };
        await Rows(data, collection).InsertOneAsync(row);
        var before = await Business(data);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await Business(data));
    }
}