using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourcePolicyServiceTests
{
    [Theory]
    [InlineData("owner")] [InlineData("linked")] [InlineData("mfa")] [InlineData("same-account")]
    [InlineData("same-person")] [InlineData("missing-person")] [InlineData("inactive-scope")] [InlineData("read-only")]
    public async Task Policy_preview_requires_current_action_authority_and_independent_people(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data, true);
        var proposal = await Propose(data, pool, maker); var id = proposal["proposalId"].AsInt64;
        if (fault == "mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(5).AddMilliseconds(1);
        if (fault is "owner" or "linked")
            await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", fault == "owner" ? 2200L : 2201L),
                Builders<BsonDocument>.Update.Set("status", "revoked"));
        if (fault == "read-only") await OnlyPolicyPermission(data, reviewer, Permissions.StockSourceRead);
        if (fault == "same-account")
        {
            await Rows(data, "authorizationGrants").UpdateManyAsync(new BsonDocument("userId", maker.UserId),
                Builders<BsonDocument>.Update.AddToSet("permissions", Permissions.StockSourcePolicyApprove));
            reviewer = maker;
        }
        if (fault == "same-person")
        {
            await Rows(data, "personRecords").UpdateOneAsync(new BsonDocument("_id", PersonTestData.PersonId(reviewer.UserId)),
                Builders<BsonDocument>.Update.Set("currentEquivalenceId", PersonTestData.GroupId(maker.UserId)));
            await Rows(data, "personEquivalenceGroups").UpdateOneAsync(new BsonDocument("_id", PersonTestData.GroupId(maker.UserId)),
                new BsonDocument("$set", new BsonDocument {
                    { "memberPersonIds", new BsonArray { PersonTestData.PersonId(maker.UserId), PersonTestData.PersonId(reviewer.UserId) } },
                    { "memberUserIds", new BsonArray { maker.UserId, reviewer.UserId } }
                }));
        }
        if (fault == "missing-person") await Rows(data, "personAccountBindings").DeleteOneAsync(new BsonDocument("_id", reviewer.UserId));
        if (fault == "inactive-scope") await Rows(data, "authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 104L),
            Builders<BsonDocument>.Update.Set("status", "suspended"));
        var before = await Effects(data);
        foreach (var action in new[] { StockSourceSetupDecision.Approve, StockSourceSetupDecision.Reject })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service(data).PreviewOperatingPolicyDecisionAsync(reviewer, pool, id, action));
        Assert.Equal(before, await Effects(data));
    }

    [Theory]
    [InlineData("settings")] [InlineData("mapping")] [InlineData("stop")] [InlineData("policy")] [InlineData("active")]
    public async Task Policy_preview_preserves_rejection_and_withdrawal_for_stale_proposals(string change)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data, change == "mapping");
        var service = Service(data); var proposal = await Propose(data, pool, maker); var id = proposal["proposalId"].AsInt64;
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        if (change == "settings")
        {
            var revised = await service.ProposeRevisionAsync(maker, pool, 1, Source() with { SafetyBufferBaseUnits = 5 }, Meta("revised"));
            await service.DecideRevisionAsync(reviewer, revised["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic revision", Meta("revised-approved"));
        }
        if (change == "mapping")
        {
            var mapped = await service.ProposeAliasAsync(maker, pool, 1, Linked, 1, false, Meta("disabled"));
            await service.DecideAliasAsync(reviewer, mapped["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic disable", Meta("disabled-approved"));
        }
        if (change == "stop")
        {
            var preview = await service.PreviewSuspensionAsync(maker, pool);
            await service.SuspendAsync(maker, pool, new(preview["poolRevision"].AsInt64, preview["suspensionRevision"].AsInt64, preview["configurationHash"].AsString), Meta("stop"));
        }
        if (change == "policy")
        {
            var other = await Propose(data, pool, maker, "replacement");
            await service.DecideOperatingPolicyAsync(reviewer, other["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic replacement", Meta("replacement-approved"));
        }
        if (change == "active") await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument("_id", pool), Builders<BsonDocument>.Update.Set("active", true));
        var before = await Effects(data);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.PreviewOperatingPolicyDecisionAsync(reviewer, pool, id, StockSourceSetupDecision.Approve));
        Assert.False((await service.PreviewOperatingPolicyDecisionAsync(reviewer, pool, id, StockSourceSetupDecision.Reject))["bindingCurrent"].AsBoolean);
        Assert.False((await service.PreviewOperatingPolicyDecisionAsync(maker, pool, id, StockSourceSetupDecision.Withdraw))["bindingCurrent"].AsBoolean);
        if (change == "mapping")
        {
            await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 2201L), Builders<BsonDocument>.Update.Set("status", "revoked"));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.PreviewOperatingPolicyDecisionAsync(reviewer, pool, id, StockSourceSetupDecision.Reject));
        }
        Assert.Equal(before, await Effects(data));
    }

    [Theory]
    [InlineData(StockSourceSetupDecision.Approve)] [InlineData(StockSourceSetupDecision.Reject)] [InlineData(StockSourceSetupDecision.Withdraw)]
    public async Task Policy_preview_rejects_already_decided_proposals(StockSourceSetupDecision decided)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var id = (await Propose(data, pool, maker))["proposalId"].AsInt64;
        await service.DecideOperatingPolicyAsync(decided == StockSourceSetupDecision.Withdraw ? maker : reviewer, id, 1, decided, "Synthetic decision", Meta("decided"));
        var before = await Effects(data);
        foreach (var action in new[] { StockSourceSetupDecision.Approve, StockSourceSetupDecision.Reject, StockSourceSetupDecision.Withdraw })
        {
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() =>
                service.PreviewOperatingPolicyDecisionAsync(action == StockSourceSetupDecision.Withdraw ? maker : reviewer, pool, id, action));
            Assert.Equal("stock_source_policy_already_decided", error.Code);
        }
        Assert.Equal(before, await Effects(data));
    }

    [Fact]
    public async Task Policy_preview_never_rebinds_a_proposal_to_another_selected_pool()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var id = (await Propose(data, pool, maker))["proposalId"].AsInt64;
        var second = await service.ProposeAsync(maker, Source() with { PhysicalKey = "another-pool" }, Meta("another"));
        var accepted = await service.DecideAsync(reviewer, second["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic pool", Meta("another-approved"));
        var before = await Effects(data);
        foreach (var (selectedPool, proposal) in new[] { (accepted["poolId"].AsInt64, id), (pool, long.MaxValue), (long.MaxValue, id) })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.PreviewOperatingPolicyDecisionAsync(reviewer, selectedPool, proposal, StockSourceSetupDecision.Approve));
        Assert.Equal(before, await Effects(data));
    }

    [Theory]
    [InlineData("grant")] [InlineData("person")] [InlineData("person-time")] [InlineData("mfa-time")] [InlineData("page")]
    public async Task Policy_preview_and_page_recheck_final_authority(string change)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data, true);
        var id = (await Propose(data, pool, maker))["proposalId"].AsInt64;
        if (change == "person-time")
            await Rows(data, "personAccountBindings").UpdateOneAsync(new BsonDocument("_id", reviewer.UserId),
                Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.AddSeconds(1).UtcDateTime));
        var before = await Effects(data); var captured = 0; var changed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "PolicyPreviewFinal-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                var selected = change switch {
                    "person" => e.CommandName == "find" && e.Command.GetValue("find", "") == "personEquivalenceGroups",
                    "person-time" or "mfa-time" => e.CommandName == "update" && e.Command.GetValue("update", "") == "authorizationGrants",
                    _ => e.CommandName == "find" && e.Command.GetValue("find", "") == StockSourcePolicyBson.Proposals
                };
                if (selected) captured = e.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.RequestId != captured || Interlocked.CompareExchange(ref changed, 1, 0) != 0) return;
                if (change == "person") Rows(data, "personAccountBindings").UpdateOne(new BsonDocument("_id", reviewer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
                else if (change == "person-time") data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(2);
                else if (change == "mfa-time") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
                else Rows(data, "authorizationGrants").UpdateOne(new BsonDocument("_id", 2201L), Builders<BsonDocument>.Update.Set("status", "revoked"));
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var candidate = new StockSourceSetupService(db, new(client), new(db), data.Clock);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
        {
            if (change == "page") await candidate.ListOperatingPolicyProposalsAsync(reviewer, pool, 0, 25);
            else await candidate.PreviewOperatingPolicyDecisionAsync(reviewer, pool, id, StockSourceSetupDecision.Approve);
        });
        Assert.Equal(1, changed); Assert.Equal(before, await Effects(data));
    }
}
