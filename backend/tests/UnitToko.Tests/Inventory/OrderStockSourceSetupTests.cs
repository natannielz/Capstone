using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class OrderStockSourceSetupTests(MongoFixture fixture)
{
    [Fact]
    public async Task Genuine_independent_setup_initializes_one_inactive_producer_and_original_key_replay_restart_adds_nothing()
    {
        await using var data = await OrderStockSourceTestData.CreateAsync(fixture);
        var approved = data.Approved!; var head = await data.HeadAsync(); var pool = await data.PoolAsync();
        var transition = Assert.Single(await data.Rows(OrderStockSourceRecords.Transitions).Find(new BsonDocument()).ToListAsync());
        Assert.Equal(approved["decisionId"], head["setupDecisionId"]); Assert.Equal(approved["poolId"], head["_id"]);
        Assert.Equal(1L, head["revision"].AsInt64); Assert.Equal(0L, head["fence"].AsInt64);
        Assert.Equal(transition["_id"], head["lastTransitionId"]); Assert.Equal(transition["transitionHash"], head["lastHash"]);
        Assert.Equal("setup", transition["sourceKind"].AsString); Assert.Equal(approved["decisionId"], transition["sourceId"]);
        Assert.True(transition["previousId"].IsBsonNull); Assert.True(transition["previousHash"].IsBsonNull); Assert.Empty(transition["effects"].AsBsonArray);
        Assert.Equal(data.Reviewer.UserId, transition["actorId"].AsInt64); Assert.False(pool["active"].AsBoolean);
        Assert.Equal(1L, pool["revision"].AsInt64); Assert.Equal(0L, pool["fence"].AsInt64);
        var before = await data.SourceStateAsync(); var effects = await data.EffectsAsync();
        var restarted = new StockSourceSetupService(data.Data.Database, new(fixture.Client), new(data.Data.Database), data.Data.Clock);
        Assert.Equal(approved, await restarted.DecideAsync(data.Reviewer, data.Proposed["proposalId"].AsInt64, 1,
            StockSourceSetupDecision.Approve, "isolated-independent-review", OrderStockSourceTestData.Meta("m90-approve")));
        Assert.Equal(approved, await restarted.RecoverAsync(data.Reviewer, OrderStockSourceTestData.Location,
            "stock.source.approve", "m90-approve", "m90-original-key-recovery"));
        await new CollectionMigrations(data.Data.Database, data.Data.Clock).ApplyAsync();
        Assert.Equal(before, await data.SourceStateAsync()); Assert.Equal(effects, await data.EffectsAsync());
        var storedRevision = (await data.HeadAsync())["revision"].AsInt64;
        foreach (var reservations in new[] { false, true }) {
            var result = await data.ReadAsync(reservations);
            Assert.True(result.Complete); Assert.Equal(0L, result.OpenCount);
            Assert.Equal(storedRevision, result.ProducerRevision);
            Assert.Equal(reservations ? StockSourceObligationDomain.Reservations : StockSourceObligationDomain.Orders, result.Domain);
        }
        Assert.Equal(2L, (await data.HeadAsync())["fence"].AsInt64);
        Assert.Equal(0L, (await data.PoolAsync())["fence"].AsInt64);
        Assert.False((await data.PoolAsync())["active"].AsBoolean); Assert.Equal(effects, await data.EffectsAsync());
        var expected = await data.ExpectedAsync();
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => new StockSourceObligationGate(data.Data.Database,
            [data.OrderReader, data.ReservationReader]).RequireDrainedAsync(tx, data.Reviewer, expected));
        Assert.Equal("stock_source_obligation_coverage_missing", error.Code); Assert.False(tx.IsInTransaction);
        Assert.Equal(2L, (await data.HeadAsync())["fence"].AsInt64);
    }

    [Fact]
    public async Task Required_approval_audit_failure_leaves_no_pool_head_transition_or_success_receipt()
    {
        await using var data = await OrderStockSourceTestData.CreateAsync(fixture, approve: false);
        var effects = await data.EffectsAsync();
        var definition = await UnitToko.Platform.Identity.AccountContactStorage.DefinitionAsync(data.Data.Database, "auditEvents", default);
        var prior = definition!["options"]["validator"].AsBsonDocument;
        await data.Data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", "auditEvents" }, { "validator", new BsonDocument("$and", new BsonArray { prior,
                new BsonDocument("operation", new BsonDocument("$ne", "stock.source.approve")) }) },
            { "validationLevel", "strict" }, { "validationAction", "error" } });
        await Assert.ThrowsAsync<MongoWriteException>(() => data.ApproveAsync());
        foreach (var name in new[] { "inventoryPools", "inventoryPoolRegistrations", "stockSourceDecisions",
            OrderStockSourceRecords.Heads, OrderStockSourceRecords.Transitions })
            Assert.Equal(0L, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "stock.source.approve")));
        Assert.Equal(1L, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(effects, await data.EffectsAsync());
    }

    [Fact]
    public async Task Factor_expiry_after_real_head_insert_aborts_all_new_owners_and_retry_preserves_gaps_without_repairing_revisions()
    {
        await using var data = await OrderStockSourceTestData.CreateAsync(fixture, approve: false);
        var effects = await data.EffectsAsync(); var originalTime = data.Data.Clock.UtcNow; var fired = 0; var captured = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "M90FinalAuthority-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(value => {
                if (value.CommandName == "insert" && value.Command.GetValue("insert", "") == OrderStockSourceRecords.Heads) captured = value.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(value => {
                if (value.RequestId == captured && Interlocked.Exchange(ref fired, 1) == 0)
                    data.Data.Clock.UtcNow = originalTime.AddMinutes(6);
            });
        };
        using var client = new MongoClient(settings); var database = client.GetDatabase(data.Data.Database.DatabaseNamespace.DatabaseName);
        var service = new StockSourceSetupService(database, new(client), new(database), data.Data.Clock);
        var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideAsync(data.Reviewer,
            data.Proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "isolated-independent-review",
            OrderStockSourceTestData.Meta("m90-approve")));
        Assert.Equal("step_up_required", error.Code); Assert.Equal(1, fired);
        foreach (var name in new[] { "inventoryPools", "inventoryPoolRegistrations", "stockSourceDecisions",
            OrderStockSourceRecords.Heads, OrderStockSourceRecords.Transitions })
            Assert.Equal(0L, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "m90-approve")));
        var allocated = await data.Rows("idCounters").Find(new BsonDocument("_id", OrderStockSourceRecords.Transitions)).SingleAsync();
        var gap = allocated["value"].AsInt64; Assert.True(gap > 0);
        data.Data.Clock.UtcNow = originalTime; var approved = await data.ApproveAsync();
        var transition = await data.Rows(OrderStockSourceRecords.Transitions).Find(new BsonDocument()).SingleAsync();
        Assert.True(transition["_id"].AsInt64 > gap); Assert.Equal(1L, transition["revision"].AsInt64);
        Assert.Equal(approved["poolId"], (await data.HeadAsync())["_id"]);
        Assert.False((await data.PoolAsync())["active"].AsBoolean); Assert.Equal(effects, await data.EffectsAsync());
    }

    [Fact]
    public async Task Deleted_genuine_head_refuses_both_readers_and_receipt_recovery_never_manufactures_a_replacement()
    {
        await using var data = await OrderStockSourceTestData.CreateAsync(fixture);
        var approved = data.Approved!; var effects = await data.EffectsAsync();
        var transition = await data.Rows(OrderStockSourceRecords.Transitions).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(1L, (await data.Rows(OrderStockSourceRecords.Heads).DeleteOneAsync(new BsonDocument("_id", data.PoolId))).DeletedCount);
        foreach (var reservations in new[] { false, true })
            Assert.Equal("order_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => data.ReadAsync(reservations))).Code);
        Assert.Equal(approved, await data.Service.RecoverAsync(data.Reviewer, OrderStockSourceTestData.Location,
            "stock.source.approve", "m90-approve", "m90-no-repair"));
        Assert.Equal(approved, await data.ApproveAsync());
        Assert.Equal(0L, await data.Rows(OrderStockSourceRecords.Heads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(transition, await data.Rows(OrderStockSourceRecords.Transitions).Find(new BsonDocument()).SingleAsync());
        Assert.False((await data.PoolAsync())["active"].AsBoolean); Assert.Equal(effects, await data.EffectsAsync());
    }

    [Theory]
    [InlineData("head-revision")] [InlineData("transition-byte")] [InlineData("approval-receipt")] [InlineData("registration")]
    public async Task Damaged_actual_producer_membership_or_provenance_is_unavailable_and_not_repaired(string fault)
    {
        await using var data = await OrderStockSourceTestData.CreateAsync(fixture);
        var effects = await data.EffectsAsync();
        if (fault == "head-revision") await data.Rows(OrderStockSourceRecords.Heads).UpdateOneAsync(new BsonDocument("_id", data.PoolId),
            Builders<BsonDocument>.Update.Set("revision", 2L));
        if (fault == "transition-byte") await data.Rows(OrderStockSourceRecords.Transitions).UpdateOneAsync(new BsonDocument("poolId", data.PoolId),
            Builders<BsonDocument>.Update.Set("sourceHash", new string('f', 64)));
        if (fault == "approval-receipt") await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument {
            { "operation", "stock.source.approve" }, { "result.decisionId", data.Approved!["decisionId"] } });
        if (fault == "registration") await data.Rows("inventoryPoolRegistrations").DeleteOneAsync(new BsonDocument("_id", data.PoolId));
        var before = await data.SourceStateAsync();
        foreach (var reservations in new[] { false, true })
            Assert.Equal("order_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => data.ReadAsync(reservations))).Code);
        Assert.Equal(before, await data.SourceStateAsync()); Assert.Equal(effects, await data.EffectsAsync());
    }

    [Fact]
    public async Task Foreign_scope_revoked_actor_and_stale_actual_pool_expectations_cannot_fence_or_read_producer_state()
    {
        await using var data = await OrderStockSourceTestData.CreateAsync(fixture); var expected = await data.ExpectedAsync();
        await data.Rows("authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel,
            100, AuthorizationStatus.Active, 1)));
        var foreignLocation = OrderStockSourceTestData.Location with { ChannelId = new(104) };
        var foreign = await data.Data.AddActorAsync(4000, foreignLocation.ToScope(), Permissions.StockSourceRead);
        var before = await data.SourceStateAsync();
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.ReadAsync(actor: foreign))).Code);
        foreach (var stale in new[] { expected with { PoolRevision = expected.PoolRevision + 1 }, expected with { PoolFence = expected.PoolFence + 1 } })
            Assert.Equal("order_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => data.ReadAsync(expected: stale))).Code);
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", data.Reviewer.UserId), Builders<BsonDocument>.Update.Inc("grantVersion", 1L));
        Assert.Equal("stale_or_inactive_actor", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.ReadAsync())).Code);
        Assert.Equal(before, await data.SourceStateAsync()); Assert.False((await data.PoolAsync())["active"].AsBoolean);
    }

    [Fact]
    public async Task Disabled_genuinely_reviewed_alias_cannot_hide_an_orphan_reservation_of_the_actual_canonical_pool()
    {
        await using var data = await OrderStockSourceTestData.CreateAsync(fixture);
        var location = OrderStockSourceTestData.Location with { ChannelId = new(104) };
        await data.Rows("authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel,
            100, AuthorizationStatus.Active, 1)));
        await data.Data.AddGrantAsync(data.Maker, 1001, location.ToScope(), [Permissions.StockSourcePropose, Permissions.StockSourceRead]);
        await data.Data.AddGrantAsync(data.Reviewer, 2001, location.ToScope(), [Permissions.StockSourceApprove, Permissions.StockSourceRead]);
        var proposed = await data.Service.ProposeAliasAsync(data.Maker, data.PoolId, 1, location, 0, true,
            OrderStockSourceTestData.Meta("m90-alias-enable"));
        await data.Service.DecideAliasAsync(data.Reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "isolated-alias-review", OrderStockSourceTestData.Meta("m90-alias-enabled"));
        proposed = await data.Service.ProposeAliasAsync(data.Maker, data.PoolId, 1, location, 1, false,
            OrderStockSourceTestData.Meta("m90-alias-disable"));
        await data.Service.DecideAliasAsync(data.Reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "isolated-alias-review", OrderStockSourceTestData.Meta("m90-alias-disabled"));
        var latest = await data.Rows("stockSourceAliases").Find(new BsonDocument("poolId", data.PoolId))
            .Sort(new BsonDocument("aliasRevision", -1)).FirstAsync();
        Assert.Equal(2L, latest["aliasRevision"].AsInt64); Assert.False(latest["enabled"].AsBoolean);
        Assert.Equal(StockSourceSetupBson.Location(location), latest["requestedLocation"]);
        Assert.False((await data.PoolAsync())["active"].AsBoolean);
        var before = await data.SourceStateAsync(); var effects = await data.EffectsAsync();
        // Deliberately corrupt isolated storage with a structurally valid orphan. This is a
        // negative membership fixture, never a legitimate admission or successful new obligation.
        var orphan = new BsonDocument { { "_id", 880001L }, { "schemaVersion", 1 }, { "kind", "ordering.reservation" },
            { "revision", 1L }, { "fence", 0L }, { "orderId", 880002L }, { "suborderId", 880003L },
            { "allocation", new BsonDocument { { "skuId", 880004L }, { "authority", new BsonDocument { { "id", 880005L }, { "revision", 1L } } },
                { "poolId", data.PoolId }, { "poolRevision", 1L }, { "balanceId", 880006L }, { "balanceRevision", 1L },
                { "definitionId", 880007L }, { "lotId", 880008L }, { "packages", 2L }, { "location", StockSourceSetupBson.Location(location) } } },
            { "remaining", 2L }, { "dispatched", 0L }, { "released", 0L } };
        await data.Rows("reservations").InsertOneAsync(orphan);
        Assert.True(await data.Rows("reservations").Find(Builders<BsonDocument>.Filter.Eq("_id", orphan["_id"]) &
            OrderSubmissionSchemas.Validator("reservations")).AnyAsync());
        foreach (var reservations in new[] { false, true })
            Assert.Equal("order_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => data.ReadAsync(reservations))).Code);
        Assert.Equal(before, await data.SourceStateAsync());
        Assert.Equal(orphan, await data.Rows("reservations").Find(new BsonDocument("_id", orphan["_id"])).SingleAsync());
        Assert.Equal(1L, (await data.Rows("reservations").DeleteOneAsync(new BsonDocument("_id", orphan["_id"]))).DeletedCount);
        Assert.Equal(effects, await data.EffectsAsync());
        foreach (var reservations in new[] { false, true }) {
            var genuineEmptyState = await data.ReadAsync(reservations);
            Assert.Equal(0L, genuineEmptyState.OpenCount); Assert.Equal(1L, genuineEmptyState.ProducerRevision);
        }
        Assert.Equal(1L, await data.Rows(OrderStockSourceRecords.Transitions).CountDocumentsAsync(new BsonDocument()));
        Assert.False((await data.PoolAsync())["active"].AsBoolean);
    }

    [Fact]
    public async Task Genuine_current_producer_snapshot_and_final_pool_CAS_conflict_with_real_source_stop_while_other_domains_remain_unavailable()
    {
        await using var data = await OrderStockSourceTestData.CreateAsync(fixture);
        var expected = await data.ExpectedAsync(); var effects = await data.EffectsAsync(); var head = await data.HeadAsync();
        var preview = await data.Service.PreviewSuspensionAsync(data.Operator, data.PoolId);
        var expectation = new StockSourceSuspensionExpectation(preview["poolRevision"].AsInt64,
            preview["suspensionRevision"].AsInt64, preview["configurationHash"].AsString);
        using var older = await fixture.Client.StartSessionAsync();
        older.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
        var capturedPool = await data.Rows("inventoryPools").Find(older, new BsonDocument("_id", data.PoolId)).SingleAsync();
        var actual = await new OrderStockSourceRecords(data.Data.Database).ReadAsync(older,
            new(data.Data.Database, data.Unit, new(data.Data.Database), data.Data.Clock), expected, default);
        Assert.Equal(head["revision"].AsInt64, actual.Head["revision"].AsInt64);
        Assert.Equal(0L, actual.OpenOrders); Assert.Equal(0L, actual.OpenReservations);
        var stop = await data.Service.SuspendAsync(data.Operator, data.PoolId, expectation, OrderStockSourceTestData.Meta("m90-real-stop"));
        // This is the exact canonical-pool conditional write used by the gate, exercised directly
        // because the other five genuine domains are unavailable. It is NOT a drain acceptance.
        try {
            var conflict = await Assert.ThrowsAnyAsync<MongoException>(() => data.Rows("inventoryPools").UpdateOneAsync(older, capturedPool,
                Builders<BsonDocument>.Update.Inc("fence", 1L)));
            Assert.True(conflict.HasErrorLabel("TransientTransactionError"));
        } finally { if (older.IsInTransaction) await older.AbortTransactionAsync(); }
        Assert.Equal(head, await data.HeadAsync()); Assert.Equal(1L, (await data.PoolAsync())["fence"].AsInt64);
        Assert.False((await data.PoolAsync())["active"].AsBoolean); Assert.Equal(effects, await data.EffectsAsync());
        Assert.Equal(stop, await data.Service.RecoverSuspensionAsync(data.Operator, data.PoolId, "m90-real-stop", "m90-stop-recovery"));
        foreach (var reservations in new[] { false, true }) {
            var fresh = await data.ReadAsync(reservations);
            Assert.Equal(head["revision"].AsInt64, fresh.ProducerRevision); Assert.Equal(0L, fresh.OpenCount);
        }
    }
}
