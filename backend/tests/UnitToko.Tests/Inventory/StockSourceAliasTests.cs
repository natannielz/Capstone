using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
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
public sealed partial class StockSourceAliasTests(MongoFixture fixture)
{
    private static readonly StockLocation Owner = new(new(100), new(101), new(102), new(103));
    private static readonly StockLocation Requested = Owner with { ChannelId = new(104) };
    private static StockSourceSetupInput Input() => new(Owner, "synthetic-pool", "Synthetic shared pool",
        StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60001, 9007199254740993, null, null, "synthetic-owner", "synthetic-handoff");
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic channel mapping review", "alias-tests");
    private StockSourceSetupService Service(StoreTestData data) => new(data.Database, new(fixture.Client), new(data.Database), data.Clock);
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private static Task<long> Count(StoreTestData data, string name) => Rows(data, name).CountDocumentsAsync(new BsonDocument());
    private async Task<(ActorContext Maker, ActorContext Reviewer, long Pool)> Setup(StoreTestData data)
    {

        await Rows(data, "authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)));
        var maker = await data.AddActorAsync(1000, Owner.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
        var reviewer = await data.AddActorAsync(2000, Owner.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead);
        await data.AddGrantAsync(maker, 1001, Requested.ToScope(), [Permissions.StockSourcePropose, Permissions.StockSourceRead]);
        await data.AddGrantAsync(reviewer, 2001, Requested.ToScope(), [Permissions.StockSourceApprove, Permissions.StockSourceRead]);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId);
        var service = Service(data);
        var proposed = await service.ProposeAsync(maker, Input(), Meta("source-propose"));
        var approved = await service.DecideAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-initial-review", Meta("source-approve"));
        return (maker, reviewer, approved["poolId"].AsInt64);
    }
    private static Task<BsonDocument> Propose(StockSourceSetupService service, ActorContext maker, long pool, string key = "alias",
        long version = 0, bool enabled = true, long poolVersion = 1) =>
        service.ProposeAliasAsync(maker, pool, poolVersion, Requested, version, enabled, Meta(key));
    private static Task<BsonDocument> Approve(StockSourceSetupService service, ActorContext reviewer, long proposal, string key = "approve") =>
        service.DecideAliasAsync(reviewer, proposal, 1, StockSourceSetupDecision.Approve, "synthetic-alias-review", Meta(key));
    private static async Task<BsonDocument> Physical(StoreTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "stockSourceProposals", "stockSourceDecisions", "inventoryPoolRegistrations", "stockSourceRevisions",
            "lots", "stockBalances", "movements", "reservations", "journalEntries" })
            result.Add(name, new BsonArray(await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return result;
    }

    [Fact]
    public async Task Mapping_enable_disable_and_reenable_preserve_one_pool_history_and_all_physical_facts()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        await InventoryGraphTests.InstallBalances(data);
        await Rows(data, "lots").InsertOneAsync(InventoryGraphTests.Lot());
        await Rows(data, "stockBalances").InsertOneAsync(InventoryBson.Balance(new InventoryBalanceDocument(7000, pool,
            new(new(Owner, new(200), new(201), new(202)), 9007199254740993, 2, 3, 4, 1))));
        var before = await Physical(data);
        Assert.False((await service.ReadAliasAsync(maker, pool, Requested))["bindingCurrent"].AsBoolean);
        var proposed = await Propose(service, maker, pool);
        Assert.Equal(proposed, await Propose(service, maker, pool));
        var approved = await Approve(service, reviewer, proposed["proposalId"].AsInt64);
        Assert.Equal(approved, await Approve(service, reviewer, proposed["proposalId"].AsInt64));
        var mapped = await service.ReadAliasAsync(maker, pool, Requested);
        Assert.True(mapped["bindingCurrent"].AsBoolean);
        Assert.Equal(StockSourceSetupBson.Location(Owner), mapped["mapping"]["alias"]["ownerLocation"]);
        Assert.Equal(1L, mapped["currentSource"]["pool"]["revision"].AsInt64);
        Assert.False(mapped["currentSource"]["pool"]["active"].AsBoolean);
        var originalMapping = mapped["mapping"]["alias"].DeepClone();
        var disabled = await Propose(service, maker, pool, "disable", 1, false);
        await Approve(service, reviewer, disabled["proposalId"].AsInt64, "approve-disable");
        Assert.False((await service.ReadAliasAsync(maker, pool, Requested))["bindingCurrent"].AsBoolean);
        var enabled = await Propose(service, maker, pool, "reenable", 2);
        await Approve(service, reviewer, enabled["proposalId"].AsInt64, "approve-reenable");
        Assert.Equal(3L, (await service.ReadAliasAsync(maker, pool, Requested))["mapping"]["alias"]["aliasRevision"].AsInt64);
        Assert.Equal(originalMapping, await Rows(data, "stockSourceAliases").Find(new BsonDocument("aliasRevision", 1L)).SingleAsync());
        var history = await service.ListAliasProposalsAsync(reviewer, pool, Requested, 1, 1);
        Assert.Equal(3L, history.Total); Assert.Single(history.Items); Assert.False(history.Items[0]["proposal"]["enabled"].AsBoolean);
        Assert.Equal(before, await Physical(data));
        Assert.Equal(1L, await Count(data, "inventoryPools")); Assert.Equal(8L, await Count(data, "auditEvents")); Assert.Equal(8L, await Count(data, "idempotencyRecords"));
    }

