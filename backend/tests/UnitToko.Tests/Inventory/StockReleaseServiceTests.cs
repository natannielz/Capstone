using System.Globalization;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Procurement;
using B = UnitToko.Platform.Inventory.StockReleaseBson;
using Q = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;
using Xunit.Abstractions;

namespace UnitToko.Tests.Inventory;

/// <summary>Genuine empty activation before demand, private inspection, posted clean
/// valuation and explicit independent quality. Every operator/policy input is synthetic.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class StockReleaseServiceTests(MongoFixture fixture, ITestOutputHelper output)
{
    [WindowsStorageFact]
    public async Task Actual_independent_release_transfers_selected_finite_value_once_with_durable_history_and_original_key_recovery() {
        using var timing = new StockReleaseTestDiagnostics(output);
        await using var data = await Data.CreateAsync(fixture, timing.Stage);
        var accounting = await data.Valuation.StateAsync();
        var documents = await data.Valuation.Documents.StateAsync();
        timing.Restart(); var preview = await data.Service.PreviewAsync(data.Maker, data.Input()); timing.Stage("source-read");
        Assert.Equal(0L, preview["expected"]["heldValueRevision"].AsInt64);
        Assert.Equal(0L, preview["expected"]["lotValueRevision"].AsInt64);
        Assert.Equal("explicit-clean-release-join", preview["source"]["qualityJoin"].AsString);
        var proposed = await data.Service.ProposeAsync(data.Maker, data.Input(), B.Expected(preview["expected"]), preview["binding"].AsString, Data.Meta("release-original"));
        var id = proposed["proposalId"].AsInt64;
        Assert.Equal((1L, 1L), await data.ObligationsAsync());
        Assert.Equal(3L, (await data.BalanceAsync())["held"].AsInt64);
        Assert.Equal("independent_person_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.PreviewDecisionAsync(data.Maker, id, StockReleaseDecision.Approve, LotValuePolicy.ProportionalFloorPolicy))).Code);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(data.Outsider, id,
            StockReleaseDecision.Approve, LotValuePolicy.ProportionalFloorPolicy));
        var review = await data.Service.PreviewDecisionAsync(data.Checker, id, StockReleaseDecision.Approve, LotValuePolicy.ProportionalFloorPolicy);
        var expected = B.Expected(review["expected"]); var binding = review["binding"].AsString;
        var approved = await data.Service.DecideAsync(data.Checker, id, 1, StockReleaseDecision.Approve,
            LotValuePolicy.ProportionalFloorPolicy, expected, binding, Data.Meta("release-approved-original"));
        Assert.Equal("approved", approved["status"].AsString); Assert.Equal("11250.00", approved["transferredValue"].AsString);
        Assert.Equal((2L, 0L), await data.ObligationsAsync());
        var balance = await data.BalanceAsync(); Assert.Equal(3L, balance["onHand"].AsInt64);
        Assert.Equal(2L, balance["held"].AsInt64); Assert.Equal(0L, balance["reserved"].AsInt64);
        var held = await data.Rows(LotValueStore.HeldHeads).Find(new BsonDocument("_id", data.ValueAllocationId)).SingleAsync();
        var available = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.Valuation.Receipt["balanceId"])).SingleAsync();
        Assert.Equal(1L, held["remainingPackages"].AsInt64); Assert.Equal("11250.00", held["remainingValue"].AsString);
        Assert.Equal(1L, available["remainingPackages"].AsInt64); Assert.Equal("11250.00", available["remainingValue"].AsString);
        var remainingChoices = await data.Service.SourcesAsync(data.Maker, data.Input().Location);
        var remainingChoice = Assert.Single(remainingChoices["items"].AsBsonArray);
        Assert.Equal(1L, remainingChoice["releaseablePackages"].AsInt64);
        Assert.Equal(0L, remainingChoices["unavailableCount"].AsInt64);
        Assert.Equal(data.ValueAllocationId, remainingChoice["valuationAllocationId"].AsInt64);
        var beforeFalseZero = await data.BusinessEffectsAsync();
        try {
            // Schema-valid false zero triggers owning history authentication, never exhaustion authority.
            Assert.Equal(1L, (await data.Rows(LotValueStore.HeldHeads).UpdateOneAsync(new BsonDocument("_id", held["_id"]),
                Builders<BsonDocument>.Update.Set("remainingPackages", 0L).Set("remainingValue", "0.00"))).ModifiedCount);
            Assert.Equal("lot_value_provenance_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() =>
                data.Service.PreviewAsync(data.Maker, data.Input()))).Code);
        } finally {
            await data.Rows(LotValueStore.HeldHeads).ReplaceOneAsync(new BsonDocument("_id", held["_id"]), held);
        }
        Assert.Equal(beforeFalseZero, await data.BusinessEffectsAsync());
        Assert.Equal(accounting, await data.Valuation.StateAsync()); Assert.Equal(documents, await data.Valuation.Documents.StateAsync());
        var restarted = data.NewService();
        Assert.Equal(proposed, await restarted.ProposeAsync(data.Maker, data.Input(), B.Expected(preview["expected"]), preview["binding"].AsString, Data.Meta("release-original")));
        Assert.Equal(approved, await restarted.DecideAsync(data.Checker, id, 1, StockReleaseDecision.Approve,
            LotValuePolicy.ProportionalFloorPolicy, expected, binding, Data.Meta("release-approved-original")));
        Assert.Equal(proposed, await restarted.RecoverAsync(data.Maker, B.Propose, "release-original"));
        Assert.Equal(approved, await restarted.RecoverAsync(data.Checker, B.Decide, "release-approved-original"));
        Assert.Equal("approve", (await restarted.ReadAsync(data.Maker, id))!["decision"]["decision"].AsString);
        Assert.Equal("idempotency_key_reused", (await Assert.ThrowsAsync<PersistenceConflictException>(() => restarted.DecideAsync(data.Checker, id, 1,
            StockReleaseDecision.Approve, LotValuePolicy.ProportionalFloorPolicy, expected, binding,
            Data.Meta("release-approved-original") with { Reason = "Changed original decision body" }))).Code);
        // Exact same original allocation retains only its finite remainder. No repost,
        // new acquisition or extra stock is manufactured for the second release.
        var second = await data.ProposeAsync("release-remainder-original");
        var tail = await data.DecideAsync(second["proposalId"].AsInt64, "release-remainder-approved");
        Assert.Equal("11250.00", tail["transferredValue"].AsString);
        held = await data.Rows(LotValueStore.HeldHeads).Find(new BsonDocument("_id", data.ValueAllocationId)).SingleAsync();
        available = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.Valuation.Receipt["balanceId"])).SingleAsync();
        Assert.Equal(0L, held["remainingPackages"].AsInt64); Assert.Equal("0.00", held["remainingValue"].AsString);
        Assert.Equal(2L, available["remainingPackages"].AsInt64); Assert.Equal("22500.00", available["remainingValue"].AsString);
        Assert.Equal(1L, (await data.BalanceAsync())["held"].AsInt64);
        Assert.Equal((4L, 0L), await data.ObligationsAsync());
        var physical = await data.BalanceAsync();
        Assert.Equal("stock_release_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker, data.Input()))).Code);
        Assert.Equal(physical, await data.BalanceAsync());
        Assert.Equal(2L, await data.Rows(LotValueStore.Transitions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", B.Namespace)));
        Assert.Equal(accounting, await data.Valuation.StateAsync()); Assert.Equal(documents, await data.Valuation.Documents.StateAsync());
        foreach (var table in new[] { "cashMovements", "paymentAllocations", "procurementFundingAllocations", "procurementDisbursements", "procurementSettlements", "reservations" })
            Assert.False(await data.Rows(table).Find(new BsonDocument()).AnyAsync());
        // Re-enter the genuine current held chain after both approved releases.
        await data.Unit.ExecuteAsync(async (tx, ct) => {
            var reader = new StockReleaseSource(data.Database, data.Unit, data.Clock, data.Valuation.Inspection.Receiving.Procurement.Catalog,
                data.Valuation.Inspection.Receiving.Orders.Service, data.Valuation.Sources, data.Valuation.Service);
            var proof = await reader.ReadCurrentAsync(tx, new(data.Valuation.ReceiptId, data.QualityAllocationId, data.ValueAllocationId), ct);
            Assert.Equal(1L, proof.Balance["held"].AsInt64); return true;
        });
    }

    [WindowsStorageFact]
    public async Task Competing_real_releases_cannot_double_consume_and_losing_approval_rolls_back_all_business_effects() {
        using var timing = new StockReleaseTestDiagnostics(output);
        await using var data = await Data.CreateAsync(fixture, timing.Stage); var accounting = await data.Valuation.StateAsync();
        var input = data.Input(packages: 2); var firstPreview = await data.Service.PreviewAsync(data.Maker, input);
        var first = await data.Service.ProposeAsync(data.Maker, input, B.Expected(firstPreview["expected"]), firstPreview["binding"].AsString, Data.Meta("competing-one"));
        var secondPreview = await data.Service.PreviewAsync(data.Maker, input);
        var second = await data.Service.ProposeAsync(data.Maker, input, B.Expected(secondPreview["expected"]), secondPreview["binding"].AsString, Data.Meta("competing-two"));
        var proposals = new[] { first, second };
        var reviews = new[] {
            await data.Service.PreviewDecisionAsync(data.Checker, first["proposalId"].AsInt64, StockReleaseDecision.Approve, LotValuePolicy.ProportionalFloorPolicy),
            await data.Service.PreviewDecisionAsync(data.Checker, second["proposalId"].AsInt64, StockReleaseDecision.Approve, LotValuePolicy.ProportionalFloorPolicy)
        };
        foreach (var review in reviews) {
            Assert.Equal(review["expected"]["balanceRevision"], review["before"]["version"]);
            Assert.Equal(3L, review["before"]["held"].AsInt64); Assert.Equal(1L, review["after"]["held"].AsInt64);
            Assert.Equal(review["proposal"]["source"], review["source"]);
        }
        async Task<(BsonDocument? Result, PersistenceConflictException? Conflict)> Approve(int index) {
            try { return (await data.Service.DecideAsync(data.Checker, proposals[index]["proposalId"].AsInt64, 1, StockReleaseDecision.Approve,
                LotValuePolicy.ProportionalFloorPolicy, B.Expected(reviews[index]["expected"]), reviews[index]["binding"].AsString,
                Data.Meta("competing-approval-" + index)), null); }
            catch (PersistenceConflictException error) { return (null, error); }
        }
        timing.Restart(); var outcomes = await Task.WhenAll(Approve(0), Approve(1)); timing.Stage("concurrent-approvals");
        var winner = Assert.Single(outcomes, outcome => outcome.Result is not null);
        var loserIndex = Array.FindIndex(outcomes, outcome => outcome.Result is null);
        Assert.InRange(loserIndex, 0, 1); Assert.NotNull(outcomes[loserIndex].Conflict);
        Assert.Contains(outcomes[loserIndex].Conflict!.Code, new[] { "stock_release_changed", "version_conflict" });
        Assert.Equal("22500.00", winner.Result!["transferredValue"].AsString);
        Assert.Equal(1L, (await data.BalanceAsync())["held"].AsInt64);
        Assert.Equal(1L, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(LotValueStore.Transitions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", B.Namespace)));
        Assert.Equal((3L, 1L), await data.ObligationsAsync());
        var effects = await data.BusinessEffectsAsync();
        Assert.Null(await data.Service.RecoverAsync(data.Checker, B.Decide, "competing-approval-" + loserIndex));
        var losing = await Approve(loserIndex); Assert.Null(losing.Result); Assert.NotNull(losing.Conflict);
        Assert.Equal(effects, await data.BusinessEffectsAsync()); Assert.Equal(accounting, await data.Valuation.StateAsync());
        // An independent rejection can still close the losing request using its
        // historical origin, without displaying that old stock as current approval.
        var reject = await data.Service.PreviewDecisionAsync(data.Checker, proposals[loserIndex]["proposalId"].AsInt64,
            StockReleaseDecision.Reject, LotValuePolicy.ProportionalFloorPolicy);
        Assert.Equal(reject["before"], reject["after"]); Assert.Equal(3L, reject["before"]["held"].AsInt64);
        var rejected = await data.DecideAsync(proposals[loserIndex]["proposalId"].AsInt64, "competing-loser-rejected", StockReleaseDecision.Reject);
        Assert.Equal("rejected", rejected["status"].AsString); Assert.Equal((4L, 0L), await data.ObligationsAsync());
        Assert.Equal(1L, (await data.BalanceAsync())["held"].AsInt64);
        Assert.Equal(1L, await data.Rows(LotValueStore.Transitions).CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Factor_expiry_after_actual_preparing_decision_rolls_back_release_value_receipt_and_authority_fences() {
        using var timing = new StockReleaseTestDiagnostics(output);
        await using var data = await Data.CreateAsync(fixture, timing.Stage);
        var proposed = await data.ProposeAsync("factor-expiry-proposal"); var id = proposed["proposalId"].AsInt64;
        var review = await data.Service.PreviewDecisionAsync(data.Checker, id, StockReleaseDecision.Approve, LotValuePolicy.ProportionalFloorPolicy);
        var before = await data.BusinessEffectsAsync(); var accounting = await data.Valuation.StateAsync();
        var authority = new BsonDocument();
        foreach (var table in new[] { "authorizationScopes", "authorizationGrants", "memberships", "identities" })
            authority[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var preparedRequest = 0; var observed = 0; var clockBefore = data.Clock.UtcNow;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "stock-release-factor-expiry-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName && command.CommandName == "insert" &&
                    command.Command.GetValue("insert", BsonNull.Value) == B.Decisions && command.Command["documents"].AsBsonArray.Any(row => row["phase"] == "preparing"))
                    preparedRequest = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (command.CommandName == "insert" && command.RequestId == preparedRequest && Interlocked.CompareExchange(ref observed, 1, 0) == 0)
                    data.Clock.UtcNow = data.Checker.FactorCompletedAt.Add(SessionPolicy.FreshFactorLifetime).AddMilliseconds(1);
            });
        };
        using var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database); var catalog = new CatalogAuthoringService(database, unit, ids, data.Clock);
        var requests = new ProcurementRequestService(database, unit, ids, data.Clock, catalog);
        var orders = new PurchaseOrderService(database, unit, ids, data.Clock, catalog, requests, new(database, unit, ids, data.Clock, catalog));
        var sources = new StockSourceSetupService(database, unit, ids, data.Clock); var receiving = new ProcurementReceivingService(database, unit, ids, data.Clock, catalog, orders, requests);
        var valuation = new ProcurementStockValuationService(database, unit, ids, data.Clock, catalog, orders, receiving, sources);
        var service = new StockReleaseService(database, unit, ids, data.Clock, catalog, orders, sources, valuation);
        try {
            Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideAsync(data.Checker, id, 1,
                StockReleaseDecision.Approve, LotValuePolicy.ProportionalFloorPolicy, B.Expected(review["expected"]), review["binding"].AsString,
                Data.Meta("factor-expiry-decision")))).Code);
        } finally { data.Clock.UtcNow = clockBefore; }
        Assert.Equal(1, observed); Assert.Equal(before, await data.BusinessEffectsAsync()); Assert.Equal(accounting, await data.Valuation.StateAsync());
        foreach (var table in authority.Names)
            Assert.Equal(authority[table], new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Null(await data.Service.RecoverAsync(data.Checker, B.Decide, "factor-expiry-decision"));
        Assert.Equal((1L, 1L), await data.ObligationsAsync());
    }

    // Thin shared fixture for HTTP/browser owners: all business origins are created
    // by their existing commands; the only direct policy input is labelled synthetic.
    internal sealed class Data(ProcurementStockValuationTestData valuation) : IAsyncDisposable {
        internal ProcurementStockValuationTestData Valuation { get; } = valuation;
        internal IMongoDatabase Database => Valuation.Database;
        internal MongoUnitOfWork Unit => Valuation.Unit;
        internal MutableAuthorizationClock Clock => Valuation.Clock;
        internal ActorContext Maker = null!, Checker = null!, Outsider = null!;
        internal long QualityAllocationId, ValueAllocationId;
        internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
        internal StockReleaseService Service => NewService();
        internal StockReleaseService NewService() => new(Database, Unit, new(Database), Clock, Valuation.Inspection.Receiving.Procurement.Catalog,
            Valuation.Inspection.Receiving.Orders.Service, Valuation.Sources, Valuation.Service);
        internal StockReleaseInput Input(long packages = 1) => new(new(new(100), new(101), new(102), new(103)), Valuation.ReceiptId,
            QualityAllocationId, ValueAllocationId, packages, LotValuePolicy.ProportionalFloorPolicy);
        internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic independent release of exact accepted and posted held goods", "stock-release-test");
        internal Task<BsonDocument> BalanceAsync() => Rows("stockBalances").Find(new BsonDocument("_id", Valuation.Receipt["balanceId"])).SingleAsync();
        internal async Task<BsonDocument> BusinessEffectsAsync() {
            var result = new BsonDocument();
            foreach (var table in new[] { B.Proposals, B.Decisions, LotValueStore.HeldHeads, LotValueStore.AvailableHeads, LotValueStore.Transitions, "stockBalances", "movements" })
                result[table] = new BsonArray(await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
            foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
                result[table] = new BsonArray(await Rows(table).Find(new BsonDocument(field, new BsonDocument("$in", new BsonArray { B.Propose, B.Decide })))
                    .Sort(new BsonDocument("_id", 1)).ToListAsync());
            return result;
        }
        internal Task<(long MembershipCount, long OpenCount)> ObligationsAsync() => Unit.ExecuteAsync(async (tx, ct) => {
            await new StockSourceTopologyFence(Database).FenceAsync(tx, 100, ct);
            return await Service.ReadObligationAsync(tx, Valuation.PoolId, ct);
        });
        internal async Task<BsonDocument> ProposeAsync(string key) {
            var preview = await Service.PreviewAsync(Maker, Input());
            return await Service.ProposeAsync(Maker, Input(), B.Expected(preview["expected"]), preview["binding"].AsString, Meta(key));
        }
        internal async Task<BsonDocument> DecideAsync(long id, string key, StockReleaseDecision decision = StockReleaseDecision.Approve) {
            var preview = await Service.PreviewDecisionAsync(Checker, id, decision, LotValuePolicy.ProportionalFloorPolicy);
            return await Service.DecideAsync(Checker, id, 1, decision, LotValuePolicy.ProportionalFloorPolicy,
                B.Expected(preview["expected"]), preview["binding"].AsString, Meta(key));
        }
        internal static async Task<Data> CreateAsync(MongoFixture fixture, Action<string>? diagnostics = null, bool useSystemClock = false, DateOnly? inspectedExpiry = null) {
            var valuation = await ProcurementStockValuationTestData.CreateAsync(fixture, activateEmptySource: true, useSystemClock: useSystemClock, inspectedExpiry: inspectedExpiry);
            diagnostics?.Invoke("fixture");
            var data = new Data(valuation);
            try {
                var value = await valuation.ProposeAsync("release-clean-valued-original", valuation.Input(packages: 2));
                var posted = await valuation.DecideAsync(value["proposalId"].AsInt64, "release-clean-valued-approved");
                data.ValueAllocationId = posted["allocationId"].AsInt64;
                diagnostics?.Invoke("value-posting");
                await data.AcceptQualityAsync();
                diagnostics?.Invoke("quality-approval");
                var scope = ProcurementRequestTestData.Scope;
                data.Maker = await valuation.Base.AddActorAsync(103301, scope, Permissions.StockReleasePropose, Permissions.StockReleaseApprove, Permissions.ProcurementStockValuationDecide);
                data.Checker = await valuation.Base.AddActorAsync(103302, scope, Permissions.StockReleaseApprove, Permissions.ProcurementStockValuationDecide);
                data.Outsider = await valuation.Base.AddActorAsync(103303, new(GrantScopeKind.FulfillmentWarehouse, 100,
                    ShopId: valuation.Inspection.Receiving.Procurement.ForeignShopId, WarehouseId: valuation.Inspection.Receiving.Procurement.ForeignWarehouseId, ChannelId: 103),
                    Permissions.StockReleasePropose, Permissions.StockReleaseApprove, Permissions.ProcurementStockValuationDecide);
                await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Checker.UserId, data.Outsider.UserId);
                diagnostics?.Invoke("fixture-access");
                return data;
            } catch { await data.DisposeAsync(); throw; }
        }
        private async Task AcceptQualityAsync() {
            var maker = await Valuation.Base.AddActorAsync(103304, ProcurementRequestTestData.Scope, Permissions.ProcurementRead, Permissions.StockRead,
                Permissions.ProcurementQualityAcceptanceRead, Permissions.ProcurementQualityAcceptancePropose);
            var checker = await Valuation.Base.AddActorAsync(103305, ProcurementRequestTestData.Scope, Permissions.ProcurementRead, Permissions.StockRead,
                Permissions.ProcurementQualityAcceptanceRead, Permissions.ProcurementQualityAcceptanceDecide);
            await PersonTestData.SeedDistinctPeopleAsync(Database, Clock, maker.UserId, checker.UserId);
            var people = await Unit.ExecuteAsync((tx, ct) => new PersonRegistryService(Database, Clock).ResolveAsync(tx,
                [Valuation.SourceMaker.UserId, Valuation.SourceChecker.UserId], ct));
            var policyId = (await new IdAllocator(Database).NextAsync(Q.Policies)).Value;
            await Rows(Q.Policies).InsertOneAsync(new BsonDocument { { "_id", policyId }, { "schemaVersion", 1 }, { "revision", 1L }, { "active", true },
                { "location", ProcurementRequestBson.Location(ProcurementRequestTestData.Location) },
                { "qualityProcedureReference", "SYNTHETIC-INDEPENDENT-SEALED-UNDAMAGED-OBSERVATION" },
                { "putawayProcedureReference", "SYNTHETIC-SEGREGATED-HELD-LOCATION-PROCEDURE" }, { "allowNonExpiring", false },
                { "nonExpiringProcedureReference", BsonNull.Value }, { "maximumObservationAgeMilliseconds", 900000L },
                { "approvalReference", "SYNTHETIC-LOCAL-QUALITY-POLICY-NOT-PRODUCTION-ACCEPTANCE" },
                { "proposedBy", Valuation.SourceMaker.UserId }, { "approvedBy", Valuation.SourceChecker.UserId },
                { "people", new BsonArray(people.Select(ProcurementRequestBson.Person)) }, { "approvedAt", Clock.UtcNow.UtcDateTime },
                { "expiresAt", Clock.UtcNow.AddDays(1).UtcDateTime }, { "fence", 0L } });
            var receipt = await Rows(ProcurementStockReceiptBson.Receipts).Find(new BsonDocument("_id", Valuation.ReceiptId)).SingleAsync();
            var inspection = await Rows("procurementInspectionSubmissions").Find(new BsonDocument("_id", Valuation.Documents.InspectionId)).SingleAsync();
            var line = inspection["input"]["lines"][0];
            var package = PurchaseOrderService.FundingOrderView(receipt["source"].AsBsonDocument)["lines"].AsBsonArray.Single(row => row["skuId"] == line["requestSkuId"]);
            var input = new ProcurementQualityAcceptanceInput(ProcurementRequestTestData.Location, Valuation.ReceiptId, 1, 2, policyId, 1,
                Clock.UtcNow, "SYNTHETIC-CLEAN-QUALITY-MAKER", line["putawayReference"].AsString, "SYNTHETIC-CLEAN-SEGREGATED-SUBSET",
                package["packageKind"] == "sealed" ? "sealed_undamaged" : "intact_as_defined", receipt["input"]["lotReference"].AsString,
                DateOnly.ParseExact(receipt["input"]["expiresOn"].AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture), null,
                "Synthetic explicit quality acceptance of clean received goods; no release authority");
            var quality = new ProcurementQualityAcceptanceService(Database, Unit, new(Database), Clock,
                Valuation.Inspection.Receiving.Procurement.Catalog, Valuation.Inspection.Receiving.Orders.Service, Valuation.Inspection.Receiving.Service, Valuation.Sources);
            var preview = await quality.PreviewAsync(maker, input);
            var proposed = await quality.ProposeAsync(maker, input, preview["binding"].AsString, ProcurementQualityAcceptanceTestData.Meta("release-clean-quality", input.Reason));
            var observation = new ProcurementQualityAcceptanceObservation(Clock.UtcNow, input.AcceptedPackages, "SYNTHETIC-CLEAN-QUALITY-CHECKER",
                input.PutawayReference, input.SegregationReference, input.AcceptedCondition, input.LotReference, input.ExpiresOn, input.NonExpiringReason);
            var review = await quality.PreviewDecisionAsync(checker, proposed["proposalId"].AsInt64, ProcurementQualityAcceptanceDecision.Approve, observation);
            var approved = await quality.DecideAsync(checker, proposed["proposalId"].AsInt64, 1, ProcurementQualityAcceptanceDecision.Approve, observation,
                review["binding"].AsString, ProcurementQualityAcceptanceTestData.Meta("release-clean-quality-approved"));
            QualityAllocationId = approved["allocationId"].AsInt64;
        }
        public ValueTask DisposeAsync() => Valuation.DisposeAsync();
    }
}
