using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentDispatchIntegrityTests(MongoFixture fixture)
{
    [Fact]
    public async Task Competing_dispatch_requests_cannot_consume_one_preparation_twice() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var review = await data.PreviewAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async index => {
            try { await data.DispatchAsync(review, "dispatch-race-" + index); return true; }
            catch (PersistenceConflictException) { return false; }
        }));
        Assert.Single(results, result => result);
        Assert.Equal(1, await data.Order.Rows("shipments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        var balance = await data.Order.Rows("stockBalances").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(9L, balance["onHand"].AsInt64); Assert.Equal(1L, balance["reserved"].AsInt64);
        var counter = await data.Order.Rows("idCounters").Find(new BsonDocument("_id", new BsonRegularExpression(@"^shipment-number\."))).SingleAsync();
        Assert.Equal(1L, counter["value"].AsInt64);
    }

    [Fact]
    public async Task Cancellation_and_dispatch_race_preserves_one_coherent_reservation_history() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var review = await data.PreviewAsync(); var order = data.Order;
        var cancel = await order.Service.ReviewOrderAsync(order.Buyer, data.Source.OrderId.Value, null, OrderReviewStage.Cancellation);
        async Task<bool> Attempt(Func<Task> action) {
            try { await action(); return true; }
            catch (PersistenceConflictException) { return false; }
        }
        var outcomes = await Task.WhenAll(Attempt(() => data.DispatchAsync(review, "dispatch-racing-cancel")), Attempt(() =>
            order.Service.DecideOrderAsync(order.Buyer, data.Source.OrderId.Value, null, OrderReviewStage.Cancellation, cancel.Revision, cancel.Review,
                CatalogPriceTestData.Meta("cancel-racing-dispatch"), action: OrderReviewAction.CancelRemainder)));
        Assert.Single(outcomes, result => result);
        var progress = (await order.Service.ReadOwnAsync(order.Buyer, data.Source.OrderId.Value)).Progress;
        var reservation = await order.Rows("reservations").Find(new BsonDocument()).SingleAsync();
        var stock = await order.Rows("stockBalances").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(2L, reservation["remaining"].AsInt64 + reservation["released"].AsInt64 + reservation["dispatched"].AsInt64);
        Assert.Equal(10L - reservation["dispatched"].AsInt64, stock["onHand"].AsInt64); Assert.Equal(reservation["remaining"], stock["reserved"]);
        Assert.Equal(outcomes[1], progress.Cancelled);
        Assert.Equal(outcomes[0] ? 1 : 0, await order.Rows("shipments").CountDocumentsAsync(new BsonDocument()));
    }

    [Theory] [InlineData("source")] [InlineData("dispatcher")] [InlineData("courier")]
    public async Task Authority_expiring_after_custody_write_rolls_back_every_dispatch_effect(string fault) {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order; var clock = order.Prices.Catalog.Clock;
        var expires = clock.UtcNow.AddMinutes(1).UtcDateTime;
        if (fault == "source") await order.Rows("fixtureDispatchApprovals").UpdateOneAsync(new BsonDocument("_id", 9400L), Builders<BsonDocument>.Update.Set("expiresAt", expires));
        else await order.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", fault == "dispatcher" ? data.Dispatcher.UserId : data.Courier.UserId), Builders<BsonDocument>.Update.Set("expiresAt", expires));
        var review = await data.PreviewAsync(); var fired = false;
        var head = await order.Rows(FulfillmentStockSourceRecords.Heads).Find(new BsonDocument()).SingleAsync();
        var pool = await order.Rows("inventoryPools").Find(new BsonDocument()).SingleAsync();
        data.Admission.BeforeComplete = async (tx, ct) => {
            if (!fired && await order.Rows("idempotencyRecords").Find(tx, new BsonDocument {
                    { "key", "expires-after-receipt" }, { "result.shipmentId", new BsonDocument("$exists", true) } }).AnyAsync(ct) &&
                await order.Rows(FulfillmentStockSourceRecords.Transitions).Find(tx, new BsonDocument("sourceKind", "dispatch")).AnyAsync(ct)) {
                fired = true; clock.UtcNow = clock.UtcNow.AddMinutes(2);
            }
        };
        var error = await Record.ExceptionAsync(() => data.DispatchAsync(review, "expires-after-receipt"));
        Assert.True(fired); Assert.True(error is DomainValidationException or AuthorizationDeniedException, error?.ToString());
        Assert.Equal(0, await order.Rows("shipments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await order.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.shipment.dispatched")));
        Assert.Equal(0, await order.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.shipment.dispatched")));
        Assert.Equal(0, await order.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "expires-after-receipt")));
        Assert.Equal(0, await order.Rows("idCounters").CountDocumentsAsync(new BsonDocument("_id", new BsonRegularExpression(@"^shipment-number\."))));
        Assert.Equal(1, await order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        var stock = await order.Rows("stockBalances").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(10L, stock["onHand"].AsInt64); Assert.Equal(2L, stock["reserved"].AsInt64);
        var reservation = await order.Rows("reservations").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(2L, reservation["remaining"].AsInt64); Assert.Equal(0L, reservation["dispatched"].AsInt64);
        Assert.Equal(head, await order.Rows(FulfillmentStockSourceRecords.Heads).Find(new BsonDocument()).SingleAsync());
        Assert.Equal(pool, await order.Rows("inventoryPools").Find(new BsonDocument()).SingleAsync());
    }

    [Theory] [InlineData("shipment")] [InlineData("receipt")] [InlineData("audit")] [InlineData("movement")]
    public async Task Damaged_accepted_dispatch_cannot_be_read_recovered_or_replaced(string fault) {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order;
        var review = await data.PreviewAsync(); await data.DispatchAsync(review, "original-dispatch");
        var shipment = await order.Rows("shipments").Find(new BsonDocument()).SingleAsync();
        if (fault == "shipment") await order.Rows("shipments").DeleteOneAsync(new BsonDocument("_id", shipment["_id"]));
        if (fault == "receipt") await order.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("key", "original-dispatch"));
        if (fault == "audit") await order.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", shipment["auditId"]));
        if (fault == "movement") await order.Rows("movements").DeleteOneAsync(new BsonDocument("source.namespace", "fulfillment.shipment"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => order.Service.ReadOwnAsync(order.Buyer, data.Source.OrderId.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.DispatchAsync(review, "replacement-dispatch"));
        // A missing receipt must not be converted into permission to resubmit stock changes.
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Dispatcher, "original-dispatch"));
        Assert.Equal(0, await order.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "replacement-dispatch")));
        var stock = await order.Rows("stockBalances").Find(new BsonDocument()).SingleAsync(); Assert.Equal(9L, stock["onHand"].AsInt64);
    }
}
