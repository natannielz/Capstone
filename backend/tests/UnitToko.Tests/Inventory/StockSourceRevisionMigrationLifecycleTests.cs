using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceRevisionMigrationTests
{
    [Fact]
    public async Task Pending_stale_rejected_and_withdrawn_proposals_remain_historical_facts()
    {
        await using var data = await StoreTestData.CreateForStockSourceRevisionMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data);
        var service = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        var current = await service.ReadCurrentSourceAsync(actors.Maker, new(new(100), new(101), new(102), new(103)), actors.Pool);
        var input = StockSourceSetupBson.ReadInput(current["input"].AsBsonDocument);
        foreach (var outcome in new[] { "reject", "withdraw", "pending", "approve" })
        {
            var proposed = await service.ProposeRevisionAsync(actors.Maker, actors.Pool, 2, input with { Label = outcome },
                new("propose-" + outcome, "Synthetic candidate", "migration"));
            if (outcome == "pending") continue;
            await service.DecideRevisionAsync(outcome == "withdraw" ? actors.Maker : actors.Reviewer, proposed["proposalId"].AsInt64, 1,
                outcome == "withdraw" ? StockSourceSetupDecision.Withdraw : outcome == "reject" ? StockSourceSetupDecision.Reject : StockSourceSetupDecision.Approve,
                "synthetic-review", new("decide-" + outcome, "Synthetic decision", "migration"));
        }
        var before = await BusinessRows(data);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await BusinessRows(data));
        Assert.Equal(3, (await service.ReadCurrentSourceAsync(actors.Maker, new(new(100), new(101), new(102), new(103)), actors.Pool))["pool"]["revision"].AsInt64);
    }

    [Fact]
    public async Task Stronger_validator_equivalent_indexes_and_original_identity_counters_are_preserved()
    {
        await using var data = await StoreTestData.CreateForStockSourceRevisionMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        var stronger = new BsonDocument("$and", new BsonArray
            { StockSourceRevisionSchemas.Validator("stockSourceRevisionProposals"), new BsonDocument("_id", new BsonDocument("$ne", 999999L)) });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "stockSourceRevisionProposals" }, { "validator", stronger } });
        var uniqueBefore = await (await Rows(data, "stockSourceRevisions").Indexes.ListAsync()).ToListAsync();
        await Rows(data, "idCounters").DeleteOneAsync(new BsonDocument("_id", "inventoryPools"));
        var before = await BusinessRows(data);
        var writes = new List<BsonDocument>();
        await ObservedMigration(data, writes).ApplyAsync();
        Assert.NotEmpty(writes); Assert.All(writes, command => Assert.Equal("majority", command["writeConcern"]["w"].AsString));
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "stockSourceRevisionProposals") })).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
        var after = await (await Rows(data, "stockSourceRevisions").Indexes.ListAsync()).ToListAsync();
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
        await using var data = await StoreTestData.CreateForStockSourceRevisionMigrationAsync(fixture);
        var before = await History(data);
        using var cancellation = new CancellationTokenSource();
        var selected = 0; var interrupted = 0; var writes = new List<BsonDocument>();
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "RevisionMigrationCancel-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "create" && e.Command.GetValue("create", "") == "stockSourceRevisionProposals")
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
        Assert.Equal(before, new BsonArray(partial.Take(15))); Assert.Equal("applying", partial[15]["state"].AsString);
        Assert.False(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
        Assert.True(await (await data.Database.ListCollectionNamesAsync(new ListCollectionNamesOptions
            { Filter = new BsonDocument("name", "stockSourceRevisionProposals") })).AnyAsync());
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(11);
        writes.Clear(); await ObservedMigration(data, writes).ApplyAsync();
        Assert.All(writes, command => Assert.Equal("majority", command["writeConcern"]["w"].AsString));
        var completed = await History(data);
        Assert.Equal(before, new BsonArray(completed.Take(15)));
        Assert.Equal("applied", completed[15]["state"].AsString);
        Assert.Equal(partial[15]["startedAt"], completed[15]["startedAt"]);
        foreach (var name in StockSourceRevisionSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(data.Database, name, StockSourceRevisionSchemas.Validator(name), StockSourceRevisionSchemas.Key(name), default);
    }

    [Fact]
    public async Task Another_live_deployment_lease_prevents_revision_storage_changes()
    {
        await using var data = await StoreTestData.CreateForStockSourceRevisionMigrationAsync(fixture);
        var lease = await new MigrationGate(data.Database, data.Clock, 16).AcquireAsync(16, default);
        Assert.NotNull(lease);
        try
        {
            var before = await History(data); var writes = new List<BsonDocument>();
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => ObservedMigration(data, writes).ApplyAsync());
            Assert.Equal("migration_in_progress", error.Code); Assert.Empty(writes); Assert.Equal(before, await History(data));
            foreach (var name in StockSourceRevisionSchemas.Collections)
                Assert.False(await (await data.Database.ListCollectionNamesAsync(new ListCollectionNamesOptions
                    { Filter = new BsonDocument("name", name) })).AnyAsync());
        }
        finally { await lease.ReleaseAsync(); }
    }

    [Theory]
    [InlineData("partial")] [InlineData("sparse")] [InlineData("hidden")]
    public async Task Weakened_revision_uniqueness_cannot_be_silently_replaced(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourceRevisionMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        await Rows(data, "stockSourceRevisions").Indexes.DropOneAsync("ux_revision_draft");
        var options = new CreateIndexOptions<BsonDocument> { Name = "altered_revision_key", Unique = true };
        if (fault == "sparse") options.Sparse = true;
        if (fault == "hidden") options.Hidden = true;
        if (fault == "partial") options.PartialFilterExpression = new BsonDocument("poolId", new BsonDocument("$gt", 0L));
        await Rows(data, "stockSourceRevisions").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(StockSourceRevisionSchemas.Key("stockSourceRevisions"), options));
        await RejectWithoutMutation(data);
    }
}
