using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourcePolicyServiceTests
{
    [Theory]
    [InlineData("valid")] [InlineData("writer")] [InlineData("tenant")] [InlineData("feed")]
    public async Task External_policy_preserves_exact_tenant_and_feed_without_asserting_completed_provider_handoff(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var source = Source() with { Mode = StockAuthorityMode.ExternalLedger, Control = StockPoolControl.ExternalHold,
            ExternalNamespace = "synthetic-provider", ReservationProtocolReference = "Synthetic provider protocol definition" };
        var (maker, reviewer, pool) = await Setup(data, source: source); var service = Service(data);
        var input = new StockSourceOperatingPolicyInput(StockMovementWriter.ExternalSystem, "Tenant-A/02", 120000,
            "Synthetic reconciliation definition", "Synthetic outage definition");
        var before = await Physical(data);
        var proposed = await service.ProposeOperatingPolicyAsync(maker, pool, Expected(await service.PreviewOperatingPolicyAsync(maker, pool)), input, Meta("external"));
        if (fault != "valid")
        {
            var row = await Rows(data, StockSourcePolicyBson.Proposals).Find(new BsonDocument("_id", proposed["proposalId"])).SingleAsync();
            row["input"][fault == "writer" ? "writer" : fault == "tenant" ? "externalTenant" : "feedIntervalMilliseconds"] =
                fault == "writer" ? new BsonString("unitToko") : fault == "tenant" ? BsonNull.Value : new BsonInt64(0);
            await Assert.ThrowsAsync<MongoWriteException>(() => Rows(data, StockSourcePolicyBson.Proposals).ReplaceOneAsync(new BsonDocument("_id", row["_id"]), row));
            return;
        }
        var accepted = await service.DecideOperatingPolicyAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic reviewed definition", Meta("approved"));
        var current = await service.ReadOperatingPolicyAsync(maker, pool);
        Assert.Equal("Tenant-A/02", current["proposal"]["input"]["externalTenant"].AsString);
        Assert.Equal(120000L, current["proposal"]["input"]["feedIntervalMilliseconds"].AsInt64);
        Assert.Equal(60001L, current["proposal"]["configuration"]["source"]["input"]["maximumAgeMilliseconds"].AsInt64);
        Assert.False((await Pool(data, pool))["active"].AsBoolean); Assert.Equal(before, await Physical(data));
        Assert.Equal(accepted, await service.RecoverOperatingPolicyAsync(reviewer, pool, "stock.source.policy.approve", "approved", "recover"));
    }

    [Fact]
    public async Task Historical_definition_survives_current_person_loss_but_approval_replay_requires_current_person_authority()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var proposed = await Propose(data, pool, maker);
        await service.DecideOperatingPolicyAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic review", Meta("approved"));
        var historical = await service.ReadOperatingPolicyAsync(maker, pool);
        await Rows(data, "personAccountBindings").UpdateOneAsync(new BsonDocument("_id", reviewer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        Assert.Equal(historical, await service.ReadOperatingPolicyAsync(maker, pool));
        var before = await Effects(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverOperatingPolicyAsync(reviewer, pool, "stock.source.policy.approve", "approved", "recover"));
        Assert.Equal(before, await Effects(data));
    }
}