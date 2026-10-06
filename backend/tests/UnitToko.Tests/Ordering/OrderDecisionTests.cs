using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderDecisionTests(MongoFixture fixture)
{
    [Fact]
    public async Task Each_store_accepts_only_its_own_packages_and_cannot_read_another_stores_part() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture, secondFulfillmentStock: 10); var actors = await Actors(data);
        var other = await data.Prices.Catalog.ActorAsync(8300, CatalogAuthoringTestData.OtherOwner, Permissions.OrderAccept); var id = await Submit(data);
        var branch = await data.Service.ReviewOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending);
        await data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, branch.Revision, branch.Review, CatalogPriceTestData.Meta("approve-both"));
        var first = Assert.Single((await data.Service.ReviewListAsync(actors.Store, OrderReviewStage.Store)).Items);
        var second = Assert.Single((await data.Service.ReviewListAsync(other, OrderReviewStage.Store)).Items);
        Assert.Single(first.Catalog.Lines); Assert.Single(second.Catalog.Lines); Assert.NotEqual(first.SuborderId, second.SuborderId);
        Assert.Equal(24L, first.Catalog.Lines[0].BaseUnits); Assert.Equal(30L, second.Catalog.Lines[0].BaseUnits);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReviewOrderAsync(actors.Store, id, second.SuborderId, OrderReviewStage.Store));
        await data.Service.DecideOrderAsync(actors.Store, id, first.SuborderId, OrderReviewStage.Store, first.Revision, first.Review, CatalogPriceTestData.Meta("first-store"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.DecideOrderAsync(other, id, second.SuborderId, OrderReviewStage.Store, second.Revision, second.Review, CatalogPriceTestData.Meta("stale-second-store")));
        var refreshed = await data.Service.ReviewOrderAsync(other, id, second.SuborderId, OrderReviewStage.Store);
        await data.Service.DecideOrderAsync(other, id, second.SuborderId, OrderReviewStage.Store, refreshed.Revision, refreshed.Review, CatalogPriceTestData.Meta("second-store"));
        var progress = (await data.Service.ReadOwnAsync(data.Buyer, id)).Progress;
        Assert.Equal(4L, progress.Revision); Assert.Equal(2, progress.AcceptedSuborders.Length); Assert.Equal(2, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
    }
    [Theory] [InlineData(OrderReviewAction.Approve)] [InlineData(OrderReviewAction.Reject)]
    public async Task Factor_expiry_after_the_decision_write_aborts_decision_audit_receipt_and_outbox(OrderReviewAction action) {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var actors = await Actors(data); var id = await Submit(data);
        var review = await data.Service.ReviewOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending); var fired = 0;
        var settings = fixture.Client.Settings.Clone();
        settings.ClusterConfigurator = builder => builder.Subscribe<MongoDB.Driver.Core.Events.CommandStartedEvent>(value => {
            if (value.CommandName == "insert" && value.Command["insert"] == "outboxEvents" &&
                value.Command["documents"].AsBsonArray.Any(row => row["kind"] == "ordering.order.decided") && Interlocked.Exchange(ref fired, 1) == 0)
                data.Prices.Catalog.Clock.UtcNow = data.Prices.Catalog.Clock.UtcNow.AddMinutes(6);
        });
        using var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new OrderSubmissionService(db, new(client), new(db), data.Prices.Catalog.Clock);
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideOrderAsync(actors.Approver, id, null,
            OrderReviewStage.Spending, review.Revision, review.Review, CatalogPriceTestData.Meta("expires-final"), action: action))).Code);
        Assert.Equal(1, fired); Assert.Equal(0, await data.Rows("orderDecisions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "ordering.order.decided")));
        Assert.Equal(0, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "ordering.order.decided")));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "expires-final")));
        Assert.Equal(1, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, (await data.Rows("stockBalances").Find(new BsonDocument()).SingleAsync())["reserved"].AsInt64);
    }
    [Theory] [InlineData("receipt")] [InlineData("audit")] [InlineData("changed-review")] [InlineData("extra-child")]
    public async Task Damaged_decision_history_cannot_serve_read_recovery_or_a_replacement_approval(string fault) {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var actors = await Actors(data); var id = await Submit(data);
        var view = await data.Service.ReviewOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending);
        var result = await data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, view.Revision, view.Review, CatalogPriceTestData.Meta("approval"));
        var row = await data.Rows("orderDecisions").Find(new BsonDocument("_id", result["decisionId"])).SingleAsync();
        if (fault == "receipt") await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("key", "approval"));
        if (fault == "audit") await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", row["auditId"]));
        if (fault == "changed-review") await data.Rows("orderDecisions").UpdateOneAsync(new BsonDocument("_id", row["_id"]), Builders<BsonDocument>.Update.Set("review", new string('f', 64)));
        if (fault == "extra-child") await data.Rows("orderDecisions").InsertOneAsync(new BsonDocument { { "_id", 999999L }, { "orderId", id } }, new InsertOneOptions { BypassDocumentValidation = true });
        Assert.NotNull(await Record.ExceptionAsync(() => data.Service.ReadOwnAsync(data.Buyer, id)));
        Assert.NotNull(await Record.ExceptionAsync(() => data.Service.RecoverOrderDecisionAsync(actors.Approver, OrderReviewStage.Spending, "approval")));
        Assert.NotNull(await Record.ExceptionAsync(() => data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, view.Revision, view.Review, CatalogPriceTestData.Meta("replacement"))));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "replacement")));
    }
    [Fact]
    public async Task Different_accounts_for_same_verified_person_cannot_approve_or_recover_approval() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var actors = await Actors(data); var id = await Submit(data);
        var review = await data.Service.ReviewOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending);
        await data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, review.Revision, review.Review, CatalogPriceTestData.Meta("approved-before-link"));
        var maker = data.Buyer.UserId; var checker = actors.Approver.UserId;
        await new MongoUnitOfWork(fixture.Client).ExecuteAsync(async (tx, ct) => {
            await data.Rows("personRecords").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.PersonId(checker)),
                Builders<BsonDocument>.Update.Set("currentEquivalenceId", PersonTestData.GroupId(maker)).Inc("version", 1L), cancellationToken: ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(maker)),
                Builders<BsonDocument>.Update.Set("memberPersonIds", new BsonArray { PersonTestData.PersonId(maker), PersonTestData.PersonId(checker) })
                    .Set("memberUserIds", new BsonArray { maker, checker }).Inc("version", 1L), cancellationToken: ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(checker)),
                Builders<BsonDocument>.Update.Set("status", "superseded").Inc("version", 1L), cancellationToken: ct); return true;
        });
        Assert.Equal("independent_person_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverOrderDecisionAsync(actors.Approver, OrderReviewStage.Spending, "approved-before-link"))).Code);
        var second = await data.Service.PreviewAsync(data.Buyer, data.Input);
        var secondId = (await data.Service.SubmitAsync(data.Buyer, data.Input, second.Review, CatalogPriceTestData.Meta("second-order")))["orderId"].AsInt64;
        var next = await data.Service.ReviewOrderAsync(actors.Approver, secondId, null, OrderReviewStage.Spending);
        Assert.Equal("independent_person_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.DecideOrderAsync(actors.Approver, secondId, null,
            OrderReviewStage.Spending, next.Revision, next.Review, CatalogPriceTestData.Meta("same-person")))).Code);
    }
    internal static async Task<(ActorContext Approver, ActorContext Store)> Actors(OrderSubmissionTestData data) {
        var catalog = data.Prices.Catalog;
        var approver = await catalog.ActorAsync(8100, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.OrderSpendApprove, Permissions.BranchPic);
        var store = await catalog.ActorAsync(8200, CatalogAuthoringTestData.Shop, Permissions.OrderAccept);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, catalog.Clock, data.Buyer.UserId, approver.UserId);
        return (approver, store);
    }
    internal static async Task<long> Submit(OrderSubmissionTestData data) {
        var review = await data.Service.PreviewAsync(data.Buyer, data.Input);
        return (await data.Service.SubmitAsync(data.Buyer, data.Input, review.Review, CatalogPriceTestData.Meta("order")))["orderId"].AsInt64;
    }
    [Fact]
    public async Task Independent_spending_then_store_acceptance_preserve_submission_and_reserved_stock() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var actors = await Actors(data); var id = await Submit(data);
        var initial = await data.Service.RecoverAsync(data.Buyer, "order");
        var storeBefore = Assert.Single((await data.Service.ReviewListAsync(actors.Store, OrderReviewStage.Store)).Items);
        Assert.False(storeBefore.CanDecide); Assert.Null(storeBefore.Total); Assert.Equal("24000.50", storeBefore.Catalog.CatalogSubtotal.ToString());
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.DecideOrderAsync(actors.Store, id, storeBefore.SuborderId, OrderReviewStage.Store,
            storeBefore.Revision, storeBefore.Review, CatalogPriceTestData.Meta("too-early")));
        var spending = Assert.Single((await data.Service.ReviewListAsync(actors.Approver, OrderReviewStage.Spending)).Items);
        Assert.Equal("25500.50", spending.Total!.Value.ToString()); Assert.True(spending.CanDecide);
        var approved = await data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, spending.Revision, spending.Review, CatalogPriceTestData.Meta("approve"));
        var store = await data.Service.ReviewOrderAsync(actors.Store, id, storeBefore.SuborderId, OrderReviewStage.Store);
        Assert.True(store.CanDecide); Assert.Equal(OrderSpendingState.Approved, store.SpendingState);
        var accepted = await data.Service.DecideOrderAsync(actors.Store, id, store.SuborderId, OrderReviewStage.Store, store.Revision, store.Review, CatalogPriceTestData.Meta("accept"));
        Assert.Equal("accepted", accepted["state"].AsString);
        Assert.Equal(approved, await data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, spending.Revision, spending.Review, CatalogPriceTestData.Meta("approve")));
        Assert.Equal(approved, await data.Service.RecoverOrderDecisionAsync(actors.Approver, OrderReviewStage.Spending, "approve"));
        Assert.Equal(initial, await data.Service.RecoverAsync(data.Buyer, "order"));
        Assert.Equal("accepted", (await data.Service.ReviewOrderAsync(actors.Store, id, store.SuborderId, OrderReviewStage.Store)).StoreState);
        Assert.Equal(2, await data.Rows("orderDecisions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, (await data.Rows("stockBalances").Find(new BsonDocument()).SingleAsync())["reserved"].AsInt64);
        await data.Service.ReadOwnAsync(data.Buyer, id);
    }
    [Fact]
    public async Task Buyer_cannot_approve_self_after_receiving_branch_approval_permission() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); _ = await Actors(data); var id = await Submit(data);
        await data.Prices.Catalog.Base.AddGrantAsync(data.Buyer, 6300, new(GrantScopeKind.BuyingBranch, 10, 11), [Permissions.OrderSpendApprove, Permissions.BranchPic]);
        var review = await data.Service.ReviewOrderAsync(data.Buyer, id, null, OrderReviewStage.Spending);
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.DecideOrderAsync(data.Buyer, id, null, OrderReviewStage.Spending,
            review.Revision, review.Review, CatalogPriceTestData.Meta("self")));
        Assert.Equal("independent_actor_required", denied.Code);
        Assert.Equal(0, await data.Rows("orderDecisions").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Other_store_and_other_branch_do_not_discover_order_rows_or_detail() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var actors = await Actors(data); var id = await Submit(data);
        var other = await data.Prices.Catalog.ActorAsync(8300, CatalogAuthoringTestData.OtherOwner, Permissions.OrderAccept);
        var store = Assert.Single((await data.Service.ReviewListAsync(actors.Store, OrderReviewStage.Store)).Items);
        Assert.Empty((await data.Service.ReviewListAsync(other, OrderReviewStage.Store)).Items);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReviewOrderAsync(other, id, store.SuborderId, OrderReviewStage.Store));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReviewOrderAsync(actors.Store, id, null, OrderReviewStage.Spending));
    }
    [Fact]
    public async Task Two_reviewers_have_one_decision_and_stale_or_reused_request_is_not_a_second_approval() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var actors = await Actors(data); var id = await Submit(data);
        var second = await data.Prices.Catalog.ActorAsync(8400, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.OrderSpendApprove, Permissions.BranchPic);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Prices.Catalog.Clock, second.UserId);
        var review = await data.Service.ReviewOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending);
        var errors = await Task.WhenAll(new[] { actors.Approver, second }.Select(actor => Record.ExceptionAsync(() => data.Service.DecideOrderAsync(actor, id, null,
            OrderReviewStage.Spending, review.Revision, review.Review, CatalogPriceTestData.Meta("race")))));
        Assert.Single(errors, error => error is null); Assert.IsType<PersistenceConflictException>(Assert.Single(errors, error => error is not null));
        Assert.Equal(1, await data.Rows("orderDecisions").CountDocumentsAsync(new BsonDocument()));
    }
}
