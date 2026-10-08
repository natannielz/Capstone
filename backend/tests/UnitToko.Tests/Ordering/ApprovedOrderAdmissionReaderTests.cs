using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;

namespace UnitToko.Tests.Ordering;

/// <summary>Local synthetic identity/catalog bootstrap, but actual commercial,
/// postal/spending/source control, release/conversion, production admission and
/// order/reservation producers. No test-only admission submits the first order.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ApprovedOrderAdmissionReaderTests(MongoFixture fixture)
{
    private static ApprovedOrderAdmissionReader Reader(GenuineFiniteDispatchTestData data) => new(data.Database, data.Unit,
        new(data.Database), data.Clock, data.Stock.Valuation.Inspection.Receiving.Procurement.Organizations.Organizations);
    private static OrderSubmissionService Orders(GenuineFiniteDispatchTestData data, ApprovedOrderAdmissionReader reader) =>
        new(data.Database, data.Unit, new(data.Database), data.Clock, reader);
    private static async Task<OrderCatalogQuote> QuoteAsync(GenuineFiniteDispatchTestData data) =>
        (await new OrderQuoteService(data.Database, data.Unit, data.Clock).PreviewAsync(data.Buyer, data.Input.Context, data.Input.Selections)).Catalog;

    [WindowsStorageFact]
    public async Task A_genuine_released_package_inside_the_independently_approved_default_buffer_refuses_without_order_stock_or_money_effects() {
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, false, createOrder: false);
        var policy = await data.Stock.Valuation.Sources.ReadOperatingPolicyAsync(data.Stock.Valuation.SourceMaker, data.Stock.Valuation.PoolId);
        Assert.Equal("approve", policy["decision"]["decision"].AsString);
        var setup = StockSourceSetupBson.ReadInput(policy["proposal"]["configuration"]["source"]["input"].AsBsonDocument);
        Assert.Equal(12L, setup.SafetyBufferBaseUnits);
        var line = Assert.Single((await QuoteAsync(data)).Lines); Assert.Equal(12L, line.Definition.BaseUnitsPerPackage); Assert.Equal(1, line.Packages);
        var physical = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        Assert.Equal(1L, StockBalancePolicy.AvailablePackages(physical.Balance));
        var value = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        Assert.Equal(1L, value["remainingPackages"].AsInt64); Assert.Equal("11250.00", value["remainingValue"].AsString);
        var before = await data.BusinessAsync();
        var failure = await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => Orders(data, Reader(data)).PreviewAsync(data.Buyer, data.Input));
        Assert.Equal("order_stock_unavailable", failure.Code); Assert.Equal(before, await data.BusinessAsync());
        Assert.Equal(0L, await data.Rows(B.Orders).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows(B.Reservations).CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Genuine_first_order_reserves_released_or_converted_quantity_once_without_consuming_value_or_posting_COGS(bool converted) {
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, converted, createOrder: false, reviewedSafetyBufferBaseUnits: 0);
        Assert.Equal(0L, await data.Rows(B.Orders).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows(B.Reservations).CountDocumentsAsync(new BsonDocument()));
        var physical = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        var value = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        var valueHistory = new BsonArray(await data.Rows(LotValueStore.Transitions).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var accounting = await data.Stock.Valuation.StateAsync(); var documents = await data.Stock.Valuation.Documents.StateAsync();
        var reader = Reader(data); var orders = Orders(data, reader); var preview = await orders.PreviewAsync(data.Buyer, data.Input);
        Assert.Equal(OrderSpendingState.Pending, preview.SpendingState);
        var result = await orders.SubmitAsync(data.Buyer, data.Input, preview.Review, GenuineFiniteDispatchTestData.Meta("production-first-order"));
        Assert.Equal(result, await orders.SubmitAsync(data.Buyer, data.Input, preview.Review, GenuineFiniteDispatchTestData.Meta("production-first-order")));
        Assert.Equal(result, await orders.RecoverAsync(data.Buyer, "production-first-order"));
        var current = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        Assert.Equal(physical with { Balance = StockBalancePolicy.Reserve(physical.Balance, physical.Balance.Revision, converted ? 12 : 1) }, current);
        Assert.Equal(value, await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        Assert.Equal(valueHistory, new BsonArray(await data.Rows(LotValueStore.Transitions).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Equal(1L, await data.Rows(B.Orders).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(B.Reservations).CountDocumentsAsync(new BsonDocument()));
        var movement = await data.Rows("movements").Find(new BsonDocument("source.namespace", "ordering.order")).SingleAsync();
        Assert.Equal("Reserve", Assert.Single(movement["legs"].AsBsonArray)["action"].AsString);
        var row = await data.Rows(B.Orders).Find(new BsonDocument()).SingleAsync();
        var decision = await data.Rows(OrderCommercialPolicyBson.Decisions).Find(new BsonDocument("_id", row["terms"]["creditDecision"]["id"])).SingleAsync();
        Assert.Equal("approve", decision["decision"].AsString);
        Assert.Equal("not_applicable", decision["details"]["rule"]["applicability"]["creditHold"].AsString);
        Assert.DoesNotContain('\n', row["terms"]["buyer"]["address"].AsString);
        Assert.Contains(data.Input.Delivery!.City, row["terms"]["buyer"]["address"].AsString);
        Assert.Equal(accounting, await data.Stock.Valuation.StateAsync()); Assert.Equal(documents, await data.Stock.Valuation.Documents.StateAsync());
    }

    [WindowsStorageFact]
    public async Task Actual_required_capacity_and_spending_applicability_mismatch_refuse_before_the_first_order_or_reservation() {
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, false, createOrder: false, reviewedSafetyBufferBaseUnits: 0);
        var organizations = data.Stock.Valuation.Inspection.Receiving.Procurement.Organizations.Organizations;
        var policies = new OrderCommercialPolicyService(data.Database, data.Unit, new(data.Database), data.Clock, organizations);
        var shop = CatalogAuthoringTestData.Shop;
        var maker = await data.Catalog.ActorAsync(719010, shop, Permissions.OrderCommercialPolicyPropose);
        var checker = await data.Catalog.ActorAsync(719011, shop, Permissions.OrderCommercialPolicyApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, checker.UserId);
        var before = await PhysicalAsync(data);
        foreach (var required in new[] { "budget", "costCenter", "creditHold", "spending-mismatch" }) {
            var current = await data.Base.Store.ReadAsync(data.Buyer, async (authority, tx, ct) => {
                var source = await policies.ReadCurrentSourceAsync(data.Buyer, authority, tx, data.Input.Context, new(100, 101, 103, "IDR"), ct);
                return source.Snapshot;
            }, default);
            var rule = OrderCommercialPolicyBson.Rule(current["details"]["rule"]);
            var applicability = new OrderCommercialApplicabilityRule(required == "budget" ? OrderCommercialApplicability.Required : OrderCommercialApplicability.NotApplicable,
                required == "costCenter" ? OrderCommercialApplicability.Required : OrderCommercialApplicability.NotApplicable,
                required == "spending-mismatch" ? OrderCommercialApplicability.NotApplicable : OrderCommercialApplicability.Required,
                required == "creditHold" ? OrderCommercialApplicability.Required : OrderCommercialApplicability.NotApplicable);
            var sellers = current["sellerSources"];
            var input = new OrderCommercialPolicyInput(new(ScopeNodeKind.Branch, 11, new(100, 101, 103, "IDR")),
                current["source"]["target"]["version"].AsInt64, current["source"]["profile"]["version"].AsInt64,
                new(sellers["organization"]["target"]["version"].AsInt64, sellers["organization"]["profile"]["version"].AsInt64,
                    sellers["shop"]["target"]["version"].AsInt64, sellers["shop"]["profile"]["version"].AsInt64,
                    sellers["channel"]["target"]["version"].AsInt64, sellers["channel"]["profile"]["version"].AsInt64),
                current["version"].AsInt64, rule with { Applicability = applicability }, data.Clock.UtcNow.AddMinutes(-1), data.Clock.UtcNow.AddDays(1));
            var preview = await policies.PreviewAsync(maker, input);
            var proposed = await policies.ProposeAsync(maker, input, preview.Binding, GenuineFiniteDispatchTestData.Meta("production-policy-" + required));
            var review = await policies.PreviewDecisionAsync(checker, proposed["proposalId"].AsInt64, 1, OrderCommercialPolicyDecision.Approve);
            await policies.DecideAsync(checker, proposed["proposalId"].AsInt64, 1, OrderCommercialPolicyDecision.Approve, review.Binding,
                GenuineFiniteDispatchTestData.Meta("production-policy-approved-" + required));
            var error = await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => Orders(data, Reader(data)).PreviewAsync(data.Buyer, data.Input));
            Assert.Equal(required == "spending-mismatch" ? "order_spending_applicability_changed" : "order_capacity_unavailable", error.Code);
            Assert.Equal(before, await PhysicalAsync(data));
        }
    }

    [WindowsStorageFact]
    public async Task A_genuine_capture_rejects_foreign_actor_cross_attempt_and_unowned_quantity_math_with_whole_rollback() {
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, false, createOrder: false, reviewedSafetyBufferBaseUnits: 0);
        var reader = Reader(data); var quote = await QuoteAsync(data); var before = await PhysicalAsync(data);
        var baseline = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        var unowned = await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => data.Unit.ExecuteAsync<bool>(async (tx, ct) => {
            var admission = await reader.ReadAndFenceAsync(tx, data.Buyer, data.Input, quote, ct);
            Assert.Equal("order_admission_unavailable", (await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() =>
                reader.CompleteAsync(tx, data.Spender, admission, ct))).Code);
            await data.Rows("fixtureProductionAdmissionPriorWork").InsertOneAsync(tx, new BsonDocument("_id", 1L), cancellationToken: ct);
            // Explicit negative mechanical write, with no order/receipt/producer.
            // Arithmetic equivalence must not satisfy the production owning proof.
            _ = await new InventoryRepository(data.Database).ApplyAsync(tx, [Assert.Single(admission.Stock).Change], ct);
            await reader.CompleteAsync(tx, data.Buyer, admission, ct); return true;
        }, default));
        Assert.Equal("order_reservation_owner_unavailable", unowned.Code);
        Assert.Equal(before, await PhysicalAsync(data));
        Assert.Equal(0L, await data.Rows("fixtureProductionAdmissionPriorWork").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(baseline, InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync()));
        using var next = await data.Database.Client.StartSessionAsync(); next.StartTransaction();
        var retained = await reader.ReadAndFenceAsync(next, data.Buyer, data.Input, quote, default);
        await next.CommitTransactionAsync(); next.StartTransaction();
        Assert.Equal("order_admission_unavailable", (await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() =>
            reader.CompleteAsync(next, data.Buyer, retained, default))).Code);
        await next.AbortTransactionAsync(); Assert.Equal(before, await PhysicalAsync(data));
    }
    private static async Task<BsonDocument> PhysicalAsync(GenuineFiniteDispatchTestData data) {
        var result = new BsonDocument();
        foreach (var table in new[] { "stockBalances", "movements", "lots", LotValueStore.AvailableHeads, LotValueStore.HeldHeads, LotValueStore.Transitions,
            B.Orders, B.Suborders, B.Reservations, "journalEntries", "invoices" })
            result[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
}
