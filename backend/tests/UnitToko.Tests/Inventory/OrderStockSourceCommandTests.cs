using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Inventory;

/// <summary>Actual commands/ledger/Mongo plus genuine independent source setup. The retained
/// active/on-hand and commercial/dispatch admission fixture is explicitly SYNTHETIC isolation;
/// this proves producer coupling, not governed activation, seven-domain drain or production acceptance.</summary>
[Collection("Mongo")]
public sealed class OrderStockSourceCommandTests(MongoFixture fixture)
{
    [Fact]
    public async Task Actual_source_separated_submission_advances_each_genuine_pool_once_and_replay_adds_no_source_effect()
    {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        await data.AddSecondFulfillmentAsync(10);
        var originalHeads = await RowsAsync(data, OrderStockSourceRecords.Heads);
        var originalPools = await RowsAsync(data, "inventoryPools");
        Assert.Equal(2, originalHeads.Count); Assert.Equal(2, originalPools.Count);
        var preview = await data.Service.PreviewAsync(data.Buyer, data.Input);
        var result = await data.Service.SubmitAsync(data.Buyer, data.Input, preview.Review, CatalogPriceTestData.Meta("m90-two-pool-order"));
        var orderId = result["orderId"].AsInt64;
        var reservations = await RowsAsync(data, "reservations"); Assert.Equal(2, reservations.Count);
        Assert.Equal(2, reservations.Select(row => row["allocation"]["poolId"].AsInt64).Distinct().Count());
        var packages = reservations.Select(row => row["allocation"]["packages"].AsInt64).Order().ToArray();
        Assert.Equal(new[] { 2L, 3L }, packages);
        foreach (var original in originalHeads)
        {
            var poolId = original["_id"].AsInt64; var head = await HeadAsync(data, poolId); var pool = await PoolAsync(data, poolId);
            Assert.Equal(original["revision"].AsInt64 + 1, head["revision"].AsInt64);
            Assert.Equal(original["fence"].AsInt64 + 1, head["fence"].AsInt64);
            var priorPool = Assert.Single(originalPools, row => row["_id"] == original["_id"]);
            Assert.Equal(priorPool["fence"].AsInt64 + 2, pool["fence"].AsInt64); // ledger Reserve + owning producer CAS
            var transition = await LatestAsync(data, poolId);
            Assert.Equal("submission", transition["sourceKind"].AsString); Assert.Equal(orderId, transition["sourceId"].AsInt64);
            Assert.Equal(original["lastTransitionId"], transition["previousId"]); Assert.Equal(original["lastHash"], transition["previousHash"]);
            var effect = Assert.Single(transition["effects"].AsBsonArray);
            var actual = Assert.Single(reservations, row => row["allocation"]["poolId"].AsInt64 == poolId);
            Assert.Equal(actual["_id"], effect["reservationId"]); Assert.Equal(actual["allocation"], effect["allocation"]);
            Assert.True(effect["before"].IsBsonNull); Assert.Equal(OrderStockSourceRecords.State(actual), effect["after"].AsBsonDocument);
            await RequireHistoryAsync(data, poolId);
        }
        Assert.Equal(2L, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        var beforeReplay = await ProducerEffectsAsync(data);
        Assert.Equal(result, await data.Service.SubmitAsync(data.Buyer, data.Input, preview.Review, CatalogPriceTestData.Meta("m90-two-pool-order")));
        Assert.Equal(result, await data.Service.RecoverAsync(data.Buyer, "m90-two-pool-order"));
        Assert.Equal(beforeReplay, await ProducerEffectsAsync(data));
        foreach (var original in originalHeads) await RequireOpenCountsAsync(data, original["_id"].AsInt64, 1, 1);
        Assert.Equal(0L, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Spending_and_store_approval_without_stock_changes_still_advance_real_head_and_pool_and_replay_once()
    {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        var actors = await OrderDecisionTests.Actors(data); var orderId = await OrderDecisionTests.Submit(data);
        var reservation = await data.Rows("reservations").Find(new BsonDocument()).SingleAsync(); var poolId = reservation["allocation"]["poolId"].AsInt64;
        var physical = await PhysicalAsync(data);
        var head = await HeadAsync(data, poolId); var pool = await PoolAsync(data, poolId);
        var review = await data.Service.ReviewOrderAsync(actors.Approver, orderId, null, OrderReviewStage.Spending);
        var approved = await data.Service.DecideOrderAsync(actors.Approver, orderId, null, OrderReviewStage.Spending,
            review.Revision, review.Review, CatalogPriceTestData.Meta("m90-spending-no-stock"));
        await RequireEmptyDecisionAsync(data, poolId, head, pool, approved["decisionId"].AsInt64);
        Assert.Equal(physical, await PhysicalAsync(data));
        var beforeReplay = await ProducerEffectsAsync(data);
        Assert.Equal(approved, await data.Service.DecideOrderAsync(actors.Approver, orderId, null, OrderReviewStage.Spending,
            review.Revision, review.Review, CatalogPriceTestData.Meta("m90-spending-no-stock")));
        Assert.Equal(approved, await data.Service.RecoverOrderDecisionAsync(actors.Approver, OrderReviewStage.Spending, "m90-spending-no-stock"));
        Assert.Equal(beforeReplay, await ProducerEffectsAsync(data));
        head = await HeadAsync(data, poolId); pool = await PoolAsync(data, poolId);
        var store = Assert.Single((await data.Service.ReviewListAsync(actors.Store, OrderReviewStage.Store)).Items);
        var accepted = await data.Service.DecideOrderAsync(actors.Store, orderId, store.SuborderId, OrderReviewStage.Store,
            store.Revision, store.Review, CatalogPriceTestData.Meta("m90-store-no-stock"));
        Assert.Equal("accepted", accepted["state"].AsString);
        await RequireEmptyDecisionAsync(data, poolId, head, pool, accepted["decisionId"].AsInt64);
        Assert.Equal(physical, await PhysicalAsync(data));
        beforeReplay = await ProducerEffectsAsync(data);
        Assert.Equal(accepted, await data.Service.DecideOrderAsync(actors.Store, orderId, store.SuborderId, OrderReviewStage.Store,
            store.Revision, store.Review, CatalogPriceTestData.Meta("m90-store-no-stock")));
        Assert.Equal(accepted, await data.Service.RecoverOrderDecisionAsync(actors.Store, OrderReviewStage.Store, "m90-store-no-stock"));
        Assert.Equal(beforeReplay, await ProducerEffectsAsync(data));
        await RequireOpenCountsAsync(data, poolId, 1, 1);
    }

    [Fact]
    public async Task Missing_actual_head_aborts_dispatch_stock_and_audit_then_genuine_partial_dispatch_records_exact_reservation_history()
    {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order;
        var reservationBefore = await order.Rows("reservations").Find(new BsonDocument()).SingleAsync();
        var poolId = reservationBefore["allocation"]["poolId"].AsInt64; var head = await HeadAsync(order, poolId); var pool = await PoolAsync(order, poolId);
        var preview = await data.PreviewAsync();
        Assert.Equal(1L, (await order.Rows(OrderStockSourceRecords.Heads).DeleteOneAsync(head)).DeletedCount);
        var beforeRefusal = await RollbackStateAsync(order);
        var denied = await Assert.ThrowsAsync<DomainValidationException>(() => data.DispatchAsync(preview, "m90-missing-head-dispatch"));
        Assert.Equal("order_source_obligation_unavailable", denied.Code);
        Assert.Equal(beforeRefusal, await RollbackStateAsync(order));
        Assert.Equal(0L, await order.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "m90-missing-head-dispatch")));
        // Restore only the exact genuinely created row deliberately deleted above; no replacement owner or synthesized transition.
        await order.Rows(OrderStockSourceRecords.Heads).InsertOneAsync(head);
        var sent = await data.DispatchAsync(preview, "m90-dispatch-producer");
        var after = await order.Rows("reservations").Find(new BsonDocument("_id", reservationBefore["_id"])).SingleAsync();
        Assert.Equal(1L, after["remaining"].AsInt64); Assert.Equal(1L, after["dispatched"].AsInt64); Assert.Equal(0L, after["released"].AsInt64);
        Assert.Equal(reservationBefore["revision"].AsInt64 + 1, after["revision"].AsInt64);
        var currentHead = await HeadAsync(order, poolId); var currentPool = await PoolAsync(order, poolId);
        Assert.Equal(head["revision"].AsInt64 + 1, currentHead["revision"].AsInt64);
        Assert.Equal(head["fence"].AsInt64 + 1, currentHead["fence"].AsInt64);
        Assert.Equal(pool["fence"].AsInt64 + 2, currentPool["fence"].AsInt64); // physical dispatch + producer CAS
        var transition = await LatestAsync(order, poolId); Assert.Equal("shipment", transition["sourceKind"].AsString);
        Assert.Equal(sent["shipmentId"], transition["sourceId"]); Assert.Equal(head["lastTransitionId"], transition["previousId"]);
        Assert.Equal(head["lastHash"], transition["previousHash"]);
        var effect = Assert.Single(transition["effects"].AsBsonArray);
        Assert.Equal(reservationBefore["_id"], effect["reservationId"]); Assert.Equal(reservationBefore["allocation"], effect["allocation"]);
        Assert.Equal(OrderStockSourceRecords.State(reservationBefore), effect["before"].AsBsonDocument);
        Assert.Equal(OrderStockSourceRecords.State(after), effect["after"].AsBsonDocument);
        await RequireHistoryAsync(order, poolId);
        var beforeReplay = await ProducerEffectsAsync(order);
        Assert.Equal(sent, await data.DispatchAsync(preview, "m90-dispatch-producer"));
        Assert.Equal(sent, await data.Service.RecoverAsync(data.Dispatcher, "m90-dispatch-producer"));
        Assert.Equal(beforeReplay, await ProducerEffectsAsync(order));
        Assert.Equal(2L, await order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await order.Rows("shipments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await order.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        await RequireOpenCountsAsync(order, poolId, 1, 1);
    }

    private static async Task RequireEmptyDecisionAsync(OrderSubmissionTestData data, long poolId, BsonDocument beforeHead, BsonDocument beforePool, long decisionId)
    {
        var head = await HeadAsync(data, poolId); var pool = await PoolAsync(data, poolId);
        Assert.Equal(beforeHead["revision"].AsInt64 + 1, head["revision"].AsInt64);
        Assert.Equal(beforeHead["fence"].AsInt64 + 1, head["fence"].AsInt64);
        Assert.Equal(beforePool["fence"].AsInt64 + 1, pool["fence"].AsInt64);
        var transition = await LatestAsync(data, poolId); Assert.Equal("decision", transition["sourceKind"].AsString);
        Assert.Equal(decisionId, transition["sourceId"].AsInt64); Assert.Empty(transition["effects"].AsBsonArray);
        Assert.Equal(beforeHead["lastTransitionId"], transition["previousId"]); Assert.Equal(beforeHead["lastHash"], transition["previousHash"]);
        await RequireHistoryAsync(data, poolId);
    }
    private static async Task RequireHistoryAsync(OrderSubmissionTestData data, long poolId)
    {
        await new MongoUnitOfWork(fixtureClient(data)).ExecuteAsync(async (tx, ct) => {
            var (head, history, _) = await new OrderStockSourceRecords(data.Database).HistoryAsync(tx, poolId, ct);
            Assert.Equal(head["revision"].AsInt64, history.LongLength);
            Assert.Equal("setup", history[0]["sourceKind"].AsString);
            Assert.Equal(head["setupDecisionId"], history[0]["sourceId"]);
            return true;
        });
    }
    private static MongoClient fixtureClient(OrderSubmissionTestData data) => data.Prices.Catalog.Client;
    private static async Task RequireOpenCountsAsync(OrderSubmissionTestData data, long poolId, long orders, long reservations)
    {
        var pool = await PoolAsync(data, poolId); var location = InventoryGraph.Location(pool["location"].AsBsonDocument);
        var actor = await data.Prices.Catalog.ActorAsync(960000 + poolId, location.ToScope(), Permissions.StockSourceRead);
        foreach (var domain in new[] { false, true })
        {
            var expected = new StockSourceObligationExpectation(poolId, location, pool["revision"].AsInt64, pool["fence"].AsInt64);
            var result = await new MongoUnitOfWork(fixtureClient(data)).ExecuteAsync((tx, ct) => domain
                ? new ReservationStockSourceObligationReader(data.Database, new(fixtureClient(data)), new(data.Database), data.Prices.Catalog.Clock).ReadAndFenceAsync(tx, actor, expected, ct)
                : new OrderStockSourceObligationReader(data.Database, new(fixtureClient(data)), new(data.Database), data.Prices.Catalog.Clock).ReadAndFenceAsync(tx, actor, expected, ct));
            Assert.True(result.Complete); Assert.Equal(expected, result.Pool);
            Assert.Equal((await HeadAsync(data, poolId))["revision"].AsInt64, result.ProducerRevision);
            Assert.Equal(domain ? reservations : orders, result.OpenCount);
        }
    }
    private static Task<List<BsonDocument>> RowsAsync(OrderSubmissionTestData data, string name) => data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
    private static Task<BsonDocument> HeadAsync(OrderSubmissionTestData data, long poolId) => data.Rows(OrderStockSourceRecords.Heads).Find(new BsonDocument("_id", poolId)).SingleAsync();
    private static Task<BsonDocument> PoolAsync(OrderSubmissionTestData data, long poolId) => data.Rows("inventoryPools").Find(new BsonDocument("_id", poolId)).SingleAsync();
    private static Task<BsonDocument> LatestAsync(OrderSubmissionTestData data, long poolId) => data.Rows(OrderStockSourceRecords.Transitions).Find(new BsonDocument("poolId", poolId)).Sort(new BsonDocument("revision", -1)).FirstAsync();
    private static Task<BsonDocument> PhysicalAsync(OrderSubmissionTestData data) => SnapshotAsync(data, ["stockBalances", "movements", "lots", "reservations", "journalEntries"]);
    private static Task<BsonDocument> ProducerEffectsAsync(OrderSubmissionTestData data) => SnapshotAsync(data,
        [OrderStockSourceRecords.Heads, OrderStockSourceRecords.Transitions, "inventoryPools", "stockBalances", "movements", "lots", "reservations", "shipments", "orderDecisions", "auditEvents", "idempotencyRecords", "outboxEvents", "journalEntries"]);
    private static Task<BsonDocument> RollbackStateAsync(OrderSubmissionTestData data) => SnapshotAsync(data,
        [OrderStockSourceRecords.Heads, OrderStockSourceRecords.Transitions, "inventoryPools", "stockBalances", "movements", "lots", "reservations", "orders", "orderSuborders", "shipments", "orderDecisions", "auditEvents", "idempotencyRecords", "outboxEvents", "journalEntries"]);
    private static async Task<BsonDocument> SnapshotAsync(OrderSubmissionTestData data, string[] names)
    {
        var result = new BsonDocument();
        foreach (var name in names) result[name] = new BsonArray(await RowsAsync(data, name));
        return result;
    }
}
