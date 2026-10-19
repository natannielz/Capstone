using MongoDB.Bson;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Work;

namespace UnitToko.Platform.Ordering;

/// <summary>"Pekerjaan saya": orders waiting for the actor's spending approval or store acceptance, counted once per order.
/// Cancellation is not read: its CanDecide means the buyer may still cancel, not that a decision is pending.</summary>
public sealed class OrderReviewWorkSource(OrderSubmissionService orders) : IWorkSource
{
    internal static readonly OrderReviewStage[] Stages = [OrderReviewStage.Spending, OrderReviewStage.Store];
    public string Key => "ordering.reviews";
    public string Area => "orders";
    public string Label => "Pesanan menunggu keputusan";
    public string Href => "/ordering/reviews";
    public bool AvailableTo(Func<string, bool> allowed) => allowed(Permissions.OrderSpendApprove) || allowed(Permissions.OrderAccept);

    public async Task<WorkCount?> ReadAsync(ActorContext actor, CancellationToken ct)
    {
        var items = new List<(long OrderId, bool CanDecide, DateTimeOffset OrderedAt)>(); bool hasMore = false, readable = false;
        foreach (var stage in Stages) {
            try {
                var (page, more) = await orders.ReviewListAsync(actor, stage, null, ct); readable = true; hasMore |= more;
                items.AddRange(page.Select(item => (item.OrderId, item.CanDecide, item.OrderedAt)));
            }
            // A stage the actor may not review (e.g. holds store acceptance but not spending approval) is skipped.
            catch (AuthorizationDeniedException error) when (!WorkSummaryService.IsSessionDenial(error)) { }
        }
        if (!readable) throw new AuthorizationDeniedException("access_denied");
        return Summarize(items, hasMore);
    }

    internal static WorkCount? Summarize(IEnumerable<(long OrderId, bool CanDecide, DateTimeOffset OrderedAt)> items, bool hasMore)
    {
        var waiting = items.Where(item => item.CanDecide).GroupBy(item => item.OrderId).Select(order => order.Min(item => item.OrderedAt)).ToArray();
        return waiting.Length == 0 ? hasMore ? new(0, true, null) : null : new(waiting.Length, hasMore, waiting.Min());
    }
}

/// <summary>"Pekerjaan saya": incoming branch requests still pending a store decision.</summary>
public sealed class IncomingRequestWorkSource(BranchOrderRequestService requests) : IWorkSource
{
    public string Key => "ordering.requests.incoming";
    public string Area => "orders";
    public string Label => "Permintaan cabang menunggu keputusan";
    public string Href => "/ordering/requests/incoming";
    public bool AvailableTo(Func<string, bool> allowed) => allowed(Permissions.BranchOrderRequestDecide);

    public async Task<WorkCount?> ReadAsync(ActorContext actor, CancellationToken ct)
    {
        return Summarize(await requests.ListAsync(actor, incoming: true, null, ct));
    }

    /// <summary>Counts pending requests on the first page; more pages exist when nextRequestId is set.</summary>
    internal static WorkCount? Summarize(BsonDocument page)
    {
        var hasMore = !page["nextRequestId"].IsBsonNull;
        var pending = page["items"].AsBsonArray.Select(item => item.AsBsonDocument).Where(item => item["state"] == "pending").ToArray();
        if (pending.Length == 0) return hasMore ? new(0, true, null) : null;
        var oldest = pending.Select(item => item["recordedAt"]).Where(value => value.IsValidDateTime).Select(value => (DateTimeOffset?)new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)).Min();
        return new(pending.Length, hasMore, oldest);
    }
}
