using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed partial class StockSourcePolicyServiceTests(MongoFixture fixture)
{
    private static readonly StockLocation Owner = new(new(100), new(101), new(102), new(103));
    private static readonly StockLocation Linked = Owner with { ChannelId = new(104) };
    private static StockSourceSetupInput Source() => new(Owner, "policy-pool", "Synthetic source", StockAuthorityMode.WebLedger,
        StockPoolControl.SharedWebLedger, 60001, 9007199254740993, null, null, "Synthetic owner", "Synthetic handoff");
    private static StockSourceOperatingPolicyInput Input(string version = "1") =>
        new(StockMovementWriter.UnitToko, null, 0, "Synthetic reconciliation " + version, "Synthetic outage " + version);
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic policy review", "policy-tests");
    private StockSourceSetupService Service(StoreTestData data) => new(data.Database, new(fixture.Client), new(data.Database), data.Clock);
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string collection) => data.Database.GetCollection<BsonDocument>(collection);
    private static StockSourcePolicyExpectation Expected(BsonDocument preview) =>
        new(preview["poolRevision"].AsInt64, preview["suspensionRevision"].AsInt64, preview["policyRevision"].AsInt64, preview["configurationHash"].AsString);
    private async Task<(ActorContext Maker, ActorContext Reviewer, long Pool)> Setup(StoreTestData data, bool linked = false, StockSourceSetupInput? source = null)
    {
        // StoreTestData applies the registered migration chain; the service uses real installed policy storage.
        foreach (var collection in StockSourcePolicySchemas.Collections)
            await InventoryRepository.RequireStorageAsync(data.Database, collection, StockSourcePolicySchemas.Validator(collection), StockSourcePolicySchemas.Key(collection), default);
        var maker = await data.AddActorAsync(1100, Owner.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead,
            Permissions.StockSourcePolicyPropose, Permissions.StockSourceSuspend);
        var reviewer = await data.AddActorAsync(2200, Owner.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead, Permissions.StockSourcePolicyApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId);
        var service = Service(data);
        var proposed = await service.ProposeAsync(maker, source ?? Source(), Meta("source"));
        var accepted = await service.DecideAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic setup", Meta("source-approve"));
        var pool = accepted["poolId"].AsInt64;
        if (linked)
        {
            await Rows(data, "authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)));
            await data.AddGrantAsync(maker, 1101, Linked.ToScope(), [Permissions.StockSourcePropose, Permissions.StockSourceRead, Permissions.StockSourcePolicyPropose, Permissions.StockSourceSuspend]);
            await data.AddGrantAsync(reviewer, 2201, Linked.ToScope(), [Permissions.StockSourceApprove, Permissions.StockSourceRead, Permissions.StockSourcePolicyApprove]);
            var alias = await service.ProposeAliasAsync(maker, pool, 1, Linked, 0, true, Meta("alias"));
            await service.DecideAliasAsync(reviewer, alias["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic alias", Meta("alias-approve"));
        }
        return (maker, reviewer, pool);
    }
    private static async Task<BsonDocument> Physical(StoreTestData data)
    {
        var snapshot = new BsonDocument();
        foreach (var name in new[] { "stockSourceProposals", "stockSourceDecisions", "inventoryPoolRegistrations", "stockSourceRevisions",
            "stockSourceAliases", "lots", "stockBalances", "movements", "reservations", "journalEntries" })
            snapshot[name] = new BsonArray(await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return snapshot;
    }
    [Fact]
    public async Task Policy_has_one_shared_pool_version_and_independent_review_without_activation_or_quantity_changes()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data, true); var service = Service(data);
        var before = await Physical(data); var preview = await service.PreviewOperatingPolicyAsync(maker, pool);
        Assert.Equal(0L, preview["policyRevision"].AsInt64);
        var expected = Expected(preview);
        var proposed = await service.ProposeOperatingPolicyAsync(maker, pool, expected, Input(), Meta("policy"));
        Assert.Equal(proposed, await service.ProposeOperatingPolicyAsync(maker, pool, expected, Input(), Meta("policy")));
        var accepted = await service.DecideOperatingPolicyAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic policy acceptance", Meta("policy-approve"));
        Assert.Equal(accepted, await service.DecideOperatingPolicyAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic policy acceptance", Meta("policy-approve")));
        Assert.Equal(1L, accepted["policyRevision"].AsInt64);
        var current = await service.ReadOperatingPolicyAsync(maker, pool);
        Assert.True(current["bindingCurrent"].AsBoolean);
        Assert.Equal(accepted["policyId"], current["policy"]["_id"]);
        var proposal = current["proposal"].AsBsonDocument;
        Assert.Equal(9007199254740993L, proposal["configuration"]["source"]["input"]["safetyBufferBaseUnits"].AsInt64);
        Assert.Single(proposal["configuration"]["mappings"].AsBsonArray);
        var physical = await Rows(data, "inventoryPools").Find(new BsonDocument("_id", pool)).SingleAsync();
        Assert.False(physical["active"].AsBoolean); Assert.Equal(1L, physical["revision"].AsInt64);
        Assert.Equal(before, await Physical(data));
        Assert.Equal(1, await Rows(data, StockSourcePolicyBson.Policies).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(proposed, await service.RecoverOperatingPolicyAsync(maker, pool, "stock.source.policy.propose", "policy", "recover"));
        Assert.Equal(accepted, await service.RecoverOperatingPolicyAsync(reviewer, pool, "stock.source.policy.approve", "policy-approve", "recover"));
        Assert.Null(await service.RecoverOperatingPolicyAsync(maker, pool, "stock.source.policy.propose", "absent", "recover"));
    }
    [Theory]
    [InlineData(StockSourceSetupDecision.Reject)] [InlineData(StockSourceSetupDecision.Withdraw)]
    public async Task Rejection_and_original_maker_withdrawal_are_terminal_without_accepted_policy(StockSourceSetupDecision decision)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var proposed = await service.ProposeOperatingPolicyAsync(maker, pool, Expected(await service.PreviewOperatingPolicyAsync(maker, pool)), Input(), Meta("policy"));
        var actor = decision == StockSourceSetupDecision.Withdraw ? maker : reviewer;
        var result = await service.DecideOperatingPolicyAsync(actor, proposed["proposalId"].AsInt64, 1, decision, "Synthetic terminal review", Meta("decision"));
        Assert.True(result["policyId"].IsBsonNull); Assert.True(result["policyRevision"].IsBsonNull);
        Assert.Equal(0, await Rows(data, StockSourcePolicyBson.Policies).CountDocumentsAsync(new BsonDocument()));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.DecideOperatingPolicyAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Later approval", Meta("later")));
        Assert.Equal(result, await service.RecoverOperatingPolicyAsync(actor, pool, "stock.source.policy." + decision.ToString().ToLowerInvariant(), "decision", "recover"));
    }
}
