using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed partial class StockSourceRevisionTests(MongoFixture fixture)
{
    private static readonly StockLocation Location = new(new(100), new(101), new(102), new(103));
    private static StockSourceSetupInput Input() => new(Location, "synthetic-pool", "Original synthetic pool",
        StockAuthorityMode.WebLedger, StockPoolControl.ExclusiveWebAllocation, 60000, 2, null, null,
        "synthetic-owner", "synthetic-handoff");
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic source revision", "revision-tests");
    private StockSourceSetupService Service(StoreTestData data) => new(data.Database, new(fixture.Client), new(data.Database), data.Clock);
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private static Task<long> Count(StoreTestData data, string name) => Rows(data, name).CountDocumentsAsync(new BsonDocument());
    private static async Task<(ActorContext Maker, ActorContext Reviewer, long Pool)> Setup(StoreTestData data, StockSourceSetupService service)
    {
        var maker = await data.AddActorAsync(1000, Location.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
        var reviewer = await data.AddActorAsync(2000, Location.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId);
        var proposal = await service.ProposeAsync(maker, Input(), Meta("original"));
        var result = await service.DecideAsync(reviewer, proposal["proposalId"].AsInt64, 1,
            StockSourceSetupDecision.Approve, "synthetic-original-review", Meta("original-approval"));
        return (maker, reviewer, result["poolId"].AsInt64);
    }

    [Fact]
    public async Task Approved_revision_preserves_original_creation_and_one_inactive_pool_without_stock_effects()
    {
        await using var data = await Data(); var service = Service(data);
        var (maker, reviewer, pool) = await Setup(data, service);
        var original = await Rows(data, "stockSourceProposals").Find(new BsonDocument()).SingleAsync();
        var changed = Input() with { Label = "Reviewed synthetic label", MaximumAgeMilliseconds = 90000, SafetyBufferBaseUnits = 3 };
        var proposed = await service.ProposeRevisionAsync(maker, pool, 1, changed, Meta("revision"));
        Assert.Equal(proposed, await service.ProposeRevisionAsync(maker, pool, 1, changed, Meta("revision")));
        var id = proposed["proposalId"].AsInt64;
        var approval = await service.DecideRevisionAsync(reviewer, id, 1, StockSourceSetupDecision.Approve, "synthetic-revision-review", Meta("approve"));
        Assert.Equal(approval, await service.DecideRevisionAsync(reviewer, id, 1, StockSourceSetupDecision.Approve, "synthetic-revision-review", Meta("approve")));
        var current = await service.ReadCurrentSourceAsync(maker, Location, pool);
        Assert.Equal(2L, current["pool"]["revision"].AsInt64); Assert.False(current["pool"]["active"].AsBoolean);
        Assert.Equal(StockSourceSetupBson.Input(changed), current["input"]);
        var historical = await service.ReadRevisionAsync(reviewer, Location, id);
        Assert.Equal(StockSourceSetupBson.Input(Input()), historical!["proposal"]["baseInput"]);
        Assert.Equal("approve", historical["decision"]["decision"].AsString);
        Assert.Equal(1, await Count(data, "stockSourceRevisions"));
        Assert.Equal(1, await Count(data, "stockSourceRevisionDecisions"));
        Assert.Equal(4, await Count(data, "auditEvents")); Assert.Equal(4, await Count(data, "idempotencyRecords"));
        foreach (var name in new[] { "lots", "stockBalances", "reservations", "journalEntries" }) Assert.Equal(0, await Count(data, name));
        var next = changed with { Label = "Third reviewed version" };
        var nextProposal = await service.ProposeRevisionAsync(maker, pool, 2, next, Meta("next"));
        await service.DecideRevisionAsync(reviewer, nextProposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-next-review", Meta("next-approve"));
        Assert.Equal(3, (await service.ReadCurrentSourceAsync(maker, Location, pool))["pool"]["revision"].AsInt64);
        Assert.Equal(approval, await service.DecideRevisionAsync(reviewer, id, 1, StockSourceSetupDecision.Approve, "synthetic-revision-review", Meta("approve")));
        Assert.Equal(changed.Label, (await service.ReadRevisionAsync(reviewer, Location, id))!["appliedRevision"]["input"]["label"].AsString);
        Assert.True(proposed["proposalId"].IsInt64);
        Assert.Equal(1, await Count(data, "inventoryPools"));
        Assert.Equal(original, await Rows(data, "stockSourceProposals").Find(new BsonDocument()).SingleAsync());
        Assert.Equal(0, await Count(data, "movements"));
    }

    private async Task<StoreTestData> Data()
    {
        var data = await StoreTestData.CreateAsync(fixture);
        try
        {
            foreach (var name in StockSourceRevisionSchemas.Collections)
            {
                await data.Database.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument>
                {
                    Validator = StockSourceRevisionSchemas.Validator(name), ValidationAction = DocumentValidationAction.Error,
                    ValidationLevel = DocumentValidationLevel.Strict
                });
                var key = StockSourceRevisionSchemas.Key(name);
                if (!key.Equals(new BsonDocument("_id", 1)))
                    await Rows(data, name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key,
                        new CreateIndexOptions { Unique = true, Name = "ux_revision_business_key" }));
            }
            return data;
        }
        catch { await data.DisposeAsync(); throw; }
    }
    private static StockSourceSetupInput Changed() => Input() with { Label = "Changed source settings" };
    private static Task<BsonDocument> Propose(StockSourceSetupService service, ActorContext maker, long pool, string key = "revision") =>
        service.ProposeRevisionAsync(maker, pool, 1, Changed(), Meta(key));
    private static Task<BsonDocument> Approve(StockSourceSetupService service, ActorContext reviewer, long id, string key = "approval") =>
        service.DecideRevisionAsync(reviewer, id, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta(key));

    [Theory]
    [InlineData(StockSourceSetupDecision.Reject)]
    [InlineData(StockSourceSetupDecision.Withdraw)]
    public async Task Nonapproval_is_terminal_and_keeps_current_settings(StockSourceSetupDecision decision)
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        var id = (await Propose(service, maker, pool))["proposalId"].AsInt64;
        var actor = decision == StockSourceSetupDecision.Withdraw ? maker : reviewer;
        var result = await service.DecideRevisionAsync(actor, id, 1, decision, "synthetic-review", Meta("decision"));
        Assert.True(result["poolRevision"].IsBsonNull);
        Assert.Equal(result, await service.DecideRevisionAsync(actor, id, 1, decision, "synthetic-review", Meta("decision")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => Approve(service, reviewer, id));
        Assert.Equal(0, await Count(data, "stockSourceRevisions"));
        Assert.Equal(1, await Count(data, "stockSourceRevisionDecisions"));
        Assert.Equal(StockSourceSetupBson.Input(Input()), (await service.ReadCurrentSourceAsync(maker, Location, pool))["input"]);
    }

    [Fact]
    public async Task Second_proposal_based_on_old_settings_cannot_overwrite_newly_approved_settings()
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        var first = (await Propose(service, maker, pool))["proposalId"].AsInt64;
        var second = (await service.ProposeRevisionAsync(maker, pool, 1, Input() with { Label = "Competing proposal" }, Meta("second")))["proposalId"].AsInt64;
        await Approve(service, reviewer, first);
        var failure = await Assert.ThrowsAsync<PersistenceConflictException>(() => Approve(service, reviewer, second, "second-approval"));
        Assert.Equal("stock_source_revision_changed", failure.Code);
        Assert.Equal(1, await Count(data, "stockSourceRevisions")); Assert.Equal(1, await Count(data, "stockSourceRevisionDecisions"));
        Assert.Equal(Changed().Label, (await service.ReadCurrentSourceAsync(maker, Location, pool))["input"]["label"].AsString);
        await service.DecideRevisionAsync(maker, second, 1, StockSourceSetupDecision.Withdraw, "outdated proposal", Meta("withdraw"));
    }

    [Theory]
    [InlineData("no-permission")] [InlineData("same-account")] [InlineData("same-person")]
    [InlineData("missing-person")] [InlineData("stale-mfa")] [InlineData("revoked")] [InlineData("scope-suspended")]
    [InlineData("wrong-revision")]
    public async Task Current_review_authority_is_required_and_failures_leave_no_revision(string fault)
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        var id = (await Propose(service, maker, pool))["proposalId"].AsInt64;
        long expected = 1;
        switch (fault)
        {
            case "no-permission": reviewer = maker; break;
            case "same-account": await data.AddGrantAsync(maker, 5000, Location.ToScope(), [Permissions.StockSourceApprove]); reviewer = maker; break;
            case "same-person":
                var group = PersonTestData.GroupId(1000);
                await Rows(data, "personEquivalenceGroups").UpdateOneAsync(new BsonDocument("_id", group), new BsonDocument("$set", new BsonDocument
                {
                    { "memberPersonIds", new BsonArray { PersonTestData.PersonId(1000), PersonTestData.PersonId(2000) } },
                    { "memberUserIds", new BsonArray { 1000L, 2000L } }
                }));
                await Rows(data, "personRecords").UpdateOneAsync(new BsonDocument("_id", PersonTestData.PersonId(2000)),
                    new BsonDocument("$set", new BsonDocument("currentEquivalenceId", group))); break;
            case "missing-person": await Rows(data, "personAccountBindings").DeleteOneAsync(new BsonDocument("_id", reviewer.UserId)); break;
            case "stale-mfa": data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(5).AddMilliseconds(1); break;
            case "revoked": await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", reviewer.UserId),
                Builders<BsonDocument>.Update.Set("status", "revoked")); break;
            case "scope-suspended": await Rows(data, "authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 102L),
                Builders<BsonDocument>.Update.Set("status", "suspended")); break;
            case "wrong-revision": expected = 2; break;
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideRevisionAsync(reviewer, id, expected,
            StockSourceSetupDecision.Approve, "synthetic-review", Meta("approval")));
        Assert.Equal(0, await Count(data, "stockSourceRevisions")); Assert.Equal(0, await Count(data, "stockSourceRevisionDecisions"));
        Assert.Equal(3, await Count(data, "auditEvents")); Assert.Equal(3, await Count(data, "idempotencyRecords"));
        Assert.Equal(1, (await Rows(data, "inventoryPools").Find(new BsonDocument("_id", pool)).SingleAsync())["revision"].AsInt64);
    }

    [Theory]
    [InlineData("same")] [InlineData("physical-key")] [InlineData("location")] [InlineData("mode")]
    [InlineData("negative-buffer")] [InlineData("zero-age")] [InlineData("age-overflow")] [InlineData("uncanonical-label")]
    public void Revision_rules_reject_identity_changes_missing_facts_and_no_op(string fault)
    {
        var changed = fault switch
        {
            "same" => Input(),
            "physical-key" => Changed() with { PhysicalKey = "another-pool" },
            "location" => Changed() with { Location = Location with { ChannelId = new(104) } },
            "mode" => Changed() with { Mode = StockAuthorityMode.ExternalLedger },
            "negative-buffer" => Changed() with { SafetyBufferBaseUnits = -1 },
            "zero-age" => Changed() with { MaximumAgeMilliseconds = 0 },
            "age-overflow" => Changed() with { MaximumAgeMilliseconds = long.MaxValue },
            _ => Changed() with { Label = " padded " }
        };
        Assert.Throws<DomainValidationException>(() => StockSourceRevisionPolicy.Validate(Input(), changed));
    }

    [Fact]
    public async Task Active_pool_requires_handoff_and_changed_idempotency_body_cannot_rewrite_history()
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument("_id", pool), Builders<BsonDocument>.Update.Set("active", true));
        var id = (await Propose(service, maker, pool))["proposalId"].AsInt64;
        var changed = await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ProposeRevisionAsync(maker, pool, 1,
            Input() with { Label = "Different request" }, Meta("revision")));
        Assert.Equal("idempotency_key_reused", changed.Code);
        var denied = await Assert.ThrowsAsync<PersistenceConflictException>(() => Approve(service, reviewer, id));
        Assert.Equal("stock_source_handoff_required", denied.Code);
        Assert.True((await Rows(data, "inventoryPools").Find(new BsonDocument("_id", pool)).SingleAsync())["active"].AsBoolean);
        Assert.Equal(0, await Count(data, "stockSourceRevisions")); Assert.Equal(0, await Count(data, "stockSourceRevisionDecisions"));
    }

    [Fact]
    public async Task Actual_audit_insert_failure_rolls_back_pool_revision_history_decision_and_receipt()
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        var id = (await Propose(service, maker, pool))["proposalId"].AsInt64;
        var before = await Rows(data, "inventoryPools").Find(new BsonDocument("_id", pool)).SingleAsync();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", "auditEvents" }, { "validator", new BsonDocument("operation", new BsonDocument("$ne", "stock.source.revision.approve")) },
            { "validationLevel", "strict" }, { "validationAction", "error" }
        });
        await Assert.ThrowsAsync<MongoWriteException>(() => Approve(service, reviewer, id));
        Assert.Equal(before, await Rows(data, "inventoryPools").Find(new BsonDocument("_id", pool)).SingleAsync());
        Assert.Equal(0, await Count(data, "stockSourceRevisions")); Assert.Equal(0, await Count(data, "stockSourceRevisionDecisions"));
        Assert.Equal(3, await Count(data, "auditEvents")); Assert.Equal(3, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData("missing")] [InlineData("warn")] [InlineData("unique")]
    public async Task Revision_storage_must_be_installed_and_enforced_before_writing(string fault)
    {
        await using var data = await Data(); var service = Service(data); var (maker, _, pool) = await Setup(data, service);
        if (fault == "missing") await data.Database.DropCollectionAsync("stockSourceRevisions");
        if (fault == "warn") await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument
            { { "collMod", "stockSourceRevisionProposals" }, { "validationAction", "warn" } });
        if (fault == "unique") await Rows(data, "stockSourceRevisions").Indexes.DropOneAsync("ux_revision_business_key");
        await Assert.ThrowsAsync<DomainValidationException>(() => Propose(service, maker, pool));
        Assert.Equal(0, await Count(data, "stockSourceRevisionProposals")); Assert.Equal(2, await Count(data, "idempotencyRecords"));
    }

    [Fact]
    public async Task Foreign_scope_and_another_proposer_cannot_read_review_or_withdraw()
    {
        await using var data = await Data(); var service = Service(data); var (maker, _, pool) = await Setup(data, service);
        var id = (await Propose(service, maker, pool))["proposalId"].AsInt64;
        await Rows(data, "authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel,
            100, AuthorizationStatus.Active, 1)));
        var otherLocation = Location with { ChannelId = new(104) };
        var foreign = await data.AddActorAsync(3000, otherLocation.ToScope(), Permissions.StockSourceRead, Permissions.StockSourcePropose, Permissions.StockSourceApprove);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadCurrentSourceAsync(foreign, otherLocation, pool));
        Assert.Null(await service.ReadRevisionAsync(foreign, otherLocation, id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Propose(service, foreign, pool, "foreign"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Approve(service, foreign, id));
        var otherMaker = await data.AddActorAsync(4000, Location.ToScope(), Permissions.StockSourcePropose);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideRevisionAsync(otherMaker, id, 1,
            StockSourceSetupDecision.Withdraw, "synthetic-review", Meta("withdraw")));
        Assert.Equal(3, await Count(data, "idempotencyRecords"));
    }
}
