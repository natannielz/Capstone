using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSuspensionMigrationTests
{
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Stronger_validator_equivalent_indexes_and_historical_people_are_preserved()
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        var stronger = new BsonDocument("$and", new BsonArray { StockSourceSuspensionSchemas.Validator(), new BsonDocument("_id", new BsonDocument("$ne", 999999L)) });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", Collection }, { "validator", stronger } });
        var lookup = StockSourceSuspensionSchemas.LookupIndexes()[0];
        await Rows(data, lookup.Collection).Indexes.DropOneAsync(lookup.Name);
        await Rows(data, lookup.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(lookup.Key, new CreateIndexOptions { Name = "reviewed_stop_audit_lookup" }));
        await Rows(data, "personAccountBindings").DeleteManyAsync(new BsonDocument());
        foreach (var name in new[] { "identities", "authorizationScopes", "memberships", "authorizationGrants" })
            await Rows(data, name).UpdateManyAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("status", "suspended"));
        var before = await Business(data); var writes = new List<BsonDocument>(); await Observed(data, writes).ApplyAsync();
        Assert.NotEmpty(writes); Assert.All(writes, command => Assert.Equal("majority", command["writeConcern"]["w"].AsString));
        Assert.Equal(before, await Business(data));
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", Collection) })).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
        Assert.Contains(await (await Rows(data, Collection).Indexes.ListAsync()).ToListAsync(), index => index["name"] == "reviewed_suspension_version");
        Assert.Contains(await (await Rows(data, lookup.Collection).Indexes.ListAsync()).ToListAsync(), index => index["name"] == "reviewed_stop_audit_lookup");
        var history = await History(data); writes.Clear(); await Observed(data, writes).ApplyAsync(); Assert.Empty(writes); Assert.Equal(history, await History(data));
    }
    [Theory]
    [InlineData("create")] [InlineData("createIndexes")]
    public async Task Interrupted_actual_ddl_resumes_under_the_same_history_and_majority_writes(string phase)
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        var before = await History(data); using var cancellation = new CancellationTokenSource();
        var selected = 0; var interrupted = 0; var writes = new List<BsonDocument>();
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "StopMigrationCancel-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == phase && e.Command.GetElement(0).Value == Collection) { selected = e.RequestId; writes.Add(e.Command.DeepClone().AsBsonDocument); }
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.CommandName == phase && e.RequestId == selected && Interlocked.CompareExchange(ref interrupted, 1, 0) == 0) cancellation.Cancel();
            });
        };
        var db = new MongoClient(settings).GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(db, data.Clock).ApplyAsync(cancellation.Token));
        Assert.Equal(1, interrupted); Assert.Single(writes); Assert.Equal("majority", writes[0]["writeConcern"]["w"].AsString);
        var partial = await History(data); Assert.Equal(before, new BsonArray(partial.Take(17))); Assert.Equal("applying", partial[17]["state"].AsString);
        Assert.False(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(11); writes.Clear(); await Observed(data, writes).ApplyAsync();
        Assert.All(writes, command => Assert.Equal("majority", command["writeConcern"]["w"].AsString));
        var after = await History(data); Assert.Equal(before, new BsonArray(after.Take(17))); Assert.Equal("applied", after[17]["state"].AsString);
        Assert.Equal(partial[17]["startedAt"], after[17]["startedAt"]); Assert.True(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
        await InventoryRepository.RequireStorageAsync(data.Database, Collection, StockSourceSuspensionSchemas.Validator(), StockSourceSuspensionSchemas.Key(), default);
    }
    [Fact]
    public async Task Another_live_migration_lease_prevents_all_target_changes()
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        var lease = await new MigrationGate(data.Database, data.Clock, 18).AcquireAsync(18, default); Assert.NotNull(lease);
        try
        {
            var before = await History(data); var writes = new List<BsonDocument>();
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => Observed(data, writes).ApplyAsync());
            Assert.Equal("migration_in_progress", error.Code); Assert.Empty(writes); Assert.Equal(before, await History(data));
            Assert.False(await (await data.Database.ListCollectionNamesAsync(new ListCollectionNamesOptions { Filter = new BsonDocument("name", Collection) })).AnyAsync());
        }
        finally { await lease.ReleaseAsync(); }
    }
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Legacy_active_flag_is_preserved_without_fabricating_stop_or_acceptance()
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        var actors = await Seed(data, false);
        await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument("_id", actors.Pool), Builders<BsonDocument>.Update.Set("active", true));
        var before = await Business(data); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await Business(data)); Assert.Equal(0, await Rows(data, Collection).CountDocumentsAsync(new BsonDocument()));
    }
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Settings_and_mapping_changes_with_equal_timestamp_stops_preserve_actual_history()
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data); var service = Service(data);
        await Change(data, actors, 1); await Stop(data, actors, "second-stop");
        var revision = await service.ProposeRevisionAsync(actors.Maker, actors.Pool, 1, actors.Input with { Label = "Reviewed replacement" }, Meta("settings"));
        await service.DecideRevisionAsync(actors.Reviewer, revision["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("settings-applied"));
        await Stop(data, actors, "third-stop");
        var before = await Business(data); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await Business(data)); Assert.Equal(3, await Rows(data, Collection).CountDocumentsAsync(new BsonDocument()));
    }
    private async Task Change(StoreTestData data, Actors actors, long baseRevision)
    {
        var service = Service(data);
        var proposal = await service.ProposeAliasAsync(actors.Maker, actors.Pool, 1, Linked, baseRevision, baseRevision % 2 == 0, Meta("change-" + baseRevision));
        await service.DecideAliasAsync(actors.Reviewer, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("changed-" + baseRevision));
    }
    [Theory(Skip = Retired.HistoricalUpgrade)]
    [InlineData("tail")] [InlineData("middle")] [InlineData("lost-channel")]
    public async Task Missing_history_and_channels_cannot_be_concealed_by_a_valid_latest_stop(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data);
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1); await Change(data, actors, 1); await Stop(data, actors, "second");
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1); await Change(data, actors, 2); await Stop(data, actors, "third");
        if (fault == "lost-channel")
        {
            var row = await Rows(data, Collection).Find(new BsonDocument("suspensionRevision", 3L)).SingleAsync();
            row["configuration"]["mappings"] = new BsonArray(); await ReplaceCoherently(data, row);
        }
        else await Rows(data, Collection).DeleteOneAsync(new BsonDocument("suspensionRevision", fault == "tail" ? 3L : 2L));
        await Reject(data);
    }
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Suspension_preflight_checks_records_beyond_first_cursor_batch()
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data); var service = Service(data);
        for (var i = 1; i <= 64; i++)
        {
            var proposal = await service.ProposeAsync(actors.Maker, actors.Input with { PhysicalKey = "synthetic-" + i }, Meta("source-" + i));
            var approved = await service.DecideAsync(actors.Reviewer, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("source-approved-" + i));
            await Stop(data, actors with { Pool = approved["poolId"].AsInt64 }, "stop-" + i);
        }
        Assert.Equal(65, await Rows(data, Collection).CountDocumentsAsync(new BsonDocument()));
        var last = await Rows(data, Collection).Find(new BsonDocument()).Sort(new BsonDocument("poolId", -1)).FirstAsync();
        last["configurationHash"] = new string('a', 64); await ReplaceCoherently(data, last, false);
        await Reject(data);
    }
}