using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourcePolicyServiceTests
{
    private static async Task OnlyPolicyPermission(StoreTestData data, ActorContext actor, string permission) =>
        await Rows(data, "authorizationGrants").UpdateManyAsync(new BsonDocument("userId", actor.UserId),
            Builders<BsonDocument>.Update.Set("permissions", new BsonArray { permission }));

    [Theory]
    [InlineData(StockSourceSetupDecision.Approve)] [InlineData(StockSourceSetupDecision.Reject)]
    public async Task Policy_review_preview_uses_only_action_permission_and_keeps_history_private(StockSourceSetupDecision action)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data, true);
        var proposal = await Propose(data, pool, maker); var id = proposal["proposalId"].AsInt64;
        await OnlyPolicyPermission(data, reviewer, Permissions.StockSourcePolicyApprove);
        var service = Service(data); var before = await Effects(data);
        var counters = new BsonArray(await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        Assert.NotEmpty(counters);
        var preview = await service.PreviewOperatingPolicyDecisionAsync(reviewer, pool, id, action);
        Assert.Equal(id, preview["proposal"]["_id"].AsInt64);
        Assert.Equal(action.ToString().ToLowerInvariant(), preview["action"].AsString);
        Assert.True(preview["bindingCurrent"].AsBoolean); Assert.True(preview["decision"].IsBsonNull);
        Assert.Equal(before, await Effects(data));
        Assert.Equal(counters, new BsonArray(await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadOperatingPolicyProposalAsync(reviewer, pool, id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListOperatingPolicyProposalsAsync(reviewer, pool, 0, 25));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.PreviewOperatingPolicyAsync(reviewer, pool));
    }

    [Fact]
    public async Task Policy_withdrawal_preview_requires_original_maker_without_person_proof()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data);
        var proposal = await Propose(data, pool, maker); var id = proposal["proposalId"].AsInt64;
        await OnlyPolicyPermission(data, maker, Permissions.StockSourcePolicyPropose);
        await OnlyPolicyPermission(data, reviewer, Permissions.StockSourcePolicyPropose);
        await Rows(data, "personAccountBindings").DeleteManyAsync(new BsonDocument());
        var service = Service(data); var before = await Effects(data);
        var result = await service.PreviewOperatingPolicyDecisionAsync(maker, pool, id, StockSourceSetupDecision.Withdraw);
        Assert.Equal("withdraw", result["action"].AsString); Assert.Equal(id, result["proposal"]["_id"].AsInt64);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            service.PreviewOperatingPolicyDecisionAsync(reviewer, pool, id, StockSourceSetupDecision.Withdraw));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListOperatingPolicyProposalsAsync(maker, pool, 0, 25));
        Assert.Equal(before, await Effects(data));
    }

    [Fact]
    public async Task Policy_history_page_includes_all_states_in_exact_reverse_id_order()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data);
        var service = Service(data); var ids = new List<long>();
        foreach (var action in new[] { StockSourceSetupDecision.Approve, StockSourceSetupDecision.Reject, StockSourceSetupDecision.Withdraw })
        {
            var proposal = await Propose(data, pool, maker, action.ToString()); ids.Add(proposal["proposalId"].AsInt64);
            await service.DecideOperatingPolicyAsync(action == StockSourceSetupDecision.Withdraw ? maker : reviewer, ids[^1], 1, action,
                "Synthetic review", Meta(action + "-decided"));
        }
        ids.Add((await Propose(data, pool, maker, "pending"))["proposalId"].AsInt64);
        var before = await Effects(data);
        var page = await service.ListOperatingPolicyProposalsAsync(maker, pool, 1, 2);
        Assert.Equal(4, page.Total); Assert.Equal(ids.AsEnumerable().Reverse().Skip(1).Take(2), page.Items.Select(view => view["proposal"]["_id"].AsInt64));
        foreach (var item in page.Items)
            Assert.Equal(item, await service.ReadOperatingPolicyProposalAsync(maker, pool, item["proposal"]["_id"].AsInt64));
        Assert.Empty((await service.ListOperatingPolicyProposalsAsync(maker, pool, 4, 2)).Items);
        foreach (var (offset, limit) in new[] { (-1, 25), (0, 0), (0, 101) })
            await Assert.ThrowsAsync<DomainValidationException>(() => service.ListOperatingPolicyProposalsAsync(maker, pool, offset, limit));
        Assert.Equal(before, await Effects(data));
    }
}
