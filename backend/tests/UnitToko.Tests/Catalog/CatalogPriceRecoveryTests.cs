using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Platform.Authorization;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogPriceRecoveryTests(MongoFixture fixture)
{
    [Theory]
    [InlineData("propose")] [InlineData("approve")] [InlineData("reject")] [InlineData("withdraw")]
    public async Task Receipt_only_recovery_returns_original_action_without_submitting_work(string action)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var actor = action is "propose" or "withdraw" ? data.Maker : data.Checker;
        Assert.Null(await data.Service.RecoverAsync(actor, data.SkuId, action, "never-submitted"));
        Assert.Equal(0, await data.Rows("catalogPriceProposals").CountDocumentsAsync(new BsonDocument()));
        var proposal = await data.ProposeAsync();
        var key = action == "propose" ? "price-propose" : "decision-" + action;
        var expected = proposal;
        if (action != "propose") expected = await data.Service.DecideAsync(actor, proposal["proposalId"].AsInt64, 1,
            action switch { "approve" => CatalogPriceDecision.Approve, "reject" => CatalogPriceDecision.Reject, _ => CatalogPriceDecision.Withdraw },
            "Synthetic action-specific review", CatalogPriceTestData.Meta(key));
        var before = new BsonDocument();
        foreach (var name in new[] { "catalogPriceProposals", "catalogPriceDecisions", "priceVersions", "idempotencyRecords", "outboxEvents" })
            before[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        Assert.Equal(expected, await data.Service.RecoverAsync(actor, data.SkuId, action, key));
        foreach (var element in before)
            Assert.Equal(element.Value, new BsonArray(await data.Rows(element.Name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Null(await data.Service.RecoverAsync(actor, data.SkuId, action, "different-key"));
    }

    [Fact]
    public async Task Recovery_remains_actor_and_action_scoped_and_requires_current_independence()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync(); await data.ApproveAsync(proposal);
        Assert.Null(await data.Service.RecoverAsync(data.Checker, data.SkuId, "reject", "price-approve"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Maker, data.SkuId, "approve", "price-approve"));
        await data.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId),
            Builders<BsonDocument>.Update.Set("status", "suspended"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Checker, data.SkuId, "approve", "price-approve"));
    }
}