    [Fact]
    public async Task Settings_change_invalidates_mapping_and_requires_independent_reapproval()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var p = await Propose(service, maker, pool); await Approve(service, reviewer, p["proposalId"].AsInt64);
        var revision = await service.ProposeRevisionAsync(maker, pool, 1, Input() with { Label = "Changed settings" }, Meta("settings"));
        await service.DecideRevisionAsync(reviewer, revision["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "new-settings-review", Meta("approve-settings"));
        Assert.False((await service.ReadAliasAsync(maker, pool, Requested))["bindingCurrent"].AsBoolean);
        var renewed = await Propose(service, maker, pool, "renew", 1, true, 2);
        await Approve(service, reviewer, renewed["proposalId"].AsInt64, "approve-renew");
        var current = await service.ReadAliasAsync(maker, pool, Requested);
        Assert.True(current["bindingCurrent"].AsBoolean); Assert.Equal(2L, current["mapping"]["alias"]["poolRevision"].AsInt64);
        Assert.Equal(1L, (await service.ListAliasProposalsAsync(maker, pool, Requested, 0, 25)).Items[0]["alias"]["poolRevision"].AsInt64);
        Assert.Equal(1L, await Count(data, "inventoryPools"));
    }

    [Theory]
    [InlineData(StockSourceSetupDecision.Reject)] [InlineData(StockSourceSetupDecision.Withdraw)]
    public async Task Nonapproval_is_terminal_and_cannot_create_a_mapping(StockSourceSetupDecision decision)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var p = await Propose(service, maker, pool); var actor = decision == StockSourceSetupDecision.Withdraw ? maker : reviewer;
        var result = await service.DecideAliasAsync(actor, p["proposalId"].AsInt64, 1, decision, "synthetic-decision", Meta("decision"));
        Assert.Equal(result, await service.DecideAliasAsync(actor, p["proposalId"].AsInt64, 1, decision, "synthetic-decision", Meta("decision")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => Approve(service, reviewer, p["proposalId"].AsInt64));
        Assert.Equal(0L, await Count(data, "stockSourceAliases")); Assert.True(result["aliasRevision"].IsBsonNull);
    }

    [Theory]
    [InlineData("requested-grant")] [InlineData("owner-grant")] [InlineData("requested-membership")] [InlineData("owner-membership")]
    [InlineData("requested-channel")] [InlineData("owner-channel")] [InlineData("stale-mfa")] [InlineData("missing-person")]
    [InlineData("same-person")] [InlineData("wrong-revision")]
    public async Task Review_requires_both_current_scopes_and_independent_current_person_authority(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var p = await Propose(service, maker, pool);
        if (fault.EndsWith("-grant")) await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", fault.StartsWith("requested") ? 2001L : 2000L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        if (fault.EndsWith("-membership")) await Rows(data, "memberships").UpdateOneAsync(new BsonDocument("_id", fault.StartsWith("requested") ? 2001L : 2000L), Builders<BsonDocument>.Update.Set("status", "suspended"));
        if (fault.EndsWith("-channel")) await Rows(data, "authorizationScopes").UpdateOneAsync(new BsonDocument("_id", fault.StartsWith("requested") ? 104L : 103L), Builders<BsonDocument>.Update.Set("status", "suspended"));
        if (fault == "stale-mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(5).AddMilliseconds(1);
        if (fault == "missing-person") await Rows(data, "personAccountBindings").DeleteOneAsync(new BsonDocument("_id", reviewer.UserId));
        if (fault == "same-person")
        {
            var group = PersonTestData.GroupId(1000);
            await Rows(data, "personEquivalenceGroups").UpdateOneAsync(new BsonDocument("_id", group), new BsonDocument("$set", new BsonDocument
            { { "memberPersonIds", new BsonArray { PersonTestData.PersonId(1000), PersonTestData.PersonId(2000) } }, { "memberUserIds", new BsonArray { 1000L, 2000L } } }));
            await Rows(data, "personRecords").UpdateOneAsync(new BsonDocument("_id", PersonTestData.PersonId(2000)), Builders<BsonDocument>.Update.Set("currentEquivalenceId", group));
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideAliasAsync(reviewer, p["proposalId"].AsInt64,
            fault == "wrong-revision" ? 2 : 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("approve")));
        Assert.Equal(0L, await Count(data, "stockSourceAliases")); Assert.Equal(0L, await Count(data, "stockSourceAliasDecisions"));
        Assert.Equal(3L, await Count(data, "auditEvents")); Assert.Equal(3L, await Count(data, "idempotencyRecords"));
    }

    [Fact]
    public async Task Owner_only_and_requested_only_actors_cannot_propose_read_or_count_private_mapping_history()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, _, pool) = await Setup(data); var service = Service(data);
        await Propose(service, maker, pool);
        foreach (var location in new[] { Owner, Requested })
        {
            var actor = await data.AddActorAsync(location == Owner ? 3000 : 4000, location.ToScope(), Permissions.StockSourceRead, Permissions.StockSourcePropose);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAliasAsync(actor, pool, Requested));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListAliasProposalsAsync(actor, pool, Requested, 0, 25));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Propose(service, actor, pool, "foreign"));
        }
        Assert.Equal(1L, await Count(data, "stockSourceAliasProposals")); Assert.Equal(3L, await Count(data, "idempotencyRecords"));
    }

    [Fact]
    public async Task Active_pool_stale_settings_duplicate_body_and_stale_alias_base_cannot_change_mapping()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var first = await Propose(service, maker, pool); var second = await Propose(service, maker, pool, "second");
        await Assert.ThrowsAsync<PersistenceConflictException>(() => Propose(service, maker, pool, "alias", 0, false));
        await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument("_id", pool), Builders<BsonDocument>.Update.Set("active", true));
        Assert.Equal("stock_source_handoff_required", (await Assert.ThrowsAsync<PersistenceConflictException>(() => Approve(service, reviewer, first["proposalId"].AsInt64))).Code);
        await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument("_id", pool), Builders<BsonDocument>.Update.Set("active", false));
        await Approve(service, reviewer, first["proposalId"].AsInt64);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => Approve(service, reviewer, second["proposalId"].AsInt64, "second-approve"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => Propose(service, maker, pool, "same-state", 1));
        Assert.Equal(1L, await Count(data, "stockSourceAliases")); Assert.Equal(1L, await Count(data, "stockSourceAliasDecisions"));
    }

    [Fact]
    public async Task Audit_failure_rolls_back_the_mapping_decision_pool_fence_and_receipt()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var p = await Propose(service, maker, pool);
        var before = await Rows(data, "inventoryPools").Find(new BsonDocument("_id", pool)).SingleAsync();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        { { "collMod", "auditEvents" }, { "validator", new BsonDocument("operation", new BsonDocument("$ne", "stock.source.alias.approve")) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await Assert.ThrowsAsync<MongoWriteException>(() => Approve(service, reviewer, p["proposalId"].AsInt64));
        Assert.Equal(before, await Rows(data, "inventoryPools").Find(new BsonDocument("_id", pool)).SingleAsync());
        Assert.Equal(0L, await Count(data, "stockSourceAliases")); Assert.Equal(0L, await Count(data, "stockSourceAliasDecisions"));
        Assert.Equal(3L, await Count(data, "idempotencyRecords"));
    }
}
