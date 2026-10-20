using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceAliasMigrationTests
{
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Stale_pending_and_terminal_proposals_and_reapproved_settings_remain_history()
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data); var service = Service(data);
        foreach (var outcome in new[] { "reject", "withdraw", "pending" })
        {
            var proposed = await service.ProposeAliasAsync(actors.Maker, actors.Pool, 1, Requested, 1, false, Meta("propose-" + outcome));
            if (outcome == "pending") continue;
            await service.DecideAliasAsync(outcome == "withdraw" ? actors.Maker : actors.Reviewer, proposed["proposalId"].AsInt64, 1,
                outcome == "withdraw" ? StockSourceSetupDecision.Withdraw : StockSourceSetupDecision.Reject,
                "synthetic-review", Meta("decide-" + outcome));
        }
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        await AddAlias(data, actors, 1, false, 1);
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        var revision = await service.ProposeRevisionAsync(actors.Maker, actors.Pool, 1,
            actors.Input with { Label = "Updated source settings" }, Meta("source-revision"));
        await service.DecideRevisionAsync(actors.Reviewer, revision["proposalId"].AsInt64, 1,
            StockSourceSetupDecision.Approve, "synthetic-review", Meta("source-revision-approve"));
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        await AddAlias(data, actors, 2, true, 2);
        var before = await BusinessRows(data);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await BusinessRows(data));
        Assert.True((await service.ReadAliasAsync(actors.Maker, actors.Pool, Requested))["bindingCurrent"].AsBoolean);
    }

    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Historical_approvals_do_not_depend_on_current_people_bindings_or_live_access()
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        await Rows(data, "personAccountBindings").DeleteManyAsync(new BsonDocument());
        foreach (var name in new[] { "identities", "memberships", "authorizationGrants", "authorizationScopes" })
            await Rows(data, name).UpdateManyAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("status", "suspended"));
        var before = await BusinessRows(data);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await BusinessRows(data));
        Assert.True(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
    }
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Stronger_validator_equivalent_indexes_and_original_identity_counters_are_preserved()
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        var stronger = new BsonDocument("$and", new BsonArray
            { StockSourceAliasSchemas.Validator("stockSourceAliasProposals"), new BsonDocument("_id", new BsonDocument("$ne", 999999L)) });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "stockSourceAliasProposals" }, { "validator", stronger } });
        var uniqueBefore = await (await Rows(data, "stockSourceAliases").Indexes.ListAsync()).ToListAsync();
        Assert.Contains(uniqueBefore, index => index["name"] == "ux_alias_draft");
        await Rows(data, "idCounters").DeleteOneAsync(new BsonDocument("_id", "inventoryPools"));
        var before = await BusinessRows(data);
        var writes = new List<BsonDocument>();
        await ObservedMigration(data, writes).ApplyAsync();
        Assert.NotEmpty(writes); Assert.All(writes, command => Assert.Equal("majority", command["writeConcern"]["w"].AsString));
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "stockSourceAliasProposals") })).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
        var after = await (await Rows(data, "stockSourceAliases").Indexes.ListAsync()).ToListAsync();
        foreach (var index in uniqueBefore) Assert.Contains(after, candidate => candidate.Equals(index));
        Assert.Equal(before, await BusinessRows(data));
        Assert.Equal(2, (await new IdAllocator(data.Database).NextAsync("inventoryPools")).Value);
        var history = await History(data);
        writes.Clear(); await ObservedMigration(data, writes).ApplyAsync();
        Assert.Empty(writes); Assert.Equal(history, await History(data));
    }

    [Fact]
    public async Task Interrupted_real_ddl_resumes_with_original_history_and_majority_writes()
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        var before = await History(data);
        using var cancellation = new CancellationTokenSource();
        var selected = 0; var interrupted = 0; var writes = new List<BsonDocument>();
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "AliasMigrationCancel-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "create" && e.Command.GetValue("create", "") == "stockSourceAliasProposals")
                { selected = e.RequestId; writes.Add(e.Command.DeepClone().AsBsonDocument); }
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.CommandName == "create" && e.RequestId == selected && Interlocked.CompareExchange(ref interrupted, 1, 0) == 0)
                    cancellation.Cancel();
            });
        };
        var database = new MongoClient(settings).GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(database, data.Clock).ApplyAsync(cancellation.Token));
        Assert.Equal(1, interrupted); Assert.Single(writes); Assert.Equal("majority", writes[0]["writeConcern"]["w"].AsString);
        var partial = await History(data);
        Assert.Equal(before, new BsonArray(partial.Take(16))); Assert.Equal("applying", partial[16]["state"].AsString);
        Assert.False(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
        Assert.True(await (await data.Database.ListCollectionNamesAsync(new ListCollectionNamesOptions
            { Filter = new BsonDocument("name", "stockSourceAliasProposals") })).AnyAsync());
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(11);
        writes.Clear(); await ObservedMigration(data, writes).ApplyAsync();
        Assert.All(writes, command => Assert.Equal("majority", command["writeConcern"]["w"].AsString));
        var completed = await History(data);
        Assert.Equal(before, new BsonArray(completed.Take(16)));
        Assert.Equal("applied", completed[16]["state"].AsString);
        Assert.Equal(partial[16]["startedAt"], completed[16]["startedAt"]);
        foreach (var name in StockSourceAliasSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(data.Database, name, StockSourceAliasSchemas.Validator(name), StockSourceAliasSchemas.Key(name), default);
    }

    [Fact]
    public async Task Another_live_deployment_lease_prevents_alias_storage_changes()
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        var lease = await new MigrationGate(data.Database, data.Clock, 17).AcquireAsync(17, default);
        Assert.NotNull(lease);
        try
        {
            var before = await History(data); var writes = new List<BsonDocument>();
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => ObservedMigration(data, writes).ApplyAsync());
            Assert.Equal("migration_in_progress", error.Code); Assert.Empty(writes); Assert.Equal(before, await History(data));
            foreach (var name in StockSourceAliasSchemas.Collections)
                Assert.False(await (await data.Database.ListCollectionNamesAsync(new ListCollectionNamesOptions
                    { Filter = new BsonDocument("name", name) })).AnyAsync());
        }
        finally { await lease.ReleaseAsync(); }
    }

    [Theory(Skip = Retired.HistoricalUpgrade)]
    [InlineData("partial")] [InlineData("sparse")] [InlineData("hidden")]
    public async Task Weakened_alias_uniqueness_cannot_be_silently_replaced(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        await Rows(data, "stockSourceAliases").Indexes.DropOneAsync("ux_alias_draft");
        var options = new CreateIndexOptions<BsonDocument> { Name = "altered_alias_key", Unique = true };
        if (fault == "sparse") options.Sparse = true;
        if (fault == "hidden") options.Hidden = true;
        if (fault == "partial") options.PartialFilterExpression = new BsonDocument("poolId", new BsonDocument("$gt", 0L));
        await Rows(data, "stockSourceAliases").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(StockSourceAliasSchemas.Key("stockSourceAliases"), options));
        await RejectWithoutMutation(data);
    }
}
