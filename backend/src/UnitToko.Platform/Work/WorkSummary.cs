using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Work;

/// <summary>What one owner queue reports: a count from its first page (HasMore = more than Count) and the oldest waiting time.</summary>
public sealed record WorkCount(int Count, bool HasMore, DateTimeOffset? OldestAt);

/// <summary>One kind of waiting work for the actor, linked to the workspace that handles it. No business payload is exposed.</summary>
public sealed record WorkItem(string Key, string Area, string Label, string Href, int Count, bool HasMore, DateTimeOffset? OldestAt, bool Unavailable = false);

/// <summary>Owned by one domain; reads only through that domain's own authorised queue service.</summary>
public interface IWorkSource
{
    string Key { get; }
    string Area { get; }
    string Label { get; }
    string Href { get; }
    /// <summary>Null when nothing is waiting. Throws AuthorizationDeniedException when the actor may not read this queue.</summary>
    Task<WorkCount?> ReadAsync(ActorContext actor, CancellationToken ct);
}

/// <summary>"Pekerjaan saya": composes the registered owner sources sequentially (bounded load, 60 s deadline safe).
/// A denied source is omitted, so its queue is not revealed; any other failure becomes an unavailable item.</summary>
public sealed class WorkSummaryService(IEnumerable<IWorkSource> sources)
{
    private readonly IReadOnlyList<IWorkSource> sources = [.. sources];

    public async Task<IReadOnlyList<WorkItem>> ReadAsync(ActorContext actor, CancellationToken ct)
    {
        var items = new List<WorkItem>();
        foreach (var source in sources) {
            ct.ThrowIfCancellationRequested();
            try {
                if (await source.ReadAsync(actor, ct) is { Count: > 0 } work)
                    items.Add(new(source.Key, source.Area, source.Label, source.Href, work.Count, work.HasMore, work.OldestAt));
            }
            catch (AuthorizationDeniedException) { }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { items.Add(new(source.Key, source.Area, source.Label, source.Href, 0, false, null, Unavailable: true)); }
        }
        return [.. items.OrderBy(item => item.Unavailable).ThenBy(item => item.OldestAt is null).ThenBy(item => item.OldestAt).ThenBy(item => item.Key, StringComparer.Ordinal)];
    }
}
