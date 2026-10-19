using MongoDB.Bson;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Work;

namespace UnitToko.Tests.Work;

public sealed class WorkSourcesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Payment_reviews_count_unassigned_and_own_assignments_only() {
        var work = PaymentReviewWorkSource.Summarize([("pending", null, T0.AddHours(2)), ("assigned", 7, T0.AddHours(1)), ("assigned", 99, T0),
            ("rejected", null, T0), ("replacement_requested", null, T0)], hasMore: false, actorId: 7);
        Assert.NotNull(work);
        Assert.Equal((2, false, T0.AddHours(1)), (work.Count, work.HasMore, work.OldestAt));
    }

    // The queue is newest-first and mixes finished items: an empty first page with more pages is "to be checked", never "nothing waiting".
    [Fact]
    public void Payment_reviews_with_nothing_waiting_on_page_one_but_more_pages_are_to_be_checked() {
        var work = PaymentReviewWorkSource.Summarize([("assigned", 99, T0)], hasMore: true, actorId: 7);
        Assert.Equal((0, true, (DateTimeOffset?)null), (work!.Count, work.HasMore, work.OldestAt));
        Assert.Null(PaymentReviewWorkSource.Summarize([("assigned", 99, T0)], hasMore: false, actorId: 7));
    }

    [Fact]
    public void A_full_page_without_more_is_exact_not_plus() {
        var work = PaymentReviewWorkSource.Summarize(Enumerable.Range(0, 25).Select(i => ("pending", (long?)null, (DateTimeOffset?)T0.AddMinutes(i))), hasMore: false, actorId: 7);
        Assert.Equal((25, false, T0), (work!.Count, work.HasMore, work.OldestAt));
        Assert.True(PaymentReviewWorkSource.Summarize([("pending", null, T0)], hasMore: true, actorId: 7)!.HasMore);
    }

    [Fact]
    public void Incoming_requests_count_pending_only_and_use_the_oldest_recorded_time() {
        BsonDocument Request(string state, DateTimeOffset at) => new() { { "state", state }, { "recordedAt", at.UtcDateTime } };
        var page = new BsonDocument { { "items", new BsonArray { Request("pending", T0.AddHours(3)), Request("accepted", T0), Request("pending", T0.AddHours(1)) } }, { "nextRequestId", BsonNull.Value } };
        var work = IncomingRequestWorkSource.Summarize(page);
        Assert.Equal((2, false, T0.AddHours(1)), (work!.Count, work.HasMore, work.OldestAt));
        page["nextRequestId"] = 41L;
        Assert.True(IncomingRequestWorkSource.Summarize(page)!.HasMore);
        Assert.Null(IncomingRequestWorkSource.Summarize(new BsonDocument { { "items", new BsonArray { Request("rejected", T0) } }, { "nextRequestId", BsonNull.Value } }));
        var backlog = IncomingRequestWorkSource.Summarize(new BsonDocument { { "items", new BsonArray { Request("rejected", T0) } }, { "nextRequestId", 9L } });
        Assert.Equal((0, true), (backlog!.Count, backlog.HasMore));
    }

    // Cancellation "CanDecide" means the buyer may still cancel; it is not a pending decision and is never counted.
    [Fact]
    public void Order_reviews_read_spending_and_store_only_and_count_each_order_once() {
        Assert.Equal([OrderReviewStage.Spending, OrderReviewStage.Store], OrderReviewWorkSource.Stages);
        var work = OrderReviewWorkSource.Summarize([(41L, true, T0.AddHours(2)), (41L, true, T0.AddHours(3)), (42L, false, T0), (43L, true, T0.AddHours(1))], hasMore: false);
        Assert.Equal((2, false, T0.AddHours(1)), (work!.Count, work.HasMore, work.OldestAt));
        Assert.Equal((0, true), (OrderReviewWorkSource.Summarize([(42L, false, T0)], hasMore: true)!.Count, true));
        Assert.Null(OrderReviewWorkSource.Summarize([(42L, false, T0)], hasMore: false));
    }

    // Each source is offered only to actors who can see its workspace in navigation (identityDestinations).
    [Fact]
    public void Sources_follow_their_workspace_navigation_permissions() {
        static Func<string, bool> Holding(params string[] permissions) => permissions.Contains;
        IWorkSource payment = new PaymentReviewWorkSource(null!), orders = new OrderReviewWorkSource(null!),
            incoming = new IncomingRequestWorkSource(null!), dispatch = new DispatchWorkSource(null!);
        Assert.True(payment.AvailableTo(Holding(Permissions.PaymentReviewRead, Permissions.InvoiceRead)));
        Assert.False(payment.AvailableTo(Holding(Permissions.PaymentReviewRead)));
        Assert.True(orders.AvailableTo(Holding(Permissions.OrderSpendApprove)));
        Assert.True(orders.AvailableTo(Holding(Permissions.OrderAccept)));
        Assert.False(orders.AvailableTo(Holding(Permissions.OrderCancel)));
        Assert.True(incoming.AvailableTo(Holding(Permissions.BranchOrderRequestDecide)));
        Assert.False(incoming.AvailableTo(Holding(Permissions.BranchOrderRequestRead)));
        Assert.True(dispatch.AvailableTo(Holding(Permissions.ShipmentDispatch, Permissions.ShipmentAssign)));
        Assert.False(dispatch.AvailableTo(Holding(Permissions.ShipmentDispatch)));
    }
}
