using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentReadTests(MongoFixture fixture)
{
    internal static ShipmentQueryService Service(ShipmentDispatchTestData data) => new(data.Order.Database,
        new(data.Order.Prices.Catalog.Client), data.Order.Prices.Catalog.Clock, data.Order.Service, data.Preparations);

    [Fact]
    public async Task Only_assigned_courier_own_buyer_and_explicit_scoped_readers_see_actual_dispatched_packages() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order; var catalog = order.Prices.Catalog;
        await catalog.Base.AddGrantAsync(order.Buyer, 6510, CatalogAuthoringTestData.Audience, [Permissions.ShipmentRead]);
        var peer = await catalog.ActorAsync(9210, CatalogAuthoringTestData.Audience, Permissions.ShipmentRead);
        var unassigned = await catalog.ActorAsync(9220, new(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103), Permissions.ShipmentRead);
        var otherStore = await catalog.ActorAsync(9230, CatalogAuthoringTestData.OtherOwner, Permissions.ShipmentRead);
        var store = await catalog.ActorAsync(9240, CatalogAuthoringTestData.Shop, Permissions.ShipmentRead);
        var pic = await catalog.ActorAsync(9250, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.ShipmentRead, Permissions.BranchPic);
        var noPic = await catalog.ActorAsync(9260, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.ShipmentRead);
        var sent = await data.DispatchAsync(await data.PreviewAsync(), "read-dispatched"); var id = sent["shipmentId"].AsInt64; var service = Service(data);
        foreach (var actor in new[] { data.Courier, order.Buyer, store, pic }) {
            var page = await service.ListAsync(actor); var item = Assert.Single(page.Items); Assert.False(page.HasMore);
            Assert.Equal(id, item.Id.Value); Assert.Equal("Cabang fixture", item.BuyerName);
            Assert.Equal(1, Assert.Single(item.Lines).Packages); Assert.Equal(12L, item.Lines[0].BaseUnits);
            Assert.Equal(12, item.Lines[0].Definition.BaseUnitsPerPackage);
            Assert.Equal(data.Courier.UserId, item.CourierId.Value);
            Assert.Equal("Alamat cabang sintetis 1", (await service.ReadAsync(actor, id)).Delivery.Address);
            Assert.Empty((await service.ListAsync(actor, id)).Items);
        }
        foreach (var actor in new[] { peer, unassigned, otherStore }) {
            Assert.Empty((await service.ListAsync(actor)).Items);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(actor, id));
        }
        foreach (var actor in new[] { data.Dispatcher, data.Picker, noPic })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListAsync(actor));
        await order.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Courier.UserId), Builders<BsonDocument>.Update.Set("status", "suspended"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.Courier, id));
        Assert.Equal(id, (await service.ReadAsync(order.Buyer, id)).Id.Value);
    }

    [Fact]
    public async Task Cancellation_of_remainder_preserves_dispatch_but_damaged_stock_history_prevents_disclosure() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order;
        var result = await data.DispatchAsync(await data.PreviewAsync(), "read-after-cancel"); var id = result["shipmentId"].AsInt64;
        var cancellation = await order.Service.ReviewOrderAsync(order.Buyer, data.Source.OrderId.Value, null, UnitToko.Modules.Ordering.OrderReviewStage.Cancellation);
        await order.Service.DecideOrderAsync(order.Buyer, data.Source.OrderId.Value, null, UnitToko.Modules.Ordering.OrderReviewStage.Cancellation,
            cancellation.Revision, cancellation.Review, CatalogPriceTestData.Meta("read-cancel-remainder"), action: UnitToko.Modules.Ordering.OrderReviewAction.CancelRemainder);
        var service = Service(data); Assert.Equal(1, Assert.Single((await service.ReadAsync(data.Courier, id)).Lines).Packages);
        await order.Rows("movements").DeleteOneAsync(new BsonDocument("source.namespace", "fulfillment.shipment"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.Courier, id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListAsync(data.Courier));
    }

    [Theory]
    [InlineData(false, "membership")] [InlineData(true, "membership")]
    [InlineData(false, "session")] [InlineData(true, "session")]
    [InlineData(false, "deadline")] [InlineData(true, "deadline")]
    public async Task Courier_revocation_or_session_expiry_during_read_blocks_the_final_response(bool list, string fault) {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order; var catalog = order.Prices.Catalog;
        var id = (await data.DispatchAsync(await data.PreviewAsync(), "read-race-dispatch"))["shipmentId"].AsInt64;
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = 0; var observed = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.Command.GetValue("find", BsonNull.Value) == "shipments" && requestId == 0) requestId = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                    entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Shipment read race did not release");
                }
            });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(order.Database.DatabaseNamespace.DatabaseName);
        var orders = new UnitToko.Platform.Ordering.OrderSubmissionService(database, new(client), new(database), catalog.Clock);
        var reader = new ShipmentQueryService(database, new(client), catalog.Clock, orders, new(database, new(client), new(database), catalog.Clock, orders));
        var pending = Task.Run(async () => { if (list) await reader.ListAsync(data.Courier); else await reader.ReadAsync(data.Courier, id); });
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (fault == "membership") await order.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Courier.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (fault == "session") await catalog.Sessions.RevokeAsync(catalog.Issued[data.Courier.UserId].OpaqueToken, "Synthetic courier read revocation");
            if (fault == "deadline") catalog.Clock.UtcNow = catalog.Clock.UtcNow.AddHours(9);
        } finally { release.Set(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30))); Assert.Equal(1, observed);
    }
}
