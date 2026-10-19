using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Work;

/// <summary>What one owner queue reports from its first page. HasMore = more pages exist, so Count is a lower bound and OldestAt
/// is "at least this old"; Count 0 with HasMore means nothing was waiting on page one but the rest is unchecked.</summary>
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
    /// <summary>The same rule that shows this workspace in navigation (identityDestinations); a source the actor cannot see is never read.</summary>
    bool AvailableTo(Func<string, bool> allowed);
    /// <summary>Null when nothing is waiting.</summary>
    Task<WorkCount?> ReadAsync(ActorContext actor, CancellationToken ct);
}

/// <summary>The actor's current permissions (as the account's action list computes them), read once per summary.</summary>
public interface IWorkPermissions
{
    Task<Func<string, bool>> ReadAsync(ActorContext actor, CancellationToken ct);
}

public sealed class AuthorizedWorkPermissions(AuthorizationStore authorization, IClock clock) : IWorkPermissions
{
    public async Task<Func<string, bool>> ReadAsync(ActorContext actor, CancellationToken ct)
    {
        var snapshot = await authorization.ReadCurrentAsync(actor, ct); var now = clock.UtcNow;
        return permission => ScopePolicy.Compile(actor, snapshot, permission, now).IsAllowed;
    }
}

/// <summary>"Pekerjaan saya": composes the owner sources the actor can see, sequentially (bounded load, 60 s deadline safe).
/// A session-level denial fails the summary; any other failure of a visible source becomes an unavailable item.</summary>
public sealed class WorkSummaryService(IEnumerable<IWorkSource> sources, IWorkPermissions permissions)
{
    private readonly IReadOnlyList<IWorkSource> sources = [.. sources];

    /// <summary>The actor or session itself is no longer valid: the whole request fails rather than reporting nothing waiting.</summary>
    public static bool IsSessionDenial(AuthorizationDeniedException error) => error.Code is "invalid_actor" or "stale_or_inactive_actor" or "stale_or_inactive_session";

    public async Task<IReadOnlyList<WorkItem>> ReadAsync(ActorContext actor, CancellationToken ct)
    {
        var allowed = await permissions.ReadAsync(actor, ct);
        var items = new List<WorkItem>();
        foreach (var source in sources.Where(source => source.AvailableTo(allowed))) {
            ct.ThrowIfCancellationRequested();
            try {
                if (await source.ReadAsync(actor, ct) is { } work && (work.Count > 0 || work.HasMore))
                    items.Add(new(source.Key, source.Area, source.Label, source.Href, work.Count, work.HasMore, work.OldestAt));
            }
            catch (AuthorizationDeniedException error) when (IsSessionDenial(error)) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { items.Add(new(source.Key, source.Area, source.Label, source.Href, 0, false, null, Unavailable: true)); }
        }
        return [.. items.OrderBy(item => item.Unavailable).ThenBy(item => item.OldestAt is null).ThenBy(item => item.OldestAt).ThenBy(item => item.Key, StringComparer.Ordinal)];
    }
}
