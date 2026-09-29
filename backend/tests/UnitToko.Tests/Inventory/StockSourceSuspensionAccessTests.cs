using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Authorization;
namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSuspensionTests
{
    [Fact]
    public async Task Suspension_history_pages_one_pool_and_retains_original_configuration_after_settings_change()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, actor, pool) = await Setup(data, true); var service = Service(data);
        var first = await service.SuspendAsync(actor, pool, Expected(await service.PreviewSuspensionAsync(actor, pool)), Meta("first"));
        var proposal = await service.ProposeRevisionAsync(maker, pool, 1, Input() with { Label = "Later source settings" }, Meta("settings"));
        await service.DecideRevisionAsync(reviewer, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("settings-approved"));
        await service.SuspendAsync(actor, pool, Expected(await service.PreviewSuspensionAsync(actor, pool)), Meta("second"));
        var page = await service.ListSuspensionsAsync(actor, pool, 1, 1);
        Assert.Equal(2, page.Total); var original = Assert.Single(page.Items); Assert.Equal(first["suspensionId"], original["_id"]); Assert.Equal(1L, original["poolRevision"].AsInt64);
        Assert.Equal(original, await service.ReadSuspensionDetailAsync(actor, pool, original["_id"].AsInt64));
        var other = await service.ProposeAsync(maker, Input() with { PhysicalKey = "other-pool" }, Meta("other"));
        var approved = await service.DecideAsync(reviewer, other["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("other-approved"));
        Assert.Null(await service.ReadSuspensionDetailAsync(actor, approved["poolId"].AsInt64, original["_id"].AsInt64));
        Assert.Null(await service.ReadSuspensionDetailAsync(actor, pool, 9999));
        Assert.Empty((await service.ListSuspensionsAsync(actor, approved["poolId"].AsInt64, 0, 25)).Items);
    }
    [Theory]
    [InlineData("page", 3000L)] [InlineData("page", 3001L)] [InlineData("detail", 3000L)] [InlineData("detail", 3001L)]
    public async Task Suspension_history_never_returns_after_either_linked_grant_is_revoked_during_read(string kind, long grant)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (_, _, actor, pool) = await Setup(data, true); var service = Service(data);
        var result = await service.SuspendAsync(actor, pool, Expected(await service.PreviewSuspensionAsync(actor, pool)), Meta("stop"));
        var selected = 0; var changed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "SuspensionHistoryRace-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e => { if (e.CommandName == "find" && e.Command.GetValue("find", "") == "stockSourceSuspensions") selected = e.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.RequestId == selected && Interlocked.CompareExchange(ref changed, 1, 0) == 0)
                    Rows(data, "authorizationGrants").UpdateOne(new BsonDocument("_id", grant), Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L));
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var guarded = new StockSourceSetupService(db, new(client), new(db), data.Clock);
        if (kind == "page") await Assert.ThrowsAsync<AuthorizationDeniedException>(() => guarded.ListSuspensionsAsync(actor, pool, 0, 25));
        else await Assert.ThrowsAsync<AuthorizationDeniedException>(() => guarded.ReadSuspensionDetailAsync(actor, pool, result["suspensionId"].AsInt64));
        Assert.Equal(1, changed); Assert.Equal(1L, await Count(data, "stockSourceSuspensions")); Assert.Equal(5L, await Count(data, "idempotencyRecords"));
    }
}