using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderFulfillmentSourceTests(MongoFixture fixture)
{
    [Fact]
    public async Task Picker_sees_only_accepted_store_demand_and_explicit_partial_packages() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        var actors = await OrderDecisionTests.Actors(data);
        var picker = await data.Prices.Catalog.ActorAsync(8800, CatalogAuthoringTestData.Shop, Permissions.ShipmentPrepare);
        var id = await OrderDecisionTests.Submit(data);
        Assert.Empty((await data.Service.ListFulfillmentOrdersAsync(picker)).Items);
        var spending = await data.Service.ReviewOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending);
        await data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, spending.Revision, spending.Review, CatalogPriceTestData.Meta("pick-spending"));
        Assert.Empty((await data.Service.ListFulfillmentOrdersAsync(picker)).Items);
        var store = Assert.Single((await data.Service.ReviewListAsync(actors.Store, OrderReviewStage.Store)).Items);
        await data.Service.DecideOrderAsync(actors.Store, id, store.SuborderId, OrderReviewStage.Store, store.Revision, store.Review, CatalogPriceTestData.Meta("pick-accepted"));
        var source = Assert.Single((await data.Service.ListFulfillmentOrdersAsync(picker)).Items);
        var detail = await data.Service.ReadFulfillmentOrderAsync(picker, source.SuborderId.Value);
        Assert.True(detail.CanPrepare); Assert.Equal(source.Review, detail.Review);
        var line = Assert.Single(detail.Lines); Assert.Equal(2, line.RemainingPackages); Assert.Equal(12, line.Package.BaseUnitsPerPackage);
        Assert.Single(ShipmentPreparationPolicy.Require(detail, detail.Review, [new(line.SkuId, 1)]));
        Assert.Throws<UnitToko.Modules.Contracts.DomainValidationException>(() => ShipmentPreparationPolicy.Require(detail, detail.Review, [new(line.SkuId, 3)]));
        Assert.Throws<UnitToko.Modules.Contracts.DomainValidationException>(() => ShipmentPreparationPolicy.Require(detail, detail.Review, [new(line.SkuId, 1), new(line.SkuId, 1)]));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReviewOrderAsync(picker, id, store.SuborderId, OrderReviewStage.Store));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadFulfillmentOrderAsync(data.Buyer, source.SuborderId.Value));
        Assert.Equal(1, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Cancellation_removes_pick_eligibility_and_cannot_reuse_a_previous_source_review() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var actors = await OrderDecisionTests.Actors(data);
        var picker = await data.Prices.Catalog.ActorAsync(8800, CatalogAuthoringTestData.Shop, Permissions.ShipmentPrepare);
        await data.Prices.Catalog.Base.AddGrantAsync(data.Buyer, 6500, CatalogAuthoringTestData.Audience, [Permissions.OrderCancel]);
        var id = await OrderDecisionTests.Submit(data);
        var spending = await data.Service.ReviewOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending);
        await data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, spending.Revision, spending.Review, CatalogPriceTestData.Meta("pick-spending"));
        var store = Assert.Single((await data.Service.ReviewListAsync(actors.Store, OrderReviewStage.Store)).Items);
        await data.Service.DecideOrderAsync(actors.Store, id, store.SuborderId, OrderReviewStage.Store, store.Revision, store.Review, CatalogPriceTestData.Meta("pick-accepted"));
        var before = await data.Service.ReadFulfillmentOrderAsync(picker, store.SuborderId!.Value);
        var cancel = await data.Service.ReviewOrderAsync(data.Buyer, id, null, OrderReviewStage.Cancellation);
        await data.Service.DecideOrderAsync(data.Buyer, id, null, OrderReviewStage.Cancellation, cancel.Revision, cancel.Review, CatalogPriceTestData.Meta("cancel-before-pick"), action: OrderReviewAction.CancelRemainder);
        Assert.Empty((await data.Service.ListFulfillmentOrdersAsync(picker)).Items);
        var after = await data.Service.ReadFulfillmentOrderAsync(picker, before.SuborderId.Value);
        Assert.False(after.CanPrepare); Assert.Equal(0, Assert.Single(after.Lines).RemainingPackages); Assert.NotEqual(before.Review, after.Review);
        Assert.Throws<UnitToko.Modules.Contracts.DomainValidationException>(() => ShipmentPreparationPolicy.Require(after, before.Review, [new(before.Lines[0].SkuId, 1)]));
    }
}
