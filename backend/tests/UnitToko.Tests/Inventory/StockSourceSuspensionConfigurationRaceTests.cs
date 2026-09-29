using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSuspensionTests
{
    [Fact]
    public async Task Scope_suspension_before_first_authorization_fence_denies_without_business_effects()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (_, _, actor, pool) = await Setup(data, true); var service = Service(data);
        var expected = Expected(await service.PreviewSuspensionAsync(actor, pool)); var before = await Pool(data, pool);
        var captured = 0; var changed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "SuspendScope-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "find" && e.Command.GetValue("find", "") == "inventoryPools" && e.Command.Contains("projection")) captured = e.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.RequestId != captured || Interlocked.CompareExchange(ref changed, 1, 0) != 0) return;
                // Scope rows are fenced before business writes. Change the old snapshot before that lock, not from a callback holding it.
                Rows(data, "authorizationScopes").UpdateOne(new BsonDocument("_id", 104L), Builders<BsonDocument>.Update.Set("status", "suspended"));
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new StockSourceSetupService(db, new(client), new(db), data.Clock).SuspendAsync(actor, pool, expected, Meta("scope-changed")));
        Assert.Equal(1, changed); Assert.Equal(before, await Pool(data, pool));
        Assert.Equal(0L, await Count(data, StockSourceSuspensionSchemas.Collection)); Assert.Equal(4L, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData("mapping")] [InlineData("settings")]
    public async Task Configuration_approval_racing_a_reviewed_stop_requires_a_new_review(string changeKind)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, actor, pool) = await Setup(data, true); var service = Service(data);
        var proposal = changeKind == "mapping"
            ? await service.ProposeAliasAsync(maker, pool, 1, Linked, 1, false, Meta("pending-mapping"))
            : await service.ProposeRevisionAsync(maker, pool, 1, Input() with { SafetyBufferBaseUnits = 9 }, Meta("pending-settings"));
        var expected = Expected(await service.PreviewSuspensionAsync(actor, pool));
        var captured = 0; var held = 0; var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "SuspendConfigRace-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "find" && e.Command.GetValue("find", "") == "inventoryPools" && e.Command.Contains("projection")) captured = e.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.RequestId != captured || Interlocked.CompareExchange(ref held, 1, 0) != 0) return;
                loaded.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Configuration approval must complete before the old suspension snapshot resumes.");
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var pending = Task.Run(() => new StockSourceSetupService(db, new(client), new(db), data.Clock).SuspendAsync(actor, pool, expected, Meta("old-review")));
        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (changeKind == "mapping") await service.DecideAliasAsync(reviewer, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("approved-mapping"));
            else await service.DecideRevisionAsync(reviewer, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("approved-settings"));
        }
        finally { release.Set(); }
        var failure = await Assert.ThrowsAsync<PersistenceConflictException>(() => pending);
        Assert.Equal("stock_source_suspension_changed", failure.Code);
        Assert.Equal(1, held); Assert.Equal(0L, await Count(data, StockSourceSuspensionSchemas.Collection));
        Assert.Equal(6L, await Count(data, "auditEvents")); Assert.Equal(6L, await Count(data, "idempotencyRecords"));
    }
}
