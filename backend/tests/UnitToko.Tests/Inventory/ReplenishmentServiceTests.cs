using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;
using UnitToko.Tests.Procurement;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ReplenishmentServiceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Actual_finite_stock_advice_persists_exact_inputs_and_recovers_original_key_without_changing_stock_or_buying_goods()
    {
        await using var data = await StockReleaseServiceTests.Data.CreateAsync(fixture);
        var released = await data.ProposeAsync("replenishment-release");
        await data.DecideAsync(released["proposalId"].AsInt64, "replenishment-release-approved");
        var actor = await data.Valuation.Base.AddActorAsync(109801, ProcurementRequestTestData.Scope, Permissions.StockRead);
        var physical = await data.BalanceAsync();
        var pool = await data.Rows("inventoryPools").Find(new BsonDocument("_id", physical["physicalPoolId"])).SingleAsync();
        var sku = await data.Rows("skus").Find(new BsonDocument("_id", physical["skuId"])).SingleAsync();
        var input = new ReplenishmentReviewInput(ProcurementRequestTestData.Scope, pool["_id"].AsInt64, pool["revision"].AsInt64,
            sku["_id"].AsInt64, sku["revision"].AsInt64, physical["definitionId"].AsInt64,
            new(4m, 2, 3, 2, 1, 2), "Explicit synthetic daily demand, lead time and purchase rules for this test only");
        var service = new ReplenishmentService(data.Database, data.Unit, new(data.Database), data.Clock);
        var purchaseBefore = await data.Rows("purchaseOrders").CountDocumentsAsync(new BsonDocument());
        var movementBefore = await data.Rows("movements").CountDocumentsAsync(new BsonDocument());
        var first = await service.EvaluateAsync(actor, input, new("replenishment-first", "Review exact current stock and explicitly supplied planning inputs", "replenishment-test"));
        Assert.True(first.Stock.CurrentAuthority); Assert.Equal(1L, first.Stock.UsableBeforeReservations);
        Assert.Equal(0L, first.Stock.ReservedPackages); Assert.Equal(0L, first.Stock.AdditionalUnreservedCommitments);
        Assert.Equal(1L, first.Advice.AvailableAfterCommitments); Assert.True(first.Advice.ThresholdAlert); Assert.Equal(11L, first.Advice.RequiredStockPackages);
        Assert.Equal(input, first.Input); Assert.Equal(ReplenishmentPolicy.Version, first.Advice.RuleVersion);
        // This actual PO is fully received: complete producer coverage proves zero open supply.
        Assert.Equal(0L, first.Advice.EligibleSupplyPackages); Assert.Equal(1L, first.Advice.InventoryPosition);
        Assert.Equal(10L, first.Advice.SuggestedPurchasePackages);
        var restarted = new ReplenishmentService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        var recovered = await restarted.RecoverAsync(actor, "replenishment-first"); Assert.NotNull(recovered); Assert.Equal(first.Id, recovered.Id);
        Assert.Equal(ReplenishmentRecords.Advice(first.Advice), ReplenishmentRecords.Advice(recovered.Advice));
        var replay = await restarted.EvaluateAsync(actor, input, new("replenishment-first", "Review exact current stock and explicitly supplied planning inputs", "replenishment-test"));
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal("idempotency_key_reused", (await Assert.ThrowsAsync<PersistenceConflictException>(() => restarted.EvaluateAsync(actor,
            input with { Inputs = input.Inputs with { DailyDemandPerDay = 99 } }, new("replenishment-first", "Review exact current stock and explicitly supplied planning inputs", "replenishment-test")))).Code);
        Assert.Equal(physical, await data.BalanceAsync()); Assert.Equal(movementBefore, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(purchaseBefore, await data.Rows("purchaseOrders").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(ReplenishmentRecords.Collection).CountDocumentsAsync(new BsonDocument()));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.Outsider, first.Id));
        var missingDemand = await service.EvaluateAsync(actor, input with { Inputs = input.Inputs with { DailyDemandPerDay = null } },
            new("replenishment-missing-demand", "Retain threshold alert while demand remains unknown", "replenishment-test"));
        Assert.True(missingDemand.Advice.ThresholdAlert); Assert.Null(missingDemand.Advice.SuggestedPurchasePackages);
        Assert.Contains("daily_demand_missing", missingDemand.Advice.Warnings);
        var latest = await restarted.HistoryAsync(actor, input.Location, null, 1);
        Assert.Equal(missingDemand.Id, Assert.Single(latest.Items).Id); Assert.Equal(missingDemand.Id, latest.NextBefore);
        var older = await restarted.HistoryAsync(actor, input.Location, latest.NextBefore, 1);
        Assert.Equal(first.Id, Assert.Single(older.Items).Id); Assert.Null(older.NextBefore);
        Assert.Empty((await restarted.HistoryAsync(actor, input.Location, first.Id, 1)).Items);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => restarted.HistoryAsync(data.Outsider, input.Location, null, 1));
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => restarted.HistoryAsync(actor, input.Location, 0, 1));
        Assert.Equal(physical, await data.BalanceAsync());
        var savedRow = await data.Rows(ReplenishmentRecords.Collection).Find(new BsonDocument("_id", first.Id)).SingleAsync();
        await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", savedRow["auditId"]));
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => restarted.HistoryAsync(actor, input.Location, missingDemand.Id, 1));
    }

    [WindowsStorageFact]
    public async Task Genuine_order_reservations_are_subtracted_once_and_an_unproved_physical_quantity_cannot_create_advice()
    {
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, converted: false, createOrder: true);
        var actor = await data.Base.AddActorAsync(109802, ProcurementRequestTestData.Scope, Permissions.StockRead);
        var physical = await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        var pool = await data.Rows("inventoryPools").Find(new BsonDocument("_id", physical["physicalPoolId"])).SingleAsync();
        var sku = await data.Rows("skus").Find(new BsonDocument("_id", physical["skuId"])).SingleAsync();
        var input = new ReplenishmentReviewInput(ProcurementRequestTestData.Scope, pool["_id"].AsInt64, pool["revision"].AsInt64,
            sku["_id"].AsInt64, sku["revision"].AsInt64, physical["definitionId"].AsInt64, new(1m, 1, 0, 1, 1, 1), "Explicit synthetic replenishment inputs");
        var service = new ReplenishmentService(data.Database, data.Unit, new(data.Database), data.Clock);
        var result = await service.EvaluateAsync(actor, input, new("replenishment-reserved", "Review genuine reserved stock", "replenishment-test"));
        Assert.Equal(physical["reserved"].AsInt64, result.Stock.ReservedPackages);
        Assert.Equal(0L, result.Stock.AdditionalUnreservedCommitments);
        Assert.Equal(physical["onHand"].AsInt64 - physical["held"].AsInt64 - physical["expired"].AsInt64 - physical["reserved"].AsInt64,
            result.Advice.AvailableAfterCommitments);
        await data.Rows("stockBalances").UpdateOneAsync(physical, Builders<BsonDocument>.Update.Inc("onHand", 1L));
        await Assert.ThrowsAnyAsync<Exception>(() => service.EvaluateAsync(actor, input,
            new("replenishment-unproved", "Unproved extra stock must be rejected", "replenishment-test")));
        Assert.Equal(1L, await data.Rows(ReplenishmentRecords.Collection).CountDocumentsAsync(new BsonDocument()));
        Assert.Null(await service.RecoverAsync(actor, "replenishment-unproved"));
    }
}
