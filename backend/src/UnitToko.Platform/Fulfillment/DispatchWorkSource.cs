using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Work;

namespace UnitToko.Platform.Fulfillment;

/// <summary>"Pekerjaan saya": prepared shipments the actor can dispatch now (the dispatch queue's own authorisation).</summary>
public sealed class DispatchWorkSource(ShipmentDispatchService dispatches) : IWorkSource
{
    public string Key => "fulfillment.dispatches";
    public string Area => "fulfillment";
    public string Label => "Kiriman siap diberangkatkan";
    public string Href => "/fulfillment/dispatches";

    public async Task<WorkCount?> ReadAsync(ActorContext actor, CancellationToken ct)
    {
        var (items, hasMore) = await dispatches.ListAsync(actor, null, ct);
        var ready = items.Where(item => item.CanDispatch).ToArray();
        return ready.Length == 0 ? null : new(ready.Length, hasMore, ready.Min(item => item.Source.OrderedAt));
    }
}
