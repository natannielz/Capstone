using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Organizations;
using B = UnitToko.Platform.Inventory.StockSourceReconciliationBson;
using D = UnitToko.Tests.Inventory.StockSourceReconciliationTestData;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class StockSourceReconciliationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Complete_independent_observations_reconcile_once_without_stock_money_or_activation_and_discrepancies_remain_explicit()
    {
        await using var data = await D.CreateAsync(fixture);
        var preserved = await data.ProtectedAsync(); var input = await data.InputAsync();
        var baseline = await data.Service.ReadReconciliationBaselineAsync(data.Reader, data.PoolId);
        var balance = Assert.Single(baseline["balances"].AsBsonArray);
        Assert.Equal(data.Balance["_id"], balance["balanceId"]); Assert.Equal(2L, balance["onHand"].AsInt64);
        Assert.Equal(2L, balance["held"].AsInt64); Assert.Equal(0L, balance["reserved"].AsInt64);
        await DeniedAsync(() => data.Service.ProposeReconciliationAsync(data.Maker, input with { Observation = input.Observation with { Entries = [] } }, data.Binding(input), D.Meta("incomplete")));
        await DeniedAsync(() => data.Service.ProposeReconciliationAsync(data.Maker, input with { Observation = input.Observation with { Entries = [new(long.MaxValue, 2)] } }, data.Binding(input), D.Meta("foreign-balance")));

        var discrepantInput = await data.InputAsync("SYNTHETIC-DISCREPANCY", 1);
        var discrepant = await data.ProposeAsync("discrepancy", discrepantInput);
        var review = await data.Service.PreviewReconciliationDecisionAsync(data.Checker, discrepant["proposalId"].AsInt64, StockSourceReconciliationDecision.Confirm, data.Review(discrepantInput));
        Assert.False(review["canConfirm"].AsBoolean);
        await DeniedAsync(() => data.ConfirmAsync(discrepant["proposalId"].AsInt64, discrepantInput, "discrepancy-confirm"));
        var rejectionReview = await data.Service.PreviewReconciliationDecisionAsync(data.Checker, discrepant["proposalId"].AsInt64, StockSourceReconciliationDecision.Reject, null);
        var rejected = await data.Service.DecideReconciliationAsync(data.Checker, discrepant["proposalId"].AsInt64, 1,
            StockSourceReconciliationDecision.Reject, null, rejectionReview["binding"].AsString, D.Meta("discrepancy-rejected"));
        Assert.Equal("rejected", rejected["status"].AsString); Assert.Equal(preserved, await data.ProtectedAsync());

        input = await data.InputAsync(); var proposal = await data.ProposeAsync("complete", input);
        Assert.Equal(proposal, await data.ProposeAsync("complete", input));
        Assert.Equal(proposal, await data.Service.RecoverReconciliationAsync(data.Maker, B.ProposeOperation, "complete"));
        var afterProposal = await data.StateAsync();
        await DeniedAsync(() => data.Service.ProposeReconciliationAsync(data.Maker, input with { Observation = input.Observation with { SourceReference = "CHANGED-REFERENCE" } }, data.Binding(input), D.Meta("complete")));
        Assert.Equal(afterProposal, await data.StateAsync());
        Assert.Null(await data.Service.RecoverReconciliationAsync(data.Checker, B.ProposeOperation, "complete"));
        await DeniedAsync(() => data.Service.DecideReconciliationAsync(data.Checker, proposal["proposalId"].AsInt64, 1,
            StockSourceReconciliationDecision.Confirm, null, new string('0', 64), D.Meta("missing-independent-observation")));
        var different = data.Review(input) with { Entries = [new(data.Balance["_id"].AsInt64, 1)] };
        var differentReview = await data.Service.PreviewReconciliationDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, StockSourceReconciliationDecision.Confirm, different);
        Assert.False(differentReview["canConfirm"].AsBoolean);
        await DeniedAsync(() => data.Service.DecideReconciliationAsync(data.Checker, proposal["proposalId"].AsInt64, 1,
            StockSourceReconciliationDecision.Confirm, different, differentReview["binding"].AsString, D.Meta("independent-discrepancy")));
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(10);
        var independent = data.Review(input); Assert.NotEqual(input.Observation.SourceReference, independent.SourceReference);
        var confirmReview = await data.Service.PreviewReconciliationDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, StockSourceReconciliationDecision.Confirm, independent);
        var result = await data.Service.DecideReconciliationAsync(data.Checker, proposal["proposalId"].AsInt64, 1,
            StockSourceReconciliationDecision.Confirm, independent, confirmReview["binding"].AsString, D.Meta("complete-confirmed"));
        Assert.Equal("reconciled", result["status"].AsString);
        Assert.Equal(result, await data.Service.DecideReconciliationAsync(data.Checker, proposal["proposalId"].AsInt64, 1,
            StockSourceReconciliationDecision.Confirm, independent, confirmReview["binding"].AsString, D.Meta("complete-confirmed")));
        Assert.Equal(result, await data.Service.RecoverReconciliationAsync(data.Checker, B.DecideOperation, "complete-confirmed"));
        var read = Assert.IsType<BsonDocument>(await data.Service.ReadReconciliationAsync(data.Reader, proposal["proposalId"].AsInt64));
        Assert.Equal(proposal["proposalId"], read["proposal"]["id"]); Assert.Equal(result["decisionId"], read["decision"]["id"]);
        Assert.Equal(preserved, await data.ProtectedAsync());
        var poolFilter = new BsonDocument("_id", data.PoolId);
        var originalPool = await data.Rows("inventoryPools").Find(poolFilter).SingleAsync();
        try {
            // Explicit fixture-only ineligibility must not hide named discovery or immutable history.
            Assert.Equal(1L, (await data.Rows("inventoryPools").UpdateOneAsync(poolFilter,
                Builders<BsonDocument>.Update.Set("active", true))).ModifiedCount);
            var location = baseline["ownerLocation"].AsBsonDocument;
            var named = await data.Service.ReconciliationTargetsAsync(data.Reader, new StockLocation(
                new(location["organizationId"].AsInt64), new(location["shopId"].AsInt64),
                new(location["warehouseId"].AsInt64), new(location["channelId"].AsInt64)), 0, 25);
            Assert.Contains(named.Items, item => item["poolId"].AsInt64 == data.PoolId);
            Assert.Equal(read, await data.Service.ReadReconciliationAsync(data.Reader, proposal["proposalId"].AsInt64));
            Assert.Equal(2, (await data.Service.ListReconciliationsAsync(data.Reader, data.PoolId, 0, 25)).Items.Count);
            await DeniedAsync(() => data.Service.ReadReconciliationBaselineAsync(data.Reader, data.PoolId));
        } finally { await data.Rows("inventoryPools").ReplaceOneAsync(poolFilter, originalPool); }
        Assert.Equal(originalPool, await data.Rows("inventoryPools").Find(poolFilter).SingleAsync());
        Assert.False((await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync())["active"].AsBoolean);
        Assert.Equal(2L, await data.Rows(B.Proposals).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument("decision", "confirm")));
        foreach (var (table, filter) in new[] {
            ("auditEvents", new BsonDocument { { "operation", B.DecideOperation }, { "record._id", result["decisionId"] } }),
            ("outboxEvents", new BsonDocument { { "kind", B.DecideOperation }, { "requestKey", "complete-confirmed" } }),
            ("idempotencyRecords", new BsonDocument { { "operation", B.DecideOperation }, { "key", "complete-confirmed" } }) })
            Assert.Equal(1L, await data.Rows(table).CountDocumentsAsync(filter));
        var aging = await data.InputAsync("ORIGINAL-OBSERVATION-AGE"); var agingProposal = await data.ProposeAsync("aging-proposal", aging);
        data.Clock.UtcNow = data.Clock.UtcNow.AddMilliseconds(60001);
        var beforeExpiredReview = await data.StateAsync();
        // A newly entered independent count cannot rejuvenate the maker's original source observation.
        Assert.True(data.Review(aging).ObservedAt > aging.Observation.ObservedAt);
        await DeniedAsync(() => data.ConfirmAsync(agingProposal["proposalId"].AsInt64, aging, "expired-observation-confirm"));
        Assert.Equal(beforeExpiredReview, await data.StateAsync()); Assert.Equal(preserved, await data.ProtectedAsync());
    }

    [WindowsStorageFact]
    public async Task Failed_final_write_rolls_back_and_competing_reviews_or_a_real_new_receipt_cannot_confirm_a_stale_pool()
    {
        await using var data = await D.CreateAsync(fixture); var input = await data.InputAsync();
        var proposal = await data.ProposeAsync("rollback-proposal", input); var id = proposal["proposalId"].AsInt64;
        var before = await data.StateAsync(); var protectedBefore = await data.ProtectedAsync();
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "outboxEvents") })).SingleAsync();
        var validator = definition["options"]["validator"].DeepClone().AsBsonDocument;
        try {
            await GuardAsync(data, new("$and", new BsonArray { validator, new BsonDocument("kind", new BsonDocument("$ne", B.DecideOperation)) }));
            Assert.IsAssignableFrom<MongoException>(await Record.ExceptionAsync(() => data.ConfirmAsync(id, input, "rollback-confirm")));
            Assert.Equal(before, await data.StateAsync()); Assert.Equal(protectedBefore, await data.ProtectedAsync());
            Assert.Null(await data.Service.RecoverReconciliationAsync(data.Checker, B.DecideOperation, "rollback-confirm"));
        } finally { await GuardAsync(data, validator); }
        var results = await Task.WhenAll(Record.ExceptionAsync(() => data.ConfirmAsync(id, input, "competing-a")),
            Record.ExceptionAsync(() => data.ConfirmAsync(id, input, "competing-b")));
        Assert.Single(results, error => error is null); Assert.IsType<PersistenceConflictException>(Assert.Single(results, error => error is not null));
        Assert.Equal(1L, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument("proposalId", id)));
        Assert.Equal(protectedBefore, await data.ProtectedAsync());

        var staleInput = await data.InputAsync("BEFORE-SECOND-GENUINE-RECEIPT");
        var stale = await data.ProposeAsync("stale-proposal", staleInput);
        await data.Stock.RecordAsync("new-real-held-receipt", data.Stock.Input("SYNTHETIC-NEW-RECEIPT", 1));
        var afterReceipt = await data.ProtectedAsync(); var commandState = await data.StateAsync();
        await DeniedAsync(() => data.ConfirmAsync(stale["proposalId"].AsInt64, staleInput, "stale-confirm"));
        Assert.Equal(commandState, await data.StateAsync()); Assert.Equal(afterReceipt, await data.ProtectedAsync());
        var refreshed = await data.InputAsync("COMPLETE-REFRESHED-COVERAGE"); Assert.Equal(2, refreshed.Observation.Entries.Length);
        Assert.Equal(3L, refreshed.Observation.Entries.Sum(row => row.Packages));
        var fresh = await data.ProposeAsync("fresh-proposal", refreshed);
        Assert.Equal("reconciled", (await data.ConfirmAsync(fresh["proposalId"].AsInt64, refreshed, "fresh-confirm"))["status"].AsString);
        Assert.Equal(afterReceipt, await data.ProtectedAsync());
    }

    [WindowsStorageFact]
    public async Task Missing_original_owners_effects_or_paired_receipt_and_balance_never_become_an_empty_reconciled_pool()
    {
        await using var data = await D.CreateAsync(fixture); var input = await data.InputAsync();
        var proposal = await data.ProposeAsync("integrity-proposal", input); var id = proposal["proposalId"].AsInt64;
        var receipt = await data.Rows("procurementStockReceipts").Find(new BsonDocument("_id", data.Receipt["receiptId"])).SingleAsync();
        foreach (var (table, filter) in new[] {
            ("procurementStockReceipts", new BsonDocument("_id", receipt["_id"])),
            ("stockBalances", new BsonDocument("_id", data.Balance["_id"])),
            ("movements", new BsonDocument("_id", data.Receipt["movementId"])),
            ("auditEvents", new BsonDocument("_id", receipt["auditId"])),
            ("procurementQuarantineDispositionHeads", new BsonDocument("_id", receipt["disposition"]["headId"])) }) {
            var original = await data.Rows(table).Find(filter).SingleAsync();
            Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", original["_id"]))).DeletedCount);
            try { await RefuseMissingCoverageAsync(data, input, id, table + " " + filter.ToJson()); }
            finally { await data.Rows(table).InsertOneAsync(original); }
        }
        Assert.Equal(1L, (await data.Rows("procurementStockReceipts").DeleteOneAsync(new BsonDocument("_id", receipt["_id"]))).DeletedCount);
        Assert.Equal(1L, (await data.Rows("stockBalances").DeleteOneAsync(new BsonDocument("_id", data.Balance["_id"]))).DeletedCount);
        try {
            Assert.True(await data.Rows("lots").Find(new BsonDocument("_id", data.Receipt["lotId"])).AnyAsync());
            Assert.True(await data.Rows("movements").Find(new BsonDocument("_id", data.Receipt["movementId"])).AnyAsync());
            await RefuseMissingCoverageAsync(data, input, id, "paired procurementStockReceipts + stockBalances deletion");
        } finally { await data.Rows("procurementStockReceipts").InsertOneAsync(receipt); await data.Rows("stockBalances").InsertOneAsync(data.Balance); }
        var accepted = await data.ConfirmAsync(id, input, "integrity-confirm");
        var decision = await data.Rows(B.Decisions).Find(new BsonDocument("_id", accepted["decisionId"])).SingleAsync();
        foreach (var (table, filter) in new[] {
            (B.Proposals, new BsonDocument("_id", id)), (B.Decisions, new BsonDocument("_id", accepted["decisionId"])),
            ("auditEvents", new BsonDocument("_id", decision["auditId"])),
            ("outboxEvents", new BsonDocument { { "kind", B.DecideOperation }, { "requestKey", "integrity-confirm" } }),
            ("idempotencyRecords", new BsonDocument { { "operation", B.DecideOperation }, { "key", "integrity-confirm" } }) }) {
            var original = await data.Rows(table).Find(filter).SingleAsync(); await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", original["_id"]));
            try {
                var state = await data.StateAsync(); var held = await data.ProtectedAsync();
                if (table == B.Proposals) Assert.Null(await data.Service.ReadReconciliationAsync(data.Reader, id));
                else await DeniedAsync(() => data.Service.ReadReconciliationAsync(data.Reader, id));
                await DeniedAsync(() => data.Service.RecoverReconciliationAsync(data.Checker, B.DecideOperation, "integrity-confirm"));
                Assert.Equal(state, await data.StateAsync()); Assert.Equal(held, await data.ProtectedAsync());
            } finally { await data.Rows(table).InsertOneAsync(original); }
        }
        Assert.Equal(accepted, await data.Service.RecoverReconciliationAsync(data.Checker, B.DecideOperation, "integrity-confirm"));
    }

    [WindowsStorageFact]
    public async Task Every_mapped_channel_and_current_original_person_grant_and_fresh_factor_are_required_for_review()
    {
        await using var data = await D.CreateAsync(fixture); var input = await data.InputAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadReconciliationBaselineAsync(data.Outsider, data.PoolId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ProposeReconciliationAsync(data.Reader, input, data.Binding(input), D.Meta("read-only")));
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ProposeReconciliationAsync(
            data.Maker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, input, data.Binding(input), D.Meta("aged-factor")))).Code);
        var linked = await data.AddReviewedChannelAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadReconciliationBaselineAsync(data.Maker, data.PoolId));
        await data.Base.AddGrantAsync(data.Maker, 7442, linked.ToScope(), [Permissions.StockRead, Permissions.StockSourceRead, D.ReadPermission, D.ProposePermission]);
        await data.Base.AddGrantAsync(data.Checker, 7443, linked.ToScope(), [Permissions.StockRead, Permissions.StockSourceRead, D.ReadPermission, D.DecidePermission]);
        input = await data.InputAsync(); var proposal = await data.ProposeAsync("all-channels", input); var id = proposal["proposalId"].AsInt64;
        var state = await data.StateAsync();
        foreach (var (table, filter, change) in new[] {
            ("identities", new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Set("status", "suspended")),
            ("authorizationGrants", new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked")),
            ("personAccountBindings", new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked")),
            ("authorizationGrants", new BsonDocument("_id", 7443L), Builders<BsonDocument>.Update.Set("status", "revoked")) }) {
            var original = await data.Rows(table).Find(filter).SingleAsync();
            Assert.Equal(1L, (await data.Rows(table).UpdateOneAsync(filter, change)).ModifiedCount);
            try { await DeniedAsync(() => data.ConfirmAsync(id, input, "withdrawn-authority"), table + " " + filter.ToJson()); Assert.Equal(state, await data.StateAsync()); }
            finally { await data.Rows(table).ReplaceOneAsync(filter, original); }
        }
        await OrganizationAddressTests.MergePeopleAsync(data.Stock.Inspection.Receiving.Procurement.Organizations, data.Maker.UserId, data.Checker.UserId);
        Assert.Equal("independent_person_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.ConfirmAsync(id, input, "same-person"))).Code);
        Assert.Equal(state, await data.StateAsync());
    }

    [WindowsStorageFact]
    public async Task Observation_and_factor_deadlines_crossed_after_real_outbox_write_roll_back_but_expired_original_recovery_remains_historical()
    {
        await using var data = await D.CreateAsync(fixture); var input = await data.InputAsync();
        var at = data.Clock.UtcNow; var before = await data.StateAsync(); var held = await data.ProtectedAsync();
        await CrossDeadlineAsync(B.ProposeOperation, TimeSpan.FromMilliseconds(60001), service =>
            service.ProposeReconciliationAsync(data.Maker, input, data.Binding(input), D.Meta("late-proposal")));
        Assert.Equal(before, await data.StateAsync()); Assert.Equal(held, await data.ProtectedAsync()); data.Clock.UtcNow = at;
        var proposal = await data.ProposeAsync("deadline-proposal", input); var id = proposal["proposalId"].AsInt64;
        var observation = data.Review(input); var review = await data.Service.PreviewReconciliationDecisionAsync(data.Checker, id, StockSourceReconciliationDecision.Confirm, observation);
        before = await data.StateAsync();
        foreach (var elapsed in new[] { TimeSpan.FromMilliseconds(60001), TimeSpan.FromMinutes(6) }) {
            await CrossDeadlineAsync(B.DecideOperation, elapsed, service => service.DecideReconciliationAsync(data.Checker, id, 1,
                StockSourceReconciliationDecision.Confirm, observation, review["binding"].AsString, D.Meta("late-confirm")));
            Assert.Equal(before, await data.StateAsync()); Assert.Equal(held, await data.ProtectedAsync()); data.Clock.UtcNow = at;
        }
        var accepted = await data.Service.DecideReconciliationAsync(data.Checker, id, 1, StockSourceReconciliationDecision.Confirm,
            observation, review["binding"].AsString, D.Meta("deadline-confirmed"));
        data.Clock.UtcNow = at.AddMilliseconds(60001);
        Assert.Equal(accepted, await data.Service.RecoverReconciliationAsync(data.Checker, B.DecideOperation, "deadline-confirmed"));
        Assert.Equal(held, await data.ProtectedAsync());

        async Task CrossDeadlineAsync(string operation, TimeSpan elapsed, Func<StockSourceSetupService, Task> action) {
            var requestId = 0; var changed = 0;
            var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
            settings.ClusterConfigurator = builder => {
                builder.Subscribe<CommandStartedEvent>(value => {
                    if (requestId != 0 || value.DatabaseNamespace.DatabaseName != data.Database.DatabaseNamespace.DatabaseName ||
                        value.Command.GetValue("insert", BsonNull.Value) != "outboxEvents") return;
                    if (value.Command["documents"].AsBsonArray.Any(row => row.AsBsonDocument.GetValue("kind", BsonNull.Value) == operation)) requestId = value.RequestId;
                });
                builder.Subscribe<CommandSucceededEvent>(value => {
                    if (requestId != 0 && value.RequestId == requestId && Interlocked.CompareExchange(ref changed, 1, 0) == 0)
                        data.Clock.UtcNow = at.Add(elapsed);
                });
            };
            var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
            await DeniedAsync(() => action(new StockSourceSetupService(db, new MongoUnitOfWork(client), new(db), data.Clock)));
            Assert.Equal(1, changed);
        }
    }

    private static async Task RefuseMissingCoverageAsync(D data, StockSourceReconciliationInput input, long proposalId, string context)
    {
        var state = await data.StateAsync(); var held = await data.ProtectedAsync();
        await DeniedAsync(() => data.Service.ReadReconciliationBaselineAsync(data.Reader, data.PoolId), context + " baseline");
        await DeniedAsync(() => data.Service.ProposeReconciliationAsync(data.Maker, input, data.Binding(input), D.Meta("missing-coverage")), context + " proposal");
        await DeniedAsync(() => data.ConfirmAsync(proposalId, input, "missing-coverage-confirm"), context + " confirmation");
        Assert.Equal(state, await data.StateAsync()); Assert.Equal(held, await data.ProtectedAsync());
    }
    private static async Task DeniedAsync(Func<Task> action, string? context = null)
    {
        var error = await Record.ExceptionAsync(action);
        Assert.True(error is DomainValidationException or AuthorizationDeniedException or PersistenceConflictException,
            (context is null ? "" : context + ": ") + (error?.ToString() ?? "Unexpected success"));
    }
    private static Task GuardAsync(D data, BsonDocument validator) => data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
        { "collMod", "outboxEvents" }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
}
