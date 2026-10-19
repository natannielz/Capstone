using MongoDB.Bson;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Work;

namespace UnitToko.Platform.Ordering;

/// <summary>"Pekerjaan saya": orders the actor can decide now (spending approval, store acceptance, cancellation).
/// Each stage is read with its own authorisation; a stage the actor may not review is skipped, not revealed.</summary>
public sealed class OrderReviewWorkSource(OrderSubmissionService orders) : IWorkSource
{
    public string Key => "ordering.reviews";
    public string Area => "orders";
    public string Label => "Pesanan menunggu keputusan";
    public string Href => "/ordering/reviews";

    public async Task<WorkCount?> ReadAsync(ActorContext actor, CancellationToken ct)
    {
        int count = 0; bool hasMore = false, readable = false; DateTimeOffset? oldest = null;
        foreach (var stage in Enum.GetValues<OrderReviewStage>()) {
            try {
                var (items, more) = await orders.ReviewListAsync(actor, stage, null, ct); readable = true;
                var decidable = items.Where(item => item.CanDecide).ToArray();
                count += decidable.Length; hasMore |= more && decidable.Length > 0;
                foreach (var item in decidable) if (oldest is null || item.OrderedAt < oldest) oldest = item.OrderedAt;
            }
            catch (AuthorizationDeniedException) { }
        }
        if (!readable) throw new AuthorizationDeniedException("access_denied");
        return count == 0 ? null : new(count, hasMore, oldest);
    }
}

/// <summary>"Pekerjaan saya": incoming branch requests still pending a store decision.</summary>
public sealed class IncomingRequestWorkSource(BranchOrderRequestService requests) : IWorkSource
{
    public string Key => "ordering.requests.incoming";
    public string Area => "orders";
    public string Label => "Permintaan cabang menunggu keputusan";
    public string Href => "/ordering/requests/incoming";

    public async Task<WorkCount?> ReadAsync(ActorContext actor, CancellationToken ct)
    {
        return Summarize(await requests.ListAsync(actor, incoming: true, null, ct));
    }

    /// <summary>Counts pending requests on the first page; more pages exist when nextRequestId is set.</summary>
    internal static WorkCount? Summarize(BsonDocument page)
    {
        var pending = page["items"].AsBsonArray.Select(item => item.AsBsonDocument).Where(item => item["state"] == "pending").ToArray();
        if (pending.Length == 0) return null;
        var oldest = pending.Select(item => item["recordedAt"]).Where(value => value.IsValidDateTime).Select(value => (DateTimeOffset?)new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)).Min();
        return new(pending.Length, !page["nextRequestId"].IsBsonNull, oldest);
    }
}
