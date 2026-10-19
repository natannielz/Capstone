using MongoDB.Bson;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Ordering;

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

    [Fact]
    public void Payment_reviews_with_nothing_waiting_report_null_even_if_more_pages_exist() =>
        Assert.Null(PaymentReviewWorkSource.Summarize([("assigned", 99, T0)], hasMore: true, actorId: 7));

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
    }
}
