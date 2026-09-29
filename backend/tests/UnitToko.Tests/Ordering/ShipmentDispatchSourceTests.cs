using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentDispatchSourceTests(MongoFixture fixture)
{
    [Fact]
    public void Fulfillment_owners_accept_separate_handles_for_the_same_database_but_reject_another_database_or_client() {
        var client = new MongoClient("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        var database = client.GetDatabase("dispatch_owner_boundary");
        var same = client.GetDatabase("dispatch_owner_boundary");
        var clock = new UnitToko.Tests.Authorization.MutableAuthorizationClock(DateTimeOffset.Parse("2026-09-30T00:00:00Z"));
        var orders = new OrderSubmissionService(database, new(client), new(database), clock);
        var preparations = new ShipmentPreparationService(same, new(client), new(same), clock, orders);
        _ = new ShipmentDispatchService(database, new(client), new(database), clock, orders, preparations);
        var other = client.GetDatabase("dispatch_owner_other");
        Assert.Throws<ArgumentException>(() => new ShipmentPreparationService(other, new(client), new(other), clock, orders));
        Assert.Throws<ArgumentException>(() => new ShipmentDispatchService(other, new(client), new(other), clock, orders, preparations));
        var otherClient = new MongoClient("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        var disconnected = otherClient.GetDatabase(database.DatabaseNamespace.DatabaseName);
        Assert.Throws<ArgumentException>(() => new ShipmentPreparationService(disconnected, new(otherClient), new(disconnected), clock, orders));
        Assert.Throws<ArgumentException>(() => new ShipmentDispatchService(disconnected, new(otherClient), new(disconnected), clock, orders, preparations));
    }

    [Fact]
    public async Task Dispatch_preparation_requires_separate_authority_and_exact_reserved_lot_selection() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var ready = await ShipmentPreparationTests.Ready(data);
        var preparations = ShipmentPreparationTests.Service(data);
        var saved = await preparations.SaveAsync(ready.Picker, ready.Source.SuborderId.Value, 0, ready.Source.Review,
            [new(ready.Source.Lines[0].SkuId, 1)], CatalogPriceTestData.Meta("dispatch-preparation"));
        var dispatcher = await data.Prices.Catalog.ActorAsync(8900, CatalogAuthoringTestData.Shop, Permissions.ShipmentDispatch);
        var authorization = new AuthorizationStore(data.Database, new(data.Prices.Catalog.Client), data.Prices.Catalog.Clock);
        Task<PreparedShipment> Read(ActorContext actor) => authorization.ReadAsync(actor, async (snapshot, tx, ct) => {
            var result = await preparations.ReadForDispatchAsync(actor, snapshot, tx, ready.Source.SuborderId.Value, ct);
            await data.Service.FenceFulfillmentAsync(actor, snapshot, tx, [result.Source.Order], Permissions.ShipmentDispatch, ct);
            return result;
        }, default);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Read(ready.Picker));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Read(data.Buyer));
        var read = await Read(dispatcher); Assert.Equal(saved["versionId"], read.Version["_id"]);
        var reservation = Assert.Single(read.Source.Reservations); Assert.Equal(202L, reservation.Fact.LotId.Value);
        var facts = OrderSubmissionService.DispatchAllocationFacts(read.Source, [new(ready.Source.Lines[0].SkuId, 1)], [new(reservation.Fact.ReservationId, 1)]);
        var leg = Assert.Single(facts).AsBsonDocument;
        OrderDispatchFacts.Require(reservation.Reservation, leg, false);
        var after = reservation.Reservation.DeepClone().AsBsonDocument;
        after["remaining"] = 1L; after["dispatched"] = 1L; after["revision"] = 2L;
        OrderDispatchFacts.Require(after, leg, true);
        after["released"] = 1L; Assert.Throws<AuthorizationDeniedException>(() => OrderDispatchFacts.Require(after, leg, true));
        Assert.Throws<UnitToko.Modules.Contracts.DomainValidationException>(() => OrderSubmissionService.DispatchAllocationFacts(read.Source,
            [new(ready.Source.Lines[0].SkuId, 1)], [new(reservation.Fact.ReservationId, 2)]));
        Assert.Equal(1, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("shipments").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Stale_saved_preparation_cannot_supply_dispatch_after_order_cancellation() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var ready = await ShipmentPreparationTests.Ready(data);
        var preparations = ShipmentPreparationTests.Service(data);
        await preparations.SaveAsync(ready.Picker, ready.Source.SuborderId.Value, 0, ready.Source.Review,
            [new(ready.Source.Lines[0].SkuId, 1)], CatalogPriceTestData.Meta("stale-dispatch-preparation"));
        var dispatcher = await data.Prices.Catalog.ActorAsync(8900, CatalogAuthoringTestData.Shop, Permissions.ShipmentDispatch);
        await data.Prices.Catalog.Base.AddGrantAsync(data.Buyer, 6500, CatalogAuthoringTestData.Audience, [Permissions.OrderCancel]);
        var review = await data.Service.ReviewOrderAsync(data.Buyer, ready.Source.OrderId.Value, null, UnitToko.Modules.Ordering.OrderReviewStage.Cancellation);
        await data.Service.DecideOrderAsync(data.Buyer, ready.Source.OrderId.Value, null, UnitToko.Modules.Ordering.OrderReviewStage.Cancellation,
            review.Revision, review.Review, CatalogPriceTestData.Meta("cancel-before-dispatch"), action: UnitToko.Modules.Ordering.OrderReviewAction.CancelRemainder);
        var authorization = new AuthorizationStore(data.Database, new(data.Prices.Catalog.Client), data.Prices.Catalog.Clock);
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => authorization.ReadAsync(dispatcher,
            (snapshot, tx, ct) => preparations.ReadForDispatchAsync(dispatcher, snapshot, tx, ready.Source.SuborderId.Value, ct), default));
        Assert.Equal(0, await data.Rows("shipments").CountDocumentsAsync(new BsonDocument()));
    }
}
