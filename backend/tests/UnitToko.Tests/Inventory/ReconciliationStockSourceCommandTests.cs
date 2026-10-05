using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Inventory.StockSourceReconciliationBson;
using D = UnitToko.Tests.Inventory.ReconciliationStockSourceTestData;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class ReconciliationStockSourceCommandTests(MongoFixture fixture)
{
    [Fact]
    public async Task Genuine_setup_proposal_and_independent_confirmation_reconstruct_durable_history_and_replay_without_a_second_source_effect()
    {
        await using var data = await D.CreateAsync(fixture);
        var physical = await data.StockAndMoneyAsync();
        var initial = await data.HeadAsync();
        Assert.Equal(data.Approval["decisionId"], initial["setupDecisionId"]);
        Assert.Equal(1L, initial["revision"].AsInt64);
        var setup = Assert.Single(await HistoryAsync(data));
        Assert.Equal("setup", setup["sourceKind"].AsString);
        Assert.Equal(data.Approval["decisionId"], setup["sourceId"]);
        Assert.True(setup["previousId"].IsBsonNull);
        Assert.True(setup["beforeOpen"].IsBsonNull);
        Assert.True(setup["afterOpen"].IsBsonNull);
        await RequireReadAsync(data, 0);

        var (input, binding) = await data.InputAsync("SYNTHETIC-EMPTY-POOL-COUNT");
        var beforeHead = await data.HeadAsync(); var beforePool = await data.PoolAsync();
        var proposed = await data.Service.ProposeReconciliationAsync(data.Maker, input, binding, D.Meta("m91-proposal"));
        await RequireTransitionAsync(data, beforeHead, beforePool, "proposal", proposed["proposalId"].AsInt64,
            proposed["proposalId"].AsInt64, BsonNull.Value, BsonBoolean.True, data.Maker.UserId);
        await RequireReadAsync(data, 1);
        var review = new StockSourceReconciliationObservation(data.Clock.UtcNow, "SYNTHETIC-INDEPENDENT-EMPTY-COUNT", []);
        var preview = await data.Service.PreviewReconciliationDecisionAsync(data.Checker, proposed["proposalId"].AsInt64,
            StockSourceReconciliationDecision.Confirm, review);
        Assert.True(preview["canConfirm"].AsBoolean);
        beforeHead = await data.HeadAsync(); beforePool = await data.PoolAsync();
        var result = await data.Service.DecideReconciliationAsync(data.Checker, proposed["proposalId"].AsInt64, 1,
            StockSourceReconciliationDecision.Confirm, review, preview["binding"].AsString, D.Meta("m91-confirm"));
        Assert.Equal("reconciled", result["status"].AsString);
        await RequireTransitionAsync(data, beforeHead, beforePool, "decision", result["decisionId"].AsInt64,
            proposed["proposalId"].AsInt64, BsonBoolean.True, BsonBoolean.False, data.Checker.UserId);
        await RequireReadAsync(data, 0);
        var history = await HistoryAsync(data);
        Assert.Equal(new[] { "setup", "proposal", "decision" }, history.Select(row => row["sourceKind"].AsString));
        var read = Assert.IsType<BsonDocument>(await data.Service.ReadReconciliationAsync(data.Reader, proposed["proposalId"].AsInt64));
        Assert.Equal(result["decisionId"], read["decision"]["id"]);
        var committed = await data.EffectsAsync();
        var restarted = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        Assert.Equal(proposed, await restarted.ProposeReconciliationAsync(data.Maker, input, binding, D.Meta("m91-proposal")));
        Assert.Equal(result, await restarted.DecideReconciliationAsync(data.Checker, proposed["proposalId"].AsInt64, 1,
            StockSourceReconciliationDecision.Confirm, review, preview["binding"].AsString, D.Meta("m91-confirm")));
        Assert.Equal(proposed, await restarted.RecoverReconciliationAsync(data.Maker, B.ProposeOperation, "m91-proposal"));
        Assert.Equal(result, await restarted.RecoverReconciliationAsync(data.Checker, B.DecideOperation, "m91-confirm"));
        Assert.Equal(read, await restarted.ReadReconciliationAsync(data.Reader, proposed["proposalId"].AsInt64));
        Assert.Equal(committed, await data.EffectsAsync());
        Assert.Equal(physical, await data.StockAndMoneyAsync());
        Assert.False((await data.PoolAsync())["active"].AsBoolean);
    }

    [Fact]
    public async Task Rejection_of_an_older_observation_closes_only_its_actual_obligation_and_conflicts_with_an_earlier_canonical_pool_snapshot()
    {
        await using var data = await D.CreateAsync(fixture);
        var physical = await data.StockAndMoneyAsync();
        var (firstInput, firstBinding) = await data.InputAsync("SYNTHETIC-FIRST-COUNT");
        var first = await data.Service.ProposeReconciliationAsync(data.Maker, firstInput, firstBinding, D.Meta("m91-first"));
        var (secondInput, secondBinding) = await data.InputAsync("SYNTHETIC-SECOND-COUNT");
        var second = await data.Service.ProposeReconciliationAsync(data.Maker, secondInput, secondBinding, D.Meta("m91-second"));
        await RequireReadAsync(data, 2);
        data.Clock.UtcNow = data.Clock.UtcNow.AddMilliseconds(60002);
        var firstId = first["proposalId"].AsInt64;
        var preview = await data.Service.PreviewReconciliationDecisionAsync(data.Checker, firstId,
            StockSourceReconciliationDecision.Reject, null);
        Assert.True(preview["baseline"].IsBsonNull);
        var head = await data.HeadAsync(); var pool = await data.PoolAsync();
        Assert.True(pool["fence"].AsInt64 > firstInput.PoolFence + 1);
        using var older = await fixture.Client.StartSessionAsync();
        older.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        try {
            var captured = await data.Rows("inventoryPools").Find(older, new BsonDocument("_id", data.PoolId)).SingleAsync();
            Assert.Equal(pool, captured);
            var actual = await new ReconciliationStockSourceRecords(data.Database).ReadAsync(older, data.Service,
                await data.ExpectedAsync(), default);
            Assert.Equal(2L, actual.OpenCount);
            var result = await data.Service.DecideReconciliationAsync(data.Checker, firstId, 1,
                StockSourceReconciliationDecision.Reject, null, preview["binding"].AsString, D.Meta("m91-old-reject"));
            Assert.Equal("rejected", result["status"].AsString);
            await RequireTransitionAsync(data, head, pool, "decision", result["decisionId"].AsInt64,
                firstId, BsonBoolean.True, BsonBoolean.False, data.Checker.UserId);
            var conflict = await Assert.ThrowsAnyAsync<MongoException>(() => data.Rows("inventoryPools").UpdateOneAsync(older,
                captured, Builders<BsonDocument>.Update.Inc("fence", 1L)));
            Assert.True(conflict.HasErrorLabel("TransientTransactionError"));
        } finally { await InventoryRepository.AbortAsync(older); }
        await RequireReadAsync(data, 1);
        var secondPreview = await data.Service.PreviewReconciliationDecisionAsync(data.Checker, second["proposalId"].AsInt64,
            StockSourceReconciliationDecision.Reject, null);
        await data.Service.DecideReconciliationAsync(data.Checker, second["proposalId"].AsInt64, 1,
            StockSourceReconciliationDecision.Reject, null, secondPreview["binding"].AsString, D.Meta("m91-second-reject"));
        await RequireReadAsync(data, 0);
        Assert.Equal(5, (await HistoryAsync(data)).Length);
        Assert.Equal(physical, await data.StockAndMoneyAsync());
        Assert.False((await data.PoolAsync())["active"].AsBoolean);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("corrupt")]
    public async Task A_removed_or_corrupt_actual_head_refuses_the_whole_new_command_and_reader_without_repair(string fault)
    {
        await using var data = await D.CreateAsync(fixture);
        var physical = await data.StockAndMoneyAsync();
        var (input, binding) = await data.InputAsync("SYNTHETIC-HEAD-REFUSAL");
        var head = await data.HeadAsync();
        if (fault == "removed")
            Assert.Equal(1L, (await data.Rows(ReconciliationStockSourceRecords.Heads).DeleteOneAsync(head)).DeletedCount);
        else
            Assert.Equal(1L, (await data.Rows(ReconciliationStockSourceRecords.Heads).UpdateOneAsync(head,
                Builders<BsonDocument>.Update.Set("lastHash", head["lastHash"].AsString == new string('f', 64)
                    ? new string('e', 64) : new string('f', 64)))).ModifiedCount);
        var damaged = await data.EffectsAsync();
        Assert.Equal("stock_source_reconciliation_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.ProposeReconciliationAsync(data.Maker, input, binding, D.Meta("m91-damaged-head")))).Code);
        Assert.Equal("stock_source_reconciliation_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.ReadAsync())).Code);
        Assert.Equal(damaged, await data.EffectsAsync());
        Assert.Equal(physical, await data.StockAndMoneyAsync());
        if (fault == "removed")
            Assert.Equal(0L, await data.Rows(ReconciliationStockSourceRecords.Heads).CountDocumentsAsync(new BsonDocument("_id", data.PoolId)));
        Assert.Equal(0L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "m91-damaged-head")));
    }

    [Fact]
    public async Task Foreign_stale_and_late_expired_authority_cannot_publish_a_transition_or_advance_the_pool()
    {
        await using var data = await D.CreateAsync(fixture);
        var expected = await data.ExpectedAsync(); var before = await data.EffectsAsync();
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.ReadAsync(data.Outsider))).Code);
        Assert.Equal("stale_or_inactive_actor", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.ReadAsync(data.Reader with { GrantVersion = data.Reader.GrantVersion + 1 }))).Code);
        Assert.Equal("stock_source_reconciliation_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.ReadAsync(expected: expected with { PoolFence = expected.PoolFence + 1 }))).Code);
        Assert.Equal(before, await data.EffectsAsync());
        var (input, binding) = await data.InputAsync("SYNTHETIC-LATE-AUTHORITY");
        before = await data.EffectsAsync(); var physical = await data.StockAndMoneyAsync();
        var at = data.Clock.UtcNow; var request = 0; var expired = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "M91FinalAuthority-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(value => {
                if (value.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName && request == 0 &&
                    value.Command.GetValue("insert", BsonNull.Value) == ReconciliationStockSourceRecords.Transitions &&
                    value.Command["documents"].AsBsonArray.Any(row => row["sourceKind"] == "proposal")) request = value.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(value => {
                if (request != 0 && value.RequestId == request && Interlocked.CompareExchange(ref expired, 1, 0) == 0)
                    data.Clock.UtcNow = at.AddMinutes(6);
            });
        };
        using var client = new MongoClient(settings);
        var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new StockSourceSetupService(database, new(client), new(database), data.Clock);
        try {
            Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
                service.ProposeReconciliationAsync(data.Maker, input, binding, D.Meta("m91-final-expiry")))).Code);
            Assert.Equal(1, expired);
            Assert.Equal(before, await data.EffectsAsync());
            Assert.Equal(physical, await data.StockAndMoneyAsync());
            Assert.Equal(0L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "m91-final-expiry")));
        } finally { data.Clock.UtcNow = at; }
    }

    [Fact]
    public async Task A_regressed_current_pool_or_extra_receipt_linked_to_real_history_is_unavailable_without_repair_or_read_effects()
    {
        await using var data = await D.CreateAsync(fixture);
        var physical = await data.StockAndMoneyAsync();
        var (input, binding) = await data.InputAsync("SYNTHETIC-REVERSE-MEMBERSHIP");
        var proposal = await data.Service.ProposeReconciliationAsync(data.Maker, input, binding, D.Meta("m91-integrity-proposal"));
        var proposalId = proposal["proposalId"].AsInt64;
        var preview = await data.Service.PreviewReconciliationDecisionAsync(data.Checker, proposalId,
            StockSourceReconciliationDecision.Reject, null);
        var decided = await data.Service.DecideReconciliationAsync(data.Checker, proposalId, 1,
            StockSourceReconciliationDecision.Reject, null, preview["binding"].AsString, D.Meta("m91-integrity-reject"));
        var head = await data.HeadAsync(); var pool = await data.PoolAsync();
        var latest = await data.Rows(ReconciliationStockSourceRecords.Transitions)
            .Find(new BsonDocument("_id", head["lastTransitionId"])).SingleAsync();
        Assert.Equal(decided["decisionId"], latest["sourceId"]);
        Assert.True(latest["afterPoolFence"].AsInt64 > 0);
        var clean = await data.EffectsAsync();
        Assert.Equal(1L, (await data.Rows("inventoryPools").UpdateOneAsync(pool,
            Builders<BsonDocument>.Update.Set("fence", latest["afterPoolFence"].AsInt64 - 1))).ModifiedCount);
        try {
            var regressed = await data.EffectsAsync();
            Assert.Equal("stock_source_reconciliation_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.ReadAsync())).Code);
            Assert.Equal(regressed, await data.EffectsAsync());
            Assert.Equal(head, await data.HeadAsync());
        } finally {
            Assert.Equal(1L, (await data.Rows("inventoryPools").ReplaceOneAsync(new BsonDocument("_id", data.PoolId), pool)).ModifiedCount);
        }
        Assert.Equal(clean, await data.EffectsAsync());
        var actualReceipt = await data.Rows("idempotencyRecords").Find(new BsonDocument {
            { "actorId", data.Checker.UserId }, { "operation", B.DecideOperation }, { "key", "m91-integrity-reject" } }).SingleAsync();
        Assert.Equal(decided, actualReceipt["result"].AsBsonDocument);
        foreach (var corruption in new[] { "wrong", "missing" }) {
            // Negative isolated corruption only: this is an extra receipt copied from a real
            // decision, retaining its actual proposal/decision links but lying about the pool.
            // It is never a legitimate command, approval or positive producer proof.
            var extra = actualReceipt.DeepClone().AsBsonDocument;
            extra["_id"] = ObjectId.GenerateNewId(); extra["key"] = "m91-negative-extra-" + corruption;
            if (corruption == "wrong") extra["result"]["poolId"] = data.PoolId + 1;
            else extra["result"].AsBsonDocument.Remove("poolId");
            await data.Rows("idempotencyRecords").InsertOneAsync(extra);
            try {
                var corrupt = await data.EffectsAsync();
                Assert.Equal("stock_source_reconciliation_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.ReadAsync())).Code);
                Assert.Equal(corrupt, await data.EffectsAsync());
                Assert.Equal(head, await data.HeadAsync());
            } finally {
                Assert.Equal(1L, (await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("_id", extra["_id"]))).DeletedCount);
            }
            Assert.Equal(clean, await data.EffectsAsync());
        }
        await RequireReadAsync(data, 0);
        Assert.Equal(physical, await data.StockAndMoneyAsync());
        Assert.False((await data.PoolAsync())["active"].AsBoolean);
    }

    private static async Task<BsonDocument[]> HistoryAsync(D data) => await data.Unit.ExecuteAsync(async (tx, ct) => {
        var actual = await new ReconciliationStockSourceRecords(data.Database).HistoryAsync(tx, data.Service, data.PoolId, ct);
        Assert.Equal(actual.Head["revision"].AsInt64, actual.History.LongLength);
        return actual.History;
    });
    private static async Task RequireReadAsync(D data, long count)
    {
        var head = await data.HeadAsync(); var pool = await data.PoolAsync();
        var result = await data.ReadAsync();
        Assert.True(result.Complete); Assert.Equal(StockSourceObligationDomain.SourceReconciliation, result.Domain);
        Assert.Equal(count, result.OpenCount); Assert.Equal(head["revision"].AsInt64, result.ProducerRevision);
        Assert.Equal(new StockSourceObligationExpectation(data.PoolId, D.Location,
            pool["revision"].AsInt64, pool["fence"].AsInt64), result.Pool);
        Assert.Equal(head["fence"].AsInt64 + 1, (await data.HeadAsync())["fence"].AsInt64);
        Assert.Equal(pool, await data.PoolAsync());
    }
    private static async Task RequireTransitionAsync(D data, BsonDocument beforeHead, BsonDocument beforePool,
        string kind, long sourceId, long proposalId, BsonValue beforeOpen, BsonValue afterOpen, long actorId)
    {
        var afterHead = await data.HeadAsync(); var afterPool = await data.PoolAsync();
        Assert.Equal(beforeHead["revision"].AsInt64 + 1, afterHead["revision"].AsInt64);
        Assert.Equal(beforeHead["fence"].AsInt64 + 1, afterHead["fence"].AsInt64);
        var expectedPool = beforePool.DeepClone().AsBsonDocument;
        expectedPool["fence"] = beforePool["fence"].AsInt64 + 1;
        Assert.Equal(expectedPool, afterPool);
        var row = await data.Rows(ReconciliationStockSourceRecords.Transitions).Find(new BsonDocument("_id", afterHead["lastTransitionId"])).SingleAsync();
        Assert.Equal(kind, row["sourceKind"].AsString); Assert.Equal(sourceId, row["sourceId"].AsInt64);
        Assert.Equal(proposalId, row["proposalId"].AsInt64); Assert.Equal(actorId, row["actorId"].AsInt64);
        Assert.Equal(beforeHead["lastTransitionId"], row["previousId"]); Assert.Equal(beforeHead["lastHash"], row["previousHash"]);
        Assert.Equal(beforePool["fence"], row["beforePoolFence"]); Assert.Equal(afterPool["fence"], row["afterPoolFence"]);
        Assert.Equal(beforePool["revision"], row["poolRevision"]);
        Assert.Equal(beforeOpen, row["beforeOpen"]); Assert.Equal(afterOpen, row["afterOpen"]);
        Assert.Equal(row["transitionHash"], afterHead["lastHash"]);
        await HistoryAsync(data);
    }
}
