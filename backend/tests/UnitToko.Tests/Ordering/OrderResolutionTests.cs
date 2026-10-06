using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderResolutionTests(MongoFixture fixture)
{
    [Fact]
    public async Task Cancellation_and_acceptance_race_rechecks_the_reviewed_revision_and_never_double_releases() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var actors = await OrderDecisionTests.Actors(data);
        await data.Prices.Catalog.Base.AddGrantAsync(data.Buyer, 6500, CatalogAuthoringTestData.Audience, [Permissions.OrderCancel]);
        var id = await OrderDecisionTests.Submit(data); var spending = await data.Service.ReviewOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending);
        await data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, spending.Revision, spending.Review, CatalogPriceTestData.Meta("approve"));
        var store = Assert.Single((await data.Service.ReviewListAsync(actors.Store, OrderReviewStage.Store)).Items);
        var cancel = await data.Service.ReviewOrderAsync(data.Buyer, id, null, OrderReviewStage.Cancellation);
        var errors = await Task.WhenAll(Record.ExceptionAsync(() => data.Service.DecideOrderAsync(actors.Store, id, store.SuborderId, OrderReviewStage.Store, store.Revision, store.Review, CatalogPriceTestData.Meta("accept-race"))),
            Record.ExceptionAsync(() => data.Service.DecideOrderAsync(data.Buyer, id, null, OrderReviewStage.Cancellation, cancel.Revision, cancel.Review, CatalogPriceTestData.Meta("cancel-race"), action: OrderReviewAction.CancelRemainder)));
        Assert.Single(errors, error => error is null); Assert.IsType<PersistenceConflictException>(Assert.Single(errors, error => error is not null));
        var state = (await data.Service.ReadOwnAsync(data.Buyer, id)).Progress;
        Assert.Equal(3L, state.Revision);
        Assert.Equal(state.Cancelled ? 0L : 2L, (await data.Rows("stockBalances").Find(new BsonDocument()).SingleAsync())["reserved"].AsInt64);
        if (!state.Cancelled) {
            var current = await data.Service.ReviewOrderAsync(data.Buyer, id, null, OrderReviewStage.Cancellation);
            await data.Service.DecideOrderAsync(data.Buyer, id, null, OrderReviewStage.Cancellation, current.Revision, current.Review, CatalogPriceTestData.Meta("cancel-reviewed"), action: OrderReviewAction.CancelRemainder);
        }
        Assert.Equal(2, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, (await data.Rows("stockBalances").Find(new BsonDocument()).SingleAsync())["reserved"].AsInt64);
    }
    [Theory] [InlineData("movement")] [InlineData("reservation")]
    public async Task Damaged_release_evidence_blocks_recovery_instead_of_claiming_stock_was_released(string fault) {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var actors = await OrderDecisionTests.Actors(data); var id = await OrderDecisionTests.Submit(data);
        var review = await data.Service.ReviewOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending);
        await data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, review.Revision, review.Review, CatalogPriceTestData.Meta("reject"), action: OrderReviewAction.Reject);
        if (fault == "movement") await data.Rows("movements").DeleteOneAsync(new BsonDocument("source.namespace", "ordering.decision"));
        else await data.Rows("reservations").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("remaining", 2L).Set("released", 0L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverOrderDecisionAsync(actors.Approver, OrderReviewStage.Spending, "reject", action: OrderReviewAction.Reject));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadOwnAsync(data.Buyer, id));
    }
    [Fact]
    public async Task Branch_rejection_releases_both_sources_with_original_receipt_and_blocks_store_acceptance() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture, secondFulfillmentStock: 10); var actors = await OrderDecisionTests.Actors(data);
        var id = await OrderDecisionTests.Submit(data); var review = await data.Service.ReviewOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending);
        var result = await data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, review.Revision, review.Review, CatalogPriceTestData.Meta("reject"), action: OrderReviewAction.Reject);
        Assert.Equal("rejected", result["state"].AsString);
        Assert.Equal(result, await data.Service.RecoverOrderDecisionAsync(actors.Approver, OrderReviewStage.Spending, "reject", action: OrderReviewAction.Reject));
        Assert.Equal(result, await data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, review.Revision, review.Review, CatalogPriceTestData.Meta("reject"), action: OrderReviewAction.Reject));
        Assert.All(await data.Rows("stockBalances").Find(new BsonDocument()).ToListAsync(), row => { Assert.Equal(0L, row["reserved"].AsInt64); Assert.Equal(10L, row["onHand"].AsInt64); });
        Assert.Equal(4, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.All(await data.Rows("reservations").Find(new BsonDocument()).ToListAsync(), row => { Assert.Equal(0L, row["remaining"].AsInt64); Assert.Equal(row["allocation"]["packages"], row["released"]); });
        var own = await data.Service.ReadOwnAsync(data.Buyer, id); Assert.Equal(OrderSpendingState.Rejected, own.Progress.SpendingState);
        Assert.False(Assert.Single((await data.Service.ReviewListAsync(actors.Store, OrderReviewStage.Store)).Items).CanDecide);
        Assert.NotNull(await data.Service.RecoverAsync(data.Buyer, "order"));
    }
    [Fact]
    public async Task Store_rejects_only_its_part_then_buyer_cancels_the_other_part_without_double_release() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture, secondFulfillmentStock: 10); var actors = await OrderDecisionTests.Actors(data);
        await data.Prices.Catalog.Base.AddGrantAsync(data.Buyer, 6500, CatalogAuthoringTestData.Audience, [Permissions.OrderCancel]);
        var id = await OrderDecisionTests.Submit(data); var spending = await data.Service.ReviewOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending);
        await data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, spending.Revision, spending.Review, CatalogPriceTestData.Meta("approve"));
        var store = Assert.Single((await data.Service.ReviewListAsync(actors.Store, OrderReviewStage.Store)).Items);
        await data.Service.DecideOrderAsync(actors.Store, id, store.SuborderId, OrderReviewStage.Store, store.Revision, store.Review, CatalogPriceTestData.Meta("store-reject"), action: OrderReviewAction.Reject);
        Assert.Equal(new long[] { 0, 3 }, (await data.Rows("stockBalances").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Select(row => row["reserved"].AsInt64));
        var cancel = await data.Service.ReviewOrderAsync(data.Buyer, id, null, OrderReviewStage.Cancellation);
        await data.Service.DecideOrderAsync(data.Buyer, id, null, OrderReviewStage.Cancellation, cancel.Revision, cancel.Review, CatalogPriceTestData.Meta("buyer-cancel"), action: OrderReviewAction.CancelRemainder);
        Assert.All(await data.Rows("stockBalances").Find(new BsonDocument()).ToListAsync(), row => Assert.Equal(0L, row["reserved"].AsInt64));
        Assert.Equal(4, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        var view = await data.Service.ReadOwnAsync(data.Buyer, id); Assert.True(view.Progress.Cancelled); Assert.Single(view.Progress.RejectedSuborders);
        Assert.False((await data.Service.ReviewOrderAsync(data.Buyer, id, null, OrderReviewStage.Cancellation)).CanDecide);
        Assert.Equal(0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Failed_rejection_outbox_rolls_back_all_releases_and_decision() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture, secondFulfillmentStock: 10); var actors = await OrderDecisionTests.Actors(data);
        var id = await OrderDecisionTests.Submit(data); var review = await data.Service.ReviewOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending);
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" }, { "validator", new BsonDocument("kind", new BsonDocument("$ne", "ordering.order.decided")) } });
        Assert.NotNull(await Record.ExceptionAsync(() => data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, review.Revision, review.Review,
            CatalogPriceTestData.Meta("failed-reject"), action: OrderReviewAction.Reject)));
        Assert.Equal(new long[] { 2, 3 }, (await data.Rows("stockBalances").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Select(row => row["reserved"].AsInt64));
        Assert.Equal(2, await data.Rows("movements").CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows("orderDecisions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "failed-reject")));
    }
    [Fact]
    public async Task Cancellation_permission_does_not_expose_other_buyers_orders() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var id = await OrderDecisionTests.Submit(data);
        var other = await data.Prices.Catalog.ActorAsync(8600, CatalogAuthoringTestData.Audience, Permissions.OrderCancel);
        Assert.Empty((await data.Service.ReviewListAsync(other, OrderReviewStage.Cancellation)).Items);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReviewOrderAsync(other, id, null, OrderReviewStage.Cancellation));
    }
}
