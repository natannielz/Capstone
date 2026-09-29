using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceAliasTests
{
    [Fact]
    public async Task Alias_detail_is_scoped_to_selected_pool_and_channel_and_retains_older_settings_history()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var proposed = await Propose(service, maker, pool); var id = proposed["proposalId"].AsInt64; await Approve(service, reviewer, id);
        var settings = await service.ProposeRevisionAsync(maker, pool, 1, Input() with { Label = "Later settings" }, Meta("settings"));
        await service.DecideRevisionAsync(reviewer, settings["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic", Meta("settings-approve"));
        var otherSource = await service.ProposeAsync(maker, Input() with { PhysicalKey = "another-pool" }, Meta("other-source"));
        var other = await service.DecideAsync(reviewer, otherSource["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic", Meta("other-source-approve"));
        var foreignLocation = Requested with { ChannelId = new(105) };
        await Rows(data, "authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(105, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)));
        await data.AddGrantAsync(maker, 1002, foreignLocation.ToScope(), [Permissions.StockSourceRead]);
        var detail = await service.ReadAliasProposalAsync(maker, pool, Requested, id);
        Assert.NotNull(detail); Assert.Equal(1L, detail["proposal"]["poolRevision"].AsInt64); Assert.Equal("approve", detail["decision"]["decision"].AsString);
        Assert.Null(await service.ReadAliasProposalAsync(maker, pool, Requested, id + 9999));
        Assert.Null(await service.ReadAliasProposalAsync(maker, other["poolId"].AsInt64, Requested, id));
        Assert.Null(await service.ReadAliasProposalAsync(maker, pool, foreignLocation, id));
        await Rows(data, "authorizationGrants").UpdateManyAsync(new BsonDocument("userId", maker.UserId),
            Builders<BsonDocument>.Update.Pull("permissions", Permissions.StockSourceRead));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAliasProposalAsync(maker, pool, Requested, id));
        Assert.Equal(1L, await Count(data, "stockSourceAliasProposals")); Assert.Equal(1L, await Count(data, "stockSourceAliases"));
    }

    [Theory]
    [InlineData(1000L)] [InlineData(1001L)]
    public async Task Alias_detail_cannot_return_after_either_scope_is_revoked_during_the_read(long revokedGrant)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, _, pool) = await Setup(data); var service = Service(data);
        var proposed = await Propose(service, maker, pool); var audits = await Count(data, "auditEvents"); var observed = 0; var changed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "AliasDetailRace-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "find" && e.Command.GetValue("find", "") == "stockSourceAliasProposals") observed = e.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.RequestId == observed && Interlocked.CompareExchange(ref changed, 1, 0) == 0)
                    Rows(data, "authorizationGrants").UpdateOne(new BsonDocument("_id", revokedGrant),
                        Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L));
            });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var guarded = new StockSourceSetupService(database, new(client), new(database), data.Clock);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => guarded.ReadAliasProposalAsync(maker, pool, Requested, proposed["proposalId"].AsInt64));
        Assert.Equal(1, changed); Assert.Equal(audits, await Count(data, "auditEvents"));
        Assert.Equal(1L, await Count(data, "stockSourceAliasProposals")); Assert.Equal(0L, await Count(data, "stockSourceAliasDecisions"));
    }
}
