using MongoDB.Driver.Core.Events;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class StockSourceSetupMigrationTests(MongoFixture fixture)
{
    private static readonly StockLocation Location = new(new(100), new(101), new(102), new(103));
    private static readonly StockSourceSetupInput Input = new(Location, "synthetic-pool", "Synthetic pool",
        StockAuthorityMode.WebLedger, StockPoolControl.ExclusiveWebAllocation, 60000, 0, null, null, "owner", "handoff");
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private static async Task<BsonArray> History(StoreTestData data) =>
        new(await Rows(data, "schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());

    [Fact]
    public async Task Registered_source_migration_follows_actual_v14_and_installs_usable_storage()
    {
        await using var data = await StoreTestData.CreateForStockSourceSetupMigrationAsync(fixture);
        var before = await History(data);
        Assert.Equal(14, before.Count);
        var migration = new CollectionMigrations(data.Database, data.Clock);
        Assert.False(await migration.IsCurrentAsync());
        await migration.ApplyAsync();
        Assert.True(await migration.IsCurrentAsync());
        var after = await History(data);
        Assert.Equal(CollectionMigrations.CurrentVersion, after.Count);
        Assert.Equal(before, new BsonArray(after.Take(14)));
        foreach (var name in StockSourceSetupSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(data.Database, name, StockSourceSetupSchemas.Validator(name), StockSourceSetupSchemas.Key(name), default);
        await migration.ApplyAsync();
        Assert.Equal(after, await History(data));
        await ProposeAndApprove(data);
    }

    [Theory]
    [InlineData("malformed-record")]
    [InlineData("orphan-pool")]
    [InlineData("unsafe-unique")]
    [InlineData("bad-counter")]
    public async Task Historical_preflight_rejects_unsafe_data_before_target_ddl_or_allocator_writes(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourceSetupMigrationAsync(fixture);
        await data.Database.CreateCollectionAsync("inventoryPools");
        if (fault == "malformed-record") await Rows(data, "inventoryPools").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } });
        if (fault == "orphan-pool") await Rows(data, "inventoryPools").InsertOneAsync(InventoryGraphTests.Pool());
        if (fault == "unsafe-unique") await Rows(data, "inventoryPools").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument("location.channelId", 1), new CreateIndexOptions { Unique = true, Name = "unsafe" }));
        if (fault == "bad-counter") await Rows(data, "idCounters").InsertOneAsync(new BsonDocument { { "_id", "inventoryPools" }, { "value", long.MaxValue } });
        var before = await Definition(data, "inventoryPools");
        var counters = new BsonArray(await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync());
        Assert.Equal(before, await Definition(data, "inventoryPools"));
        Assert.Equal(counters, new BsonArray(await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.False(await (await data.Database.ListCollectionNamesAsync(new ListCollectionNamesOptions
            { Filter = new BsonDocument("name", "stockSourceProposals") })).AnyAsync());
        Assert.Equal("applying", (await History(data))[14]["state"].AsString);
    }

    [Fact]
    public async Task Valid_existing_draft_history_is_preserved_and_missing_counters_are_advanced()
    {
        await using var data = await StoreTestData.CreateForStockSourceSetupMigrationAsync(fixture);
        await StockSourceSetupTests.InstallAsync(data);
        await ProposeAndApprove(data);
        var before = await BusinessRows(data);
        await Rows(data, "idCounters").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$in",
            new BsonArray { "stockSourceProposals", "stockSourceDecisions", "inventoryPools", "auditEvents" })));
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await BusinessRows(data));
        foreach (var name in new[] { "stockSourceProposals", "stockSourceDecisions", "inventoryPools", "auditEvents" })
        {
            var maximum = (await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstAsync())["_id"].AsInt64;
            Assert.Equal(maximum + 1, (await new IdAllocator(data.Database).NextAsync(name)).Value);
        }
    }

    [Theory]
    [InlineData("proposal-audit")] [InlineData("decision-audit")] [InlineData("receipt")]
    [InlineData("registration")] [InlineData("pool")] [InlineData("receipt-hash")]
    [InlineData("receipt-result")] [InlineData("active-pool")]
    public async Task Broken_draft_history_cannot_be_grandfathered_into_deployment(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourceSetupMigrationAsync(fixture);
        await StockSourceSetupTests.InstallAsync(data); await ProposeAndApprove(data);
        switch (fault)
        {
            case "proposal-audit": await Rows(data, "auditEvents").DeleteOneAsync(new BsonDocument("operation", "stock.source.propose")); break;
            case "decision-audit": await Rows(data, "auditEvents").DeleteOneAsync(new BsonDocument("operation", "stock.source.approve")); break;
            case "receipt": await Rows(data, "idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "stock.source.approve")); break;
            case "registration": await Rows(data, "inventoryPoolRegistrations").DeleteManyAsync(new BsonDocument()); break;
            case "pool": await Rows(data, "inventoryPools").DeleteManyAsync(new BsonDocument()); break;
            case "receipt-hash": await Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument("operation", "stock.source.propose"),
                new BsonDocument("$set", new BsonDocument("bodyHash", new string('0', 64)))); break;
            case "receipt-result": await Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument("operation", "stock.source.approve"),
                new BsonDocument("$set", new BsonDocument("result.poolId", 999L))); break;
            case "active-pool": await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("active", true))); break;
        }
        var before = await Definition(data, "stockSourceProposals");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync());
        Assert.Equal(before, await Definition(data, "stockSourceProposals"));
    }

    [Fact]
    public async Task Actual_partial_ddl_resumes_under_the_real_lease_without_forging_history()
    {
        await using var data = await StoreTestData.CreateForStockSourceSetupMigrationAsync(fixture);
        var old = await History(data);
        using var cancellation = new CancellationTokenSource();
        var selected = 0; var interrupted = 0;
        var settings = fixture.Client.Settings.Clone();
        settings.ApplicationName = "StockSourceMigration-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.CommandName == "create" && command.Command.GetValue("create", "") == "stockSourceProposals")
                    selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (command.CommandName == "create" && command.RequestId == selected &&
                    Interlocked.CompareExchange(ref interrupted, 1, 0) == 0) cancellation.Cancel();
            });
        };
        var interruptedDatabase = new MongoClient(settings).GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(interruptedDatabase, data.Clock).ApplyAsync(cancellation.Token));
        Assert.Equal(1, interrupted);
        var partial = await History(data);
        Assert.Equal(old, new BsonArray(partial.Take(14)));
        Assert.Equal("applying", partial[14]["state"].AsString);
        Assert.False(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
        data.Clock.UtcNow += TimeSpan.FromMinutes(11);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        var complete = await History(data);
        Assert.Equal("applied", complete[14]["state"].AsString);
        Assert.Equal(partial[14]["startedAt"], complete[14]["startedAt"]);
        foreach (var name in StockSourceSetupSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(data.Database, name, StockSourceSetupSchemas.Validator(name), StockSourceSetupSchemas.Key(name), default);
    }

    [Fact]
    public async Task Competing_deployment_lease_stops_before_any_new_storage()
    {
        await using var data = await StoreTestData.CreateForStockSourceSetupMigrationAsync(fixture);
        var lease = await new MigrationGate(data.Database, data.Clock, 15).AcquireAsync(15, default);
        Assert.NotNull(lease);
        try
        {
            var before = await History(data);
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync());
            Assert.Equal("migration_in_progress", error.Code); Assert.Equal(before, await History(data));
            Assert.False(await (await data.Database.ListCollectionNamesAsync(new ListCollectionNamesOptions
                { Filter = new BsonDocument("name", "stockSourceProposals") })).AnyAsync());
        }
        finally { await lease.ReleaseAsync(); }
    }

    [Fact]
    public async Task Historical_review_survives_later_person_source_loss_and_actor_scope_suspension()
    {
        await using var data = await StoreTestData.CreateForStockSourceSetupMigrationAsync(fixture);
        await StockSourceSetupTests.InstallAsync(data); await ProposeAndApprove(data);
        await Rows(data, "personAccountBindings").DeleteManyAsync(new BsonDocument());
        await Rows(data, "identities").UpdateOneAsync(new BsonDocument("_id", 1000L),
            new BsonDocument("$set", new BsonDocument("status", "suspended")));
        await Rows(data, "authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 102L),
            new BsonDocument("$set", new BsonDocument("status", "suspended")));
        var before = await BusinessRows(data);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await BusinessRows(data));
        Assert.True(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
    }

    [Fact]
    public async Task Stricter_existing_validator_and_equivalent_index_names_are_preserved()
    {
        await using var data = await StoreTestData.CreateForStockSourceSetupMigrationAsync(fixture);
        await StockSourceSetupTests.InstallAsync(data);
        var stricter = new BsonDocument("$and", new BsonArray
            { StockSourceSetupSchemas.Validator("stockSourceProposals"), new BsonDocument("_id", new BsonDocument("$ne", 999999L)) });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "stockSourceProposals" }, { "validator", stricter } });
        var prior = await (await Rows(data, "inventoryPoolRegistrations").Indexes.ListAsync()).ToListAsync();
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(stricter, (await Definition(data, "stockSourceProposals"))["options"]["validator"]);
        var after = await (await Rows(data, "inventoryPoolRegistrations").Indexes.ListAsync()).ToListAsync();
        Assert.Equal(new BsonArray(prior), new BsonArray(after));
    }
    private async Task ProposeAndApprove(StoreTestData data)
    {
        var maker = await data.AddActorAsync(1000, Location.ToScope(), Permissions.StockSourcePropose);
        var reviewer = await data.AddActorAsync(2000, Location.ToScope(), Permissions.StockSourceApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId);
        var service = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        var proposed = await service.ProposeAsync(maker, Input, new("propose", "Synthetic migration test", "migration"));
        await service.DecideAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review",
            new("approve", "Synthetic migration test", "migration"));
    }
    private static async Task<BsonDocument> Definition(StoreTestData data, string name) =>
        await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<BsonDocument> BusinessRows(StoreTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in StockSourceSetupSchemas.Collections.Concat(["auditEvents", "idempotencyRecords"]))
            result.Add(name, new BsonArray(await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return result;
    }
}
