using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentDispatchReadTests(MongoFixture fixture)
{
    [Fact]
    public async Task Queue_and_courier_choices_stay_within_current_store_scope_and_membership() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order; var catalog = order.Prices.Catalog;
        var outsider = await catalog.ActorAsync(8950, CatalogAuthoringTestData.OtherOwner, Permissions.ShipmentDispatch, Permissions.ShipmentAssign);
        var foreignCourier = await catalog.ActorAsync(9200, new(GrantScopeKind.CourierAssignment, 100, ShopId: 201, ChannelId: 203), Permissions.ShipmentRead, Permissions.DeliveryAttempt);
        await order.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", foreignCourier.UserId }, { "schemaVersion", 1 }, { "version", 1L },
            { "displayName", "Kurir toko lain" }, { "verifiedEmail", "outside@example.invalid" }, { "verifiedAt", catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", catalog.Clock.UtcNow.UtcDateTime } });
        var queue = await data.Service.ListAsync(data.Dispatcher); Assert.True(Assert.Single(queue.Items).CanDispatch); Assert.False(queue.HasMore);
        Assert.Empty((await data.Service.ListAsync(outsider)).Items);
        Assert.Empty((await data.Service.ListAsync(data.Dispatcher, data.Prepared.PreparationId.Value)).Items);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadPreparationAsync(outsider, data.Source.SuborderId.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ListCouriersAsync(outsider, data.Source.SuborderId.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ListAsync(data.Picker));
        Assert.Equal(data.Courier.UserId, Assert.Single((await data.Service.ListCouriersAsync(data.Dispatcher, data.Source.SuborderId.Value)).Items).UserId.Value);
        var withoutAssign = await catalog.ActorAsync(8960, CatalogAuthoringTestData.Shop, Permissions.ShipmentDispatch);
        Assert.Single((await data.Service.ListAsync(withoutAssign)).Items);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ListCouriersAsync(withoutAssign, data.Source.SuborderId.Value));
        await order.Rows("memberships").UpdateOneAsync(new BsonDocument("userId", data.Courier.UserId), Builders<BsonDocument>.Update.Set("status", "suspended"));
        Assert.Empty((await data.Service.ListCouriersAsync(data.Dispatcher, data.Source.SuborderId.Value)).Items);
    }

    [Fact]
    public async Task Source_cost_or_courier_change_requires_new_review_before_stock_can_leave() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order;
        var review = await data.PreviewAsync();
        await order.Rows("fixtureDispatchApprovals").UpdateOneAsync(new BsonDocument("_id", 9400L), Builders<BsonDocument>.Update.Set("packageCost", new BsonDecimal128(7100m)).Inc("revision", 1L));
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => data.DispatchAsync(review, "stale-cost"));
        var refreshed = await data.PreviewAsync(); Assert.NotEqual(review.Review, refreshed.Review);
        await order.Rows("accountProfiles").UpdateOneAsync(new BsonDocument("_id", data.Courier.UserId), Builders<BsonDocument>.Update.Set("displayName", "Kurir diperbarui").Inc("version", 1L));
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => data.DispatchAsync(refreshed, "stale-courier"));
        Assert.Equal(0, await order.Rows("shipments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
    }
}
