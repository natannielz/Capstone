using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Work;

namespace UnitToko.Platform.Billing;

/// <summary>"Pekerjaan saya": submitted payment proofs waiting for the actor (unassigned, or assigned to the actor).
/// Reads through the review queue with its own authorisation and purpose; the correlation marks the summary origin.</summary>
public sealed class PaymentReviewWorkSource(PaymentDraftService payments) : IWorkSource
{
    public string Key => "billing.payment-reviews";
    public string Area => "finance";
    public string Label => "Bukti pembayaran menunggu pemeriksaan";
    public string Href => "/billing/payment-reviews";
    public bool AvailableTo(Func<string, bool> allowed) => allowed(Permissions.PaymentReviewRead) && allowed(Permissions.InvoiceRead);

    public async Task<WorkCount?> ReadAsync(ActorContext actor, CancellationToken ct)
    {
        var page = await payments.ListReviewQueueAsync(actor, null, new AuthorizationReadMetadata("payment-review", "work-summary-" + Guid.NewGuid().ToString("N")), ct);
        return Summarize(page.Items.Select(entry => (entry.State, entry.AssigneeId, entry.Declaration.Submission?.SubmittedAt)), page.HasMore, actor.UserId);
    }

    /// <summary>Waiting = unassigned, or assigned to this actor. Nothing waiting on page one with more pages = still to be checked.</summary>
    internal static WorkCount? Summarize(IEnumerable<(string State, long? AssigneeId, DateTimeOffset? SubmittedAt)> entries, bool hasMore, long actorId)
    {
        var waiting = entries.Where(entry => entry.State == "pending" || entry.State == "assigned" && entry.AssigneeId == actorId).ToArray();
        if (waiting.Length == 0) return hasMore ? new(0, true, null) : null;
        return new(waiting.Length, hasMore, waiting.Select(entry => entry.SubmittedAt).Where(time => time.HasValue).Min());
    }
}
