using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentCourierEligibilityTests(MongoFixture fixture)
{
    [Fact]
    public async Task Courier_target_needs_both_delivery_permissions_and_approved_scoped_membership_without_impersonating_a_session() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var ready = await ShipmentPreparationTests.Ready(data);
        var dispatcher = await data.Prices.Catalog.ActorAsync(8900, CatalogAuthoringTestData.Shop, Permissions.ShipmentDispatch, Permissions.ShipmentAssign);
        var scope = new GrantScope(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103);
        var courier = await data.Prices.Catalog.ActorAsync(9100, scope, Permissions.ShipmentRead, Permissions.DeliveryAttempt);
        var wrong = await data.Prices.Catalog.ActorAsync(9200, scope with { ShopId = 201, ChannelId = 203 }, Permissions.ShipmentRead, Permissions.DeliveryAttempt);
        foreach (var actor in new[] { courier, wrong })
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Kurir fixture " + actor.UserId }, { "verifiedEmail", actor.UserId + "@example.invalid" },
                { "verifiedAt", data.Prices.Catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Prices.Catalog.Clock.UtcNow.UtcDateTime } });
        var authorization = new AuthorizationStore(data.Database, new(data.Prices.Catalog.Client), data.Prices.Catalog.Clock);
        var eligibility = new ShipmentCourierEligibility(data.Database, authorization, data.Prices.Catalog.Clock);
        var resource = new ResourceScope(BuyingOrganizationId: 10, BranchId: 11, FulfillmentOrganizationId: 100, ShopId: 101, ChannelId: 103, CreatedBy: data.Buyer.UserId);
        Task<ShipmentCourierTarget> Read(long userId) => authorization.ReadAsync(dispatcher, async (snapshot, tx, ct) => {
            var target = await eligibility.ReadAsync(dispatcher, snapshot, tx, resource, userId, ct);
            await eligibility.FenceAsync(dispatcher, snapshot, tx, resource, target, ct); return target;
        }, default);
        var target = await Read(courier.UserId); Assert.Equal(courier.UserId, target.UserId); Assert.Equal("Kurir fixture 9100", target.Name);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Read(wrong.UserId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => authorization.ReadAsync(ready.Picker,
            (snapshot, tx, ct) => eligibility.ReadAsync(ready.Picker, snapshot, tx, resource, courier.UserId, ct), default));
        // Being assigned does not require a fictitious live session. The courier must log in for later delivery actions.
        await data.Rows("identitySessions").DeleteManyAsync(new BsonDocument("userId", courier.UserId));
        Assert.Equal(courier.UserId, (await Read(courier.UserId)).UserId);
        var grant = await data.Rows("authorizationGrants").Find(new BsonDocument("userId", courier.UserId)).SingleAsync();
        await data.Rows("authorizationGrants").UpdateOneAsync(grant, Builders<BsonDocument>.Update.Set("permissions", new BsonArray { Permissions.ShipmentRead }));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Read(courier.UserId));
        await data.Rows("authorizationGrants").ReplaceOneAsync(new BsonDocument("_id", grant["_id"]), grant);
        await data.Rows("memberships").UpdateOneAsync(new BsonDocument("userId", courier.UserId), Builders<BsonDocument>.Update.Set("status", "suspended"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Read(courier.UserId));
    }

    [Fact]
    public async Task Expired_target_grant_is_rejected_at_final_assignment_fence() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        var dispatcher = await data.Prices.Catalog.ActorAsync(8900, CatalogAuthoringTestData.Shop, Permissions.ShipmentDispatch, Permissions.ShipmentAssign);
        var courier = await data.Prices.Catalog.ActorAsync(9100, new(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103), Permissions.ShipmentRead, Permissions.DeliveryAttempt);
        await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", courier.UserId }, { "schemaVersion", 1 }, { "version", 1L },
            { "displayName", "Kurir fixture" }, { "verifiedEmail", "courier@example.invalid" }, { "verifiedAt", data.Prices.Catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Prices.Catalog.Clock.UtcNow.UtcDateTime } });
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("userId", courier.UserId),
            Builders<BsonDocument>.Update.Set("expiresAt", data.Prices.Catalog.Clock.UtcNow.AddMinutes(1).UtcDateTime));
        var authorization = new AuthorizationStore(data.Database, new(data.Prices.Catalog.Client), data.Prices.Catalog.Clock);
        var eligibility = new ShipmentCourierEligibility(data.Database, authorization, data.Prices.Catalog.Clock);
        var resource = new ResourceScope(FulfillmentOrganizationId: 100, ShopId: 101, ChannelId: 103);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => authorization.ReadAsync(dispatcher, async (snapshot, tx, ct) => {
            var target = await eligibility.ReadAsync(dispatcher, snapshot, tx, resource, courier.UserId, ct);
            data.Prices.Catalog.Clock.UtcNow = data.Prices.Catalog.Clock.UtcNow.AddMinutes(2);
            await eligibility.FenceAsync(dispatcher, snapshot, tx, resource, target, ct); return true;
        }, default));
        Assert.Equal(0, await data.Rows("shipments").CountDocumentsAsync(new BsonDocument()));
    }
}
