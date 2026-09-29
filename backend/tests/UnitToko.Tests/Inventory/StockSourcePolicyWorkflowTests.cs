using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourcePolicyServiceTests
{
    private static Task<BsonDocument> Pool(StoreTestData data, long pool) =>
        Rows(data, "inventoryPools").Find(new BsonDocument("_id", pool)).SingleAsync();
    private static async Task<BsonDocument> Effects(StoreTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { StockSourcePolicyBson.Proposals, StockSourcePolicyBson.Decisions, StockSourcePolicyBson.Policies,
            "auditEvents", "idempotencyRecords", "inventoryPools" })
            result[name] = new BsonArray(await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    private async Task<BsonDocument> Propose(StoreTestData data, long pool, UnitToko.Modules.Identity.Contracts.ActorContext maker, string key = "policy") =>
        await Service(data).ProposeOperatingPolicyAsync(maker, pool, Expected(await Service(data).PreviewOperatingPolicyAsync(maker, pool)), Input(key), Meta(key));

    [Theory]
    [InlineData("owner")] [InlineData("linked")] [InlineData("mfa")] [InlineData("same-account")]
    [InlineData("same-person")] [InlineData("missing-person")] [InlineData("inactive-scope")] [InlineData("no-permission")]
    public async Task Policy_review_requires_current_all_channel_authority_and_independent_people(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data, true); var service = Service(data);
        var proposal = await Propose(data, pool, maker);
        if (fault == "mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(5).AddMilliseconds(1);
        if (fault is "owner" or "linked")
            await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", fault == "owner" ? 2200L : 2201L),
                Builders<BsonDocument>.Update.Set("status", "revoked"));
        if (fault == "no-permission")
            await Rows(data, "authorizationGrants").UpdateManyAsync(new BsonDocument("userId", reviewer.UserId),
                Builders<BsonDocument>.Update.Set("permissions", new BsonArray { Permissions.StockSourceRead }));
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
                    { "memberUserIds", new BsonArray { maker.UserId, reviewer.UserId } } }));
        }
        if (fault == "missing-person") await Rows(data, "personAccountBindings").DeleteOneAsync(new BsonDocument("_id", reviewer.UserId));
        if (fault == "inactive-scope") await Rows(data, "authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 104L),
            Builders<BsonDocument>.Update.Set("status", "suspended"));
        var before = await Effects(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideOperatingPolicyAsync(reviewer, proposal["proposalId"].AsInt64,
            1, StockSourceSetupDecision.Approve, "Synthetic independent review", Meta("denied")));
        Assert.Equal(before, await Effects(data));
    }

    [Theory]
    [InlineData("settings")] [InlineData("mapping")] [InlineData("suspension")] [InlineData("policy")]
    public async Task Changed_binding_rejects_old_approval_but_allows_independent_rejection(string change)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data, change == "mapping"); var service = Service(data);
        var original = await Propose(data, pool, maker); data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        if (change == "settings")
        {
            var revision = await service.ProposeRevisionAsync(maker, pool, 1, Source() with { SafetyBufferBaseUnits = 3 }, Meta("settings"));
            await service.DecideRevisionAsync(reviewer, revision["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic settings", Meta("settings-approved"));
        }
        if (change == "mapping")
        {
            var alias = await service.ProposeAliasAsync(maker, pool, 1, Linked, 1, false, Meta("disable"));
            await service.DecideAliasAsync(reviewer, alias["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic disable", Meta("disabled"));
        }
        if (change == "suspension")
        {
            var preview = await service.PreviewSuspensionAsync(maker, pool);
            await service.SuspendAsync(maker, pool, new(preview["poolRevision"].AsInt64, preview["suspensionRevision"].AsInt64, preview["configurationHash"].AsString), Meta("stop"));
        }
        if (change == "policy")
        {
            var other = await Propose(data, pool, maker, "alternative");
            await service.DecideOperatingPolicyAsync(reviewer, other["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic current policy", Meta("other-approved"));
        }
        var before = await Effects(data);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.DecideOperatingPolicyAsync(reviewer, original["proposalId"].AsInt64,
            1, StockSourceSetupDecision.Approve, "Stale review", Meta("stale")));
        Assert.Equal(before, await Effects(data));
        var rejected = await service.DecideOperatingPolicyAsync(reviewer, original["proposalId"].AsInt64, 1, StockSourceSetupDecision.Reject, "Obsolete review", Meta("reject"));
        Assert.Equal("reject", rejected["status"].AsString);
        Assert.Equal(rejected, await service.RecoverOperatingPolicyAsync(reviewer, pool, "stock.source.policy.reject", "reject", "recover"));
    }

    [Fact]
    public async Task Command_only_users_recover_own_results_without_read_rights_or_missing_receipt_mutation()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        foreach (var actor in new[] { maker, reviewer })
            await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
                Builders<BsonDocument>.Update.Set("permissions", new BsonArray { actor == maker ? Permissions.StockSourcePolicyPropose : Permissions.StockSourcePolicyApprove }));
        var proposed = await Propose(data, pool, maker);
        var accepted = await service.DecideOperatingPolicyAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic review", Meta("approve"));
        Assert.Equal(proposed, await service.RecoverOperatingPolicyAsync(maker, pool, "stock.source.policy.propose", "policy", "recover"));
        Assert.Equal(accepted, await service.RecoverOperatingPolicyAsync(reviewer, pool, "stock.source.policy.approve", "approve", "recover"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadOperatingPolicyAsync(maker, pool));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadOperatingPolicyProposalAsync(reviewer, pool, proposed["proposalId"].AsInt64));
        var another = await data.AddActorAsync(3300, Owner.ToScope(), Permissions.StockSourcePolicyPropose);
        var before = await Effects(data); var counters = await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.NotEmpty(counters);
        Assert.Null(await service.RecoverOperatingPolicyAsync(another, pool, "stock.source.policy.propose", "policy", "recover"));
        Assert.Null(await service.RecoverOperatingPolicyAsync(maker, pool, "stock.source.policy.propose", "missing", "recover"));
        Assert.Equal(before, await Effects(data));
        Assert.Equal(counters, await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    }

    [Fact]
    public async Task Accepted_policy_becomes_stale_after_stop_and_reapproval_preserves_exact_stock_and_lots()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        await InventoryGraphTests.InstallBalances(data); await Rows(data, "lots").InsertOneAsync(InventoryGraphTests.Lot());
        var balance = InventoryBson.Balance(new InventoryBalanceDocument(7000, pool,
            new(new(Owner, new(200), new(201), new(202)), 9007199254740993, 2, 3, 4, 1)));
        await Rows(data, "stockBalances").InsertOneAsync(balance);
        var physical = await Physical(data);
        var first = await Propose(data, pool, maker);
        var accepted = await service.DecideOperatingPolicyAsync(reviewer, first["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic initial policy", Meta("approved"));
        var old = await service.ReadOperatingPolicyProposalAsync(maker, pool, first["proposalId"].AsInt64);
        await Assert.ThrowsAsync<PersistenceConflictException>(async () => await service.ProposeOperatingPolicyAsync(maker, pool,
            Expected(await service.PreviewOperatingPolicyAsync(maker, pool)), Input("policy"), Meta("unchanged")));
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        var preview = await service.PreviewSuspensionAsync(maker, pool);
        await service.SuspendAsync(maker, pool, new(preview["poolRevision"].AsInt64, preview["suspensionRevision"].AsInt64, preview["configurationHash"].AsString), Meta("stop"));
        Assert.False((await service.ReadOperatingPolicyAsync(maker, pool))["bindingCurrent"].AsBoolean);
        Assert.Equal(old, await service.ReadOperatingPolicyProposalAsync(maker, pool, first["proposalId"].AsInt64));
        var second = await Propose(data, pool, maker, "next");
        var next = await service.DecideOperatingPolicyAsync(reviewer, second["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic changed binding", Meta("next-approved"));
        Assert.Equal(2L, next["policyRevision"].AsInt64);
        Assert.True((await service.ReadOperatingPolicyAsync(maker, pool))["bindingCurrent"].AsBoolean);
        Assert.Equal(accepted, await service.RecoverOperatingPolicyAsync(reviewer, pool, "stock.source.policy.approve", "approved", "recover"));
        Assert.Equal(physical, await Physical(data)); Assert.Equal(balance, await Rows(data, "stockBalances").Find(new BsonDocument("_id", 7000L)).SingleAsync());
        Assert.False((await Pool(data, pool))["active"].AsBoolean);
    }

    [Theory]
    [InlineData("missing")] [InlineData("warning")] [InlineData("unique")] [InlineData("lookup")]
    public async Task Draft_storage_must_be_fully_enforced_before_any_policy_effect(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, _, pool) = await Setup(data); var service = Service(data);
        var expected = Expected(await service.PreviewOperatingPolicyAsync(maker, pool)); var before = await Effects(data);
        if (fault == "missing") await data.Database.DropCollectionAsync(StockSourcePolicyBson.Decisions);
        if (fault == "warning") await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", StockSourcePolicyBson.Proposals }, { "validationAction", "warn" } });
        if (fault == "unique") await Rows(data, StockSourcePolicyBson.Policies).Indexes.DropOneAsync("ux_stock_source_policy_revision");
        if (fault == "lookup") await Rows(data, "idempotencyRecords").Indexes.DropOneAsync("ix_stock_source_policy_receipt");
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ProposeOperatingPolicyAsync(maker, pool, expected, Input(), Meta("unsafe")));
        Assert.Equal(before, await Effects(data));
    }

    [Theory]
    [InlineData("writer")] [InlineData("tenant")] [InlineData("feed")]
    public async Task Mongo_validator_rejects_inconsistent_web_writer_facts(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, _, pool) = await Setup(data);
        await Propose(data, pool, maker);
        var row = await Rows(data, StockSourcePolicyBson.Proposals).Find(new BsonDocument()).SingleAsync();
        row["input"][fault == "writer" ? "writer" : fault == "tenant" ? "externalTenant" : "feedIntervalMilliseconds"] =
            fault == "writer" ? new BsonString("externalSystem") : fault == "tenant" ? new BsonString("foreign-tenant") : new BsonInt64(1);
        await Assert.ThrowsAsync<MongoWriteException>(() => Rows(data, StockSourcePolicyBson.Proposals).ReplaceOneAsync(new BsonDocument("_id", row["_id"]), row));
    }

    [Theory]
    [InlineData("absent")] [InlineData("proposer")] [InlineData("extra-participant")]
    public async Task Approval_receipt_requires_exact_original_independent_review_participants(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var proposal = await Propose(data, pool, maker);
        await service.DecideOperatingPolicyAsync(reviewer, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic review", Meta("approve"));
        var filter = new BsonDocument("operation", "stock.source.policy.approve");
        if (fault == "absent") await Rows(data, "idempotencyRecords").UpdateOneAsync(filter, Builders<BsonDocument>.Update.Unset("personApproval"));
        else await Rows(data, "idempotencyRecords").UpdateOneAsync(filter,
            Builders<BsonDocument>.Update.Set("personApproval.approval." + (fault == "proposer" ? "proposedBy" : "requestedBy"), 9999L));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ReadOperatingPolicyAsync(maker, pool));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.RecoverOperatingPolicyAsync(reviewer, pool, "stock.source.policy.approve", "approve", "recover"));
    }
}