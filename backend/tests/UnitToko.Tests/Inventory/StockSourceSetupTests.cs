using UnitToko.Modules.Organizations;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class StockSourceSetupTests(MongoFixture fixture)
{
    private static readonly StockLocation Location = new(new(100), new(101), new(102), new(103));
    private static StockSourceSetupInput Input(string key = "synthetic-pool") =>
        new(Location, key, "Synthetic stock pool", StockAuthorityMode.WebLedger, StockPoolControl.ExclusiveWebAllocation,
            60000, 2, null, null, "synthetic-discrepancy-owner", "synthetic-reviewed-handoff");
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic setup verification", "setup-tests");
    private StockSourceSetupService Service(StoreTestData data) => new(data.Database, new(fixture.Client), new(data.Database), data.Clock);
    internal static async Task InstallAsync(StoreTestData data)
    {
        foreach (var name in StockSourceSetupSchemas.Collections)
        {
            await data.Database.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument>
            {
                Validator = StockSourceSetupSchemas.Validator(name), ValidationAction = DocumentValidationAction.Error,
                ValidationLevel = DocumentValidationLevel.Strict
            });
            var key = StockSourceSetupSchemas.Key(name);
            if (!key.Equals(new BsonDocument("_id", 1)))
                await data.Database.GetCollection<BsonDocument>(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key,
                    new CreateIndexOptions { Unique = true, Name = "ux_setup_business_key" }));
        }
    }
    private async Task<StoreTestData> Data()
    {
        var data = await StoreTestData.CreateAsync(fixture);
        return data; // The registered forward migration installs the production storage contract.
    }
    private static async Task<(ActorContext Maker, ActorContext Reviewer)> Actors(StoreTestData data)
    {
        var maker = await data.AddActorAsync(1000, Location.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
        var reviewer = await data.AddActorAsync(2000, Location.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId);
        return (maker, reviewer);
    }
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string collection) => data.Database.GetCollection<BsonDocument>(collection);
    private static Task<long> Count(StoreTestData data, string collection) => Rows(data, collection).CountDocumentsAsync(new BsonDocument());

    [Fact]
    public async Task Proposal_and_independent_approval_are_durable_replayable_and_leave_pool_inactive()
    {
        await using var data = await Data(); var (maker, reviewer) = await Actors(data); var service = Service(data);
        var proposed = await service.ProposeAsync(maker, Input(), Meta("propose"));
        Assert.Equal(proposed, await service.ProposeAsync(maker, Input(), Meta("propose")));
        var id = proposed["proposalId"].AsInt64;
        var before = await service.ReadAsync(maker, Location, id);
        Assert.True(before!["decision"].IsBsonNull);
        var approved = await service.DecideAsync(reviewer, id, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("approve"));
        Assert.Equal(approved, await service.DecideAsync(reviewer, id, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("approve")));
        var after = await service.ReadAsync(reviewer, Location, id);
        Assert.Equal("approve", after!["decision"]["decision"].AsString);
        Assert.False(after["pool"]["active"].AsBoolean);
        Assert.Equal(approved["poolId"], after["pool"]["_id"]);
        Assert.Equal(1, await Count(data, "stockSourceProposals")); Assert.Equal(1, await Count(data, "stockSourceDecisions"));
        Assert.Equal(1, await Count(data, "inventoryPools")); Assert.Equal(1, await Count(data, "inventoryPoolRegistrations"));
        Assert.Equal(2, await Count(data, "auditEvents")); Assert.Equal(2, await Count(data, "idempotencyRecords"));
        await Rows(data, "lots").InsertOneAsync(InventoryGraphTests.Lot());
        using (var tx = await fixture.Client.StartSessionAsync())
        {
            tx.StartTransaction();
            var balance = InventoryGraphTests.Balance() with { PhysicalPoolId = approved["poolId"].AsInt64 };
            var error = await Assert.ThrowsAsync<DomainValidationException>(() => new InventoryGraph(data.Database).RequireAsync(tx, balance, true, default));
            Assert.Equal("inventory_graph_unavailable", error.Code);
            await tx.AbortTransactionAsync();
        }
        var listed = await service.ListAsync(reviewer, Location, 0, 10);
        Assert.Equal(1, listed.Total); Assert.Single(listed.Items);
        Assert.Equal(0, await Count(data, "stockBalances")); Assert.Equal(0, await Count(data, "movements"));
    }

    [Theory]
    [InlineData(StockSourceSetupDecision.Reject)]
    [InlineData(StockSourceSetupDecision.Withdraw)]
    public async Task Terminal_nonapproval_cannot_create_a_pool_or_be_changed(StockSourceSetupDecision decision)
    {
        await using var data = await Data(); var (maker, reviewer) = await Actors(data); var service = Service(data);
        var proposed = await service.ProposeAsync(maker, Input(), Meta("propose")); var id = proposed["proposalId"].AsInt64;
        var actor = decision == StockSourceSetupDecision.Withdraw ? maker : reviewer;
        var result = await service.DecideAsync(actor, id, 1, decision, "synthetic-review", Meta("decide"));
        Assert.Equal(result, await service.DecideAsync(actor, id, 1, decision, "synthetic-review", Meta("decide")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.DecideAsync(reviewer, id, 1,
            StockSourceSetupDecision.Approve, "synthetic-review", Meta("later")));
        Assert.Equal(0, await Count(data, "inventoryPools")); Assert.Equal(1, await Count(data, "stockSourceDecisions"));
        Assert.True((await service.ReadAsync(maker, Location, id))!["pool"].IsBsonNull);
    }

    [Theory]
    [InlineData("no-permission")] [InlineData("same-account")] [InlineData("stale-mfa")]
    [InlineData("missing-person")] [InlineData("wrong-revision")] [InlineData("inactive-scope")] [InlineData("same-person")]
    public async Task Invalid_approval_rolls_back_all_effects(string fault)
    {
        await using var data = await Data(); var (maker, reviewer) = await Actors(data); var service = Service(data);
        var id = (await service.ProposeAsync(maker, Input(), Meta("propose")))["proposalId"].AsInt64;
        long revision = 1;
        switch (fault)
        {
            case "no-permission": reviewer = maker; break;
            case "same-account":
                await data.AddGrantAsync(maker, 5000, Location.ToScope(), [Permissions.StockSourceApprove]); reviewer = maker; break;
            case "stale-mfa": data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(5).AddMilliseconds(1); break;
            case "missing-person": await Rows(data, "personAccountBindings").DeleteOneAsync(new BsonDocument("_id", reviewer.UserId)); break;
            case "wrong-revision": revision = 2; break;
            case "inactive-scope":
                await Rows(data, "authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 102L), new BsonDocument("$set", new BsonDocument("status", "suspended"))); break;
            case "same-person":
                await LinkPeople(data); break;
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideAsync(reviewer, id, revision,
            StockSourceSetupDecision.Approve, "synthetic-review", Meta("approve")));
        Assert.Equal(0, await Count(data, "stockSourceDecisions")); Assert.Equal(0, await Count(data, "inventoryPools"));
        Assert.Equal(0, await Count(data, "inventoryPoolRegistrations")); Assert.Equal(1, await Count(data, "idempotencyRecords"));
        Assert.Equal(1, await Count(data, "auditEvents"));
    }

    [Fact]
    public async Task Another_proposer_cannot_withdraw_and_read_permission_cannot_write()
    {
        await using var data = await Data(); var (maker, _) = await Actors(data); var service = Service(data);
        var id = (await service.ProposeAsync(maker, Input(), Meta("propose")))["proposalId"].AsInt64;
        var other = await data.AddActorAsync(3000, Location.ToScope(), Permissions.StockSourcePropose);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideAsync(other, id, 1,
            StockSourceSetupDecision.Withdraw, "synthetic-review", Meta("withdraw")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(other, Location, id));
        var reader = await data.AddActorAsync(4000, Location.ToScope(), Permissions.StockSourceRead);
        Assert.NotNull(await service.ReadAsync(reader, Location, id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ProposeAsync(reader, Input(), Meta("reader")));
    }

    [Fact]
    public async Task Exact_scopes_filter_count_and_items_before_paging_and_deny_cross_channel_commands()
    {
        await using var data = await Data(); var (maker, reviewer) = await Actors(data); var service = Service(data);
        await Rows(data, "authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel,
            100, AuthorizationStatus.Active, 1)));
        var otherLocation = Location with { ChannelId = new(104) };
        var other = await data.AddActorAsync(3000, otherLocation.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
        var own = await service.ProposeAsync(maker, Input(), Meta("own"));
        var different = await service.ProposeAsync(other, Input("second") with { Location = otherLocation }, Meta("other"));
        var page = await service.ListAsync(maker, Location, 0, 1);
        Assert.Equal(1, page.Total); Assert.Equal(own["proposalId"], page.Items.Single()["proposal"]["_id"]);
        Assert.Null(await service.ReadAsync(maker, Location, different["proposalId"].AsInt64));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListAsync(maker, otherLocation, 0, 1));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideAsync(reviewer, different["proposalId"].AsInt64,
            1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("cross")));
    }

    [Fact]
    public async Task Same_physical_key_is_unique_across_channels_and_failed_approval_has_no_partial_effects()
    {
        await using var data = await Data(); var (maker, reviewer) = await Actors(data); var service = Service(data);
        await Rows(data, "authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel,
            100, AuthorizationStatus.Active, 1)));
        var second = Location with { ChannelId = new(104) };
        await data.AddGrantAsync(maker, 5000, second.ToScope(), [Permissions.StockSourcePropose]);
        await data.AddGrantAsync(reviewer, 6000, second.ToScope(), [Permissions.StockSourceApprove]);
        var first = (await service.ProposeAsync(maker, Input(), Meta("first")))["proposalId"].AsInt64;
        var duplicate = (await service.ProposeAsync(maker, Input() with { Location = second }, Meta("second")))["proposalId"].AsInt64;
        await service.DecideAsync(reviewer, first, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("first-approve"));
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => service.DecideAsync(reviewer, duplicate, 1,
            StockSourceSetupDecision.Approve, "synthetic-review", Meta("second-approve")));
        Assert.Equal("stock_pool_key_registered", error.Code);
        Assert.Equal(1, await Count(data, "inventoryPools")); Assert.Equal(1, await Count(data, "stockSourceDecisions"));
        Assert.Equal(3, await Count(data, "idempotencyRecords"));
    }

    [Fact]
    public async Task Changed_body_cannot_reuse_request_key_and_revoked_actor_cannot_replay()
    {
        await using var data = await Data(); var (maker, reviewer) = await Actors(data); var service = Service(data);
        var id = (await service.ProposeAsync(maker, Input(), Meta("propose")))["proposalId"].AsInt64;
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ProposeAsync(maker, Input("changed"), Meta("propose")));
        await service.DecideAsync(reviewer, id, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("approve"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.DecideAsync(reviewer, id, 1,
            StockSourceSetupDecision.Approve, "different-review", Meta("approve")));
        await Rows(data, "identities").UpdateOneAsync(new BsonDocument("_id", reviewer.UserId), new BsonDocument("$inc", new BsonDocument("grantVersion", 1L)));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideAsync(reviewer, id, 1,
            StockSourceSetupDecision.Approve, "synthetic-review", Meta("approve")));
        Assert.Equal(1, await Count(data, "stockSourceDecisions"));
    }

    [Fact]
    public async Task Audit_failure_after_pool_insertion_rolls_back_decision_pool_registration_and_receipt()
    {
        await using var data = await Data(); var (maker, reviewer) = await Actors(data); var service = Service(data);
        var id = (await service.ProposeAsync(maker, Input(), Meta("propose")))["proposalId"].AsInt64;
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", "auditEvents" }, { "validator", new BsonDocument("operation", new BsonDocument("$ne", "stock.source.approve")) },
            { "validationLevel", "strict" }, { "validationAction", "error" }
        });
        await Assert.ThrowsAsync<MongoWriteException>(() => service.DecideAsync(reviewer, id, 1,
            StockSourceSetupDecision.Approve, "synthetic-review", Meta("approve")));
        Assert.Equal(0, await Count(data, "inventoryPools")); Assert.Equal(0, await Count(data, "inventoryPoolRegistrations"));
        Assert.Equal(0, await Count(data, "stockSourceDecisions")); Assert.Equal(1, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData("missing")] [InlineData("validator")] [InlineData("unique-index")]
    public async Task Uninstalled_or_weakened_storage_denies_without_business_effects(string fault)
    {
        await using var data = await Data(); var (maker, _) = await Actors(data);
        if (fault == "missing") await data.Database.DropCollectionAsync("inventoryPools");
        if (fault == "validator") await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument
            { { "collMod", "stockSourceProposals" }, { "validationAction", "warn" } });
        if (fault == "unique-index") await Rows(data, "inventoryPoolRegistrations").Indexes.DropOneAsync("ux_inventory_physical_key");
        await Assert.ThrowsAsync<DomainValidationException>(() => Service(data).ProposeAsync(maker, Input(), Meta("propose")));
        Assert.Equal(0, await Count(data, "stockSourceProposals")); Assert.Equal(0, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData("proposal-audit")] [InlineData("decision-audit")] [InlineData("registration")]
    public async Task Missing_history_or_pool_binding_denies_reads(string fault)
    {
        await using var data = await Data(); var (maker, reviewer) = await Actors(data); var service = Service(data);
        var id = (await service.ProposeAsync(maker, Input(), Meta("propose")))["proposalId"].AsInt64;
        await service.DecideAsync(reviewer, id, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("approve"));
        if (fault == "registration") await Rows(data, "inventoryPoolRegistrations").DeleteManyAsync(new BsonDocument());
        else await Rows(data, "auditEvents").DeleteOneAsync(new BsonDocument("operation", fault == "proposal-audit" ? "stock.source.propose" : "stock.source.approve"));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ReadAsync(maker, Location, id));
    }

    [Fact]
    public async Task Two_independent_reviewers_cannot_commit_different_decisions_for_one_proposal()
    {
        await using var data = await Data(); var (maker, reviewer) = await Actors(data); var service = Service(data);
        var another = await data.AddActorAsync(3000, Location.ToScope(), Permissions.StockSourceApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, another.UserId);
        var id = (await service.ProposeAsync(maker, Input(), Meta("propose")))["proposalId"].AsInt64;
        async Task<bool> Attempt(ActorContext actor, StockSourceSetupDecision decision)
        {
            try { await service.DecideAsync(actor, id, 1, decision, "synthetic-review", Meta("decide-" + actor.UserId)); return true; }
            catch (PersistenceConflictException) { return false; }
        }
        var results = await Task.WhenAll(Attempt(reviewer, StockSourceSetupDecision.Approve), Attempt(another, StockSourceSetupDecision.Reject));
        Assert.Single(results, value => value);
        Assert.Equal(1, await Count(data, "stockSourceDecisions")); Assert.Equal(2, await Count(data, "idempotencyRecords"));
        var view = await service.ReadAsync(maker, Location, id);
        Assert.Equal(view!["decision"]["decision"].AsString == "approve" ? 1 : 0, await Count(data, "inventoryPools"));
    }

    [Fact]
    public async Task External_setup_saves_explicit_protocol_but_does_not_activate_the_pool()
    {
        await using var data = await Data(); var (maker, reviewer) = await Actors(data); var service = Service(data);
        var input = Input() with { Mode = StockAuthorityMode.ExternalLedger, Control = StockPoolControl.ExternalHold,
            ExternalNamespace = "synthetic-pos", ReservationProtocolReference = "synthetic-hold-protocol-v2",
            MaximumAgeMilliseconds = long.MaxValue / TimeSpan.TicksPerMillisecond, SafetyBufferBaseUnits = long.MaxValue };
        var id = (await service.ProposeAsync(maker, input, Meta("external")))["proposalId"].AsInt64;
        await service.DecideAsync(reviewer, id, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("approve"));
        var view = await service.ReadAsync(maker, Location, id);
        Assert.Equal(StockSourceSetupBson.Input(input), view!["proposal"]["input"]);
        Assert.False(view["pool"]["active"].AsBoolean);
    }

    [Theory]
    [InlineData("external-without-protocol")] [InlineData("web-with-external-control")]
    [InlineData("leading-space")] [InlineData("trailing-newline")] [InlineData("invalid-date")]
    public async Task Draft_database_constraints_reject_noncanonical_or_inconsistent_source_facts(string fault)
    {
        await using var data = await Data(); var (maker, _) = await Actors(data); var service = Service(data);
        var id = (await service.ProposeAsync(maker, Input(), Meta("propose")))["proposalId"].AsInt64;
        var row = await Rows(data, "stockSourceProposals").Find(new BsonDocument("_id", id)).SingleAsync();
        row["_id"] = 999999L;
        switch (fault)
        {
            case "external-without-protocol": row["input"]["mode"] = "ExternalLedger"; row["input"]["control"] = "ExternalHold"; break;
            case "web-with-external-control": row["input"]["control"] = "ExternalHold"; break;
            case "leading-space": row["input"]["label"] = " spaces"; break;
            case "trailing-newline": row["input"]["physicalKey"] = "pool\n"; break;
            case "invalid-date": row["proposedAt"] = new BsonDateTime(long.MaxValue); break;
        }
        await Assert.ThrowsAsync<MongoWriteException>(() => Rows(data, "stockSourceProposals").InsertOneAsync(row));
    }

    [Fact]
    public async Task Completion_guard_runs_for_both_new_work_and_replay_and_failure_rolls_back_new_work()
    {
        await using var data = await Data();
        var actor = await data.AddActorAsync(1000, Location.ToScope(), Permissions.StockSourcePropose);
        var shouldFail = true; var writes = 0; var checks = 0;
        Task<BsonDocument> Execute() => data.Store.ExecuteCommandAsync(actor, "synthetic.completion", Meta("completion"), new { Id = 1 },
            (snapshot, tx, token) => Task.FromResult(new CommandAuthorization(new(true, "allowed", [1000]), [])),
            async (tx, token) =>
            {
                writes++;
                await Rows(data, "orders").InsertOneAsync(tx, new BsonDocument { { "_id", 4000L }, { "schemaVersion", 1 } }, cancellationToken: token);
                return new BsonDocument("ok", true);
            }, default, (_, _, _) =>
            {
                checks++;
                if (shouldFail) throw new AuthorizationDeniedException("synthetic_final_guard");
                return Task.CompletedTask;
            });
        await Assert.ThrowsAsync<AuthorizationDeniedException>(Execute);
        Assert.Equal(0, await Count(data, "orders")); Assert.Equal(0, await Count(data, "idempotencyRecords"));
        shouldFail = false; var result = await Execute();
        shouldFail = true; await Assert.ThrowsAsync<AuthorizationDeniedException>(Execute);
        shouldFail = false; Assert.Equal(result, await Execute());
        Assert.Equal(2, writes); Assert.Equal(4, checks);
        Assert.Equal(1, await Count(data, "orders")); Assert.Equal(1, await Count(data, "idempotencyRecords"));
    }
    [Fact]
    public async Task Receipt_disappearing_after_recovery_lookup_never_resubmits_the_command()
    {
        await using var data = await Data(); var (maker, _) = await Actors(data);
        var original = await Service(data).ProposeAsync(maker, Input(), Meta("lost-receipt"));
        var removed = 0;
        var settings = fixture.Client.Settings.Clone();
        settings.ApplicationName = "StockSourceRecovery-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command =>
        {
            // The next ID allocation is after recovery's committed read and before its replay transaction.
            if (command.CommandName == "findAndModify" && command.Command.GetValue("findAndModify", "") == "idCounters" &&
                command.Command["query"].AsBsonDocument.GetValue("_id", "") == "stockSourceProposals" &&
                Interlocked.CompareExchange(ref removed, 1, 0) == 0)
                Rows(data, "idempotencyRecords").DeleteOne(new BsonDocument { { "actorId", maker.UserId },
                    { "operation", "stock.source.propose" }, { "key", "lost-receipt" } });
        });
        var client = new MongoClient(settings);
        var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new StockSourceSetupService(database, new(client), new(database), data.Clock);
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            service.RecoverAsync(maker, Location, "stock.source.propose", "lost-receipt", "recovery"));
        Assert.Equal("operation_receipt_unavailable", error.Code);
        Assert.Equal(1, removed); Assert.Equal(1, await Count(data, "stockSourceProposals"));
        Assert.Equal(1, await Count(data, "auditEvents")); Assert.Equal(0, await Count(data, "idempotencyRecords"));
        Assert.Equal(original["proposalId"], (await Rows(data, "stockSourceProposals").Find(new BsonDocument()).SingleAsync())["_id"]);
    }

    [Fact]
    public async Task Receipt_only_execution_does_not_invoke_writer_and_still_rechecks_current_authority_on_replay()
    {
        await using var data = await Data();
        var actor = await data.AddActorAsync(1000, Location.ToScope(), Permissions.StockSourcePropose);
        var writes = 0; var checks = 0;
        Task<BsonDocument> Execute(bool receiptOnly, int body = 1) =>
            data.Store.ExecuteCommandAsync(actor, "synthetic.receipt-only", Meta("receipt-only"), new { Id = body },
                (snapshot, tx, token) =>
                {
                    AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, Permissions.StockSourcePropose, data.Clock.UtcNow));
                    return Task.FromResult(new CommandAuthorization(new(true, "allowed", [1000]), []));
                }, (_, _) => { writes++; return Task.FromResult(new BsonDocument("ok", true)); }, default,
                (_, _, _) => { checks++; return Task.CompletedTask; }, requireReceipt: receiptOnly);
        var missing = await Assert.ThrowsAsync<PersistenceConflictException>(() => Execute(true));
        Assert.Equal("operation_receipt_unavailable", missing.Code); Assert.Equal(0, writes); Assert.Equal(0, checks);
        Assert.Equal(0, await Count(data, "idempotencyRecords"));
        var original = await Execute(false);
        Assert.Equal(original, await Execute(true)); Assert.Equal(1, writes); Assert.Equal(2, checks);
        var changed = await Assert.ThrowsAsync<PersistenceConflictException>(() => Execute(true, 2));
        Assert.Equal("idempotency_key_reused", changed.Code);
        await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 1000L),
            Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Execute(true));
        Assert.Equal(1, writes); Assert.Equal(2, checks);
    }
    private static async Task LinkPeople(StoreTestData data)
    {
        var group = PersonTestData.GroupId(1000);
        await Rows(data, "personEquivalenceGroups").UpdateOneAsync(new BsonDocument("_id", group), new BsonDocument("$set", new BsonDocument
        {
            { "memberPersonIds", new BsonArray { PersonTestData.PersonId(1000), PersonTestData.PersonId(2000) } },
            { "memberUserIds", new BsonArray { 1000L, 2000L } }
        }));
        await Rows(data, "personRecords").UpdateOneAsync(new BsonDocument("_id", PersonTestData.PersonId(2000)),
            new BsonDocument("$set", new BsonDocument("currentEquivalenceId", group)));
    }
}
