using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentDispatchTests(MongoFixture fixture)
{
    [Fact]
    public async Task Partial_dispatch_cancellation_and_original_receipt_recovery_conserve_physical_stock() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order;
        var cancel = await order.Service.ReviewOrderAsync(order.Buyer, data.Source.OrderId.Value, null, OrderReviewStage.Cancellation);
        var preview = await data.PreviewAsync(); var sent = await data.DispatchAsync(preview, "dispatch-one");
        Assert.Equal(sent, await data.DispatchAsync(preview, "dispatch-one"));
        Assert.Equal(sent, await data.Service.RecoverAsync(data.Dispatcher, "dispatch-one"));
        Assert.Null(await data.Service.RecoverAsync(data.Dispatcher, "never-dispatched"));
        var stock = await order.Rows("stockBalances").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(9L, stock["onHand"].AsInt64); Assert.Equal(1L, stock["reserved"].AsInt64);
        var reservation = await order.Rows("reservations").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(1L, reservation["dispatched"].AsInt64); Assert.Equal(1L, reservation["remaining"].AsInt64);
        var shipment = await order.Rows("shipments").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(7000.25m, Decimal128.ToDecimal(shipment["admission"]["lots"][0]["packageCost"].AsDecimal128));
        Assert.Equal(1L, (await order.Service.ReadOwnAsync(order.Buyer, data.Source.OrderId.Value)).Progress.DispatchSequence);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => order.Service.DecideOrderAsync(order.Buyer, data.Source.OrderId.Value, null,
            OrderReviewStage.Cancellation, cancel.Revision, cancel.Review, CatalogPriceTestData.Meta("stale-cancel"), action: OrderReviewAction.CancelRemainder));
        var current = await order.Service.ReviewOrderAsync(order.Buyer, data.Source.OrderId.Value, null, OrderReviewStage.Cancellation);
        Assert.True(current.CanDecide); Assert.NotEqual(cancel.Review, current.Review);
        await order.Service.DecideOrderAsync(order.Buyer, data.Source.OrderId.Value, null, OrderReviewStage.Cancellation, current.Revision, current.Review,
            CatalogPriceTestData.Meta("cancel-one"), action: OrderReviewAction.CancelRemainder);
        Assert.Equal(sent, await data.DispatchAsync(preview, "dispatch-one"));
        Assert.Equal(sent, await data.Service.RecoverAsync(data.Dispatcher, "dispatch-one"));
        stock = await order.Rows("stockBalances").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(9L, stock["onHand"].AsInt64); Assert.Equal(0L, stock["reserved"].AsInt64);
        reservation = await order.Rows("reservations").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(1L, reservation["dispatched"].AsInt64); Assert.Equal(1L, reservation["released"].AsInt64); Assert.Equal(0L, reservation["remaining"].AsInt64);
        Assert.Equal(1, await order.Rows("shipments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(3, await order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await order.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", ShipmentDispatchBson.Operation)));
        Assert.Equal(0, await order.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Second_partial_dispatch_needs_new_preparation_and_exhausts_only_the_reserved_packages() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture);
        var first = await data.PreviewAsync(); var sent = await data.DispatchAsync(first, "first-dispatch");
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.DispatchAsync(first, "repeat-old-preparation"));
        await data.PrepareAsync(1, 1, "second-preparation");
        var second = await data.PreviewAsync(); Assert.NotEqual(first.Review, second.Review);
        var next = await data.DispatchAsync(second, "second-dispatch"); Assert.NotEqual(sent["shipmentId"], next["shipmentId"]);
        Assert.Equal(sent, await data.Service.RecoverAsync(data.Dispatcher, "first-dispatch"));
        var progress = (await data.Order.Service.ReadOwnAsync(data.Order.Buyer, data.Source.OrderId.Value)).Progress;
        Assert.Equal(2L, progress.DispatchSequence); Assert.False(progress.HasRemainingReservation);
        var cancellation = await data.Order.Service.ReviewOrderAsync(data.Order.Buyer, data.Source.OrderId.Value, null, OrderReviewStage.Cancellation);
        Assert.False(cancellation.CanDecide);
        var balance = await data.Order.Rows("stockBalances").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(8L, balance["onHand"].AsInt64); Assert.Equal(0L, balance["reserved"].AsInt64);
        Assert.Equal(3, await data.Order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Missing_real_admission_fails_closed_without_any_stock_or_dispatch_effect() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order;
        var service = new ShipmentDispatchService(order.Database, new(order.Prices.Catalog.Client), new(order.Database), order.Prices.Catalog.Clock, order.Service, data.Preparations);
        Assert.Equal("shipment_dispatch_admission_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => service.PreviewAsync(data.Dispatcher,
            data.Source.SuborderId.Value, data.Prepared.PreparationId.Value, data.Prepared.PreparationRevision, data.Courier.UserId, data.Lots))).Code);
        Assert.Equal(0, await order.Rows("shipments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
    }
}
