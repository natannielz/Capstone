using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Work;

namespace UnitToko.Tests.Work;

public sealed class WorkSummaryServiceTests
{
    private static readonly ActorContext Actor = new(7, "https://approved.example/", "subject-7", 1, DateTimeOffset.UnixEpoch, FactorAssurance.MultiFactor);
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);

    // A source is available when the actor holds the permission named after its key (mirrors its navigation rule).
    private sealed class Source(string key, Func<CancellationToken, Task<WorkCount?>> read) : IWorkSource
    {
        public int Reads { get; private set; }
        public string Key => key;
        public string Area => "finance";
        public string Label => "Label " + key;
        public string Href => "/" + key;
        public bool AvailableTo(Func<string, bool> allowed) => allowed(key);
        public Task<WorkCount?> ReadAsync(ActorContext actor, CancellationToken ct) { Reads++; return read(ct); }
    }
    private sealed class Permissions(Func<ActorContext, Func<string, bool>> read) : IWorkPermissions
    {
        public Task<Func<string, bool>> ReadAsync(ActorContext actor, CancellationToken ct) => Task.FromResult(read(actor));
    }
    private static Source Returns(string key, int count, DateTimeOffset? oldest, bool hasMore = false) =>
        new(key, _ => Task.FromResult<WorkCount?>(new(count, hasMore, oldest)));
    private static WorkSummaryService Service(params IWorkSource[] sources) => new(sources, new Permissions(_ => _ => true));

    [Fact]
    public async Task Source_whose_navigation_permission_is_missing_is_neither_read_nor_shown() {
        var hidden = new Source("hidden", _ => throw new TimeoutException("would be unavailable"));
        var service = new WorkSummaryService([Returns("a", 2, T0), hidden], new Permissions(_ => permission => permission == "a"));
        var items = await service.ReadAsync(Actor, default);
        Assert.Equal(["a"], items.Select(item => item.Key));
        Assert.Equal(("Label a", "/a", 2), (items[0].Label, items[0].Href, items[0].Count));
        Assert.Equal(0, hidden.Reads);
    }

    [Theory]
    [InlineData("stale_or_inactive_session")] [InlineData("stale_or_inactive_actor")] [InlineData("invalid_actor")]
    public async Task Session_level_denial_fails_the_summary_instead_of_reporting_nothing_waiting(string code) {
        var service = Service(Returns("a", 1, T0), new Source("revoked", _ => throw new AuthorizationDeniedException(code)));
        Assert.Equal(code, (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(Actor, default))).Code);
    }

    [Fact]
    public async Task Denial_after_the_permission_check_is_unavailable_not_silently_empty() {
        var service = Service(new Source("scoped", _ => throw new AuthorizationDeniedException("scope_denied")));
        var item = Assert.Single(await service.ReadAsync(Actor, default));
        Assert.True(item.Unavailable);
    }

    [Fact]
    public async Task Faulted_source_becomes_unavailable_others_still_shown() {
        var service = Service(new Source("slow", _ => throw new TimeoutException("slow")), Returns("a", 1, T0));
        var items = await service.ReadAsync(Actor, default);
        Assert.Equal(["a", "slow"], items.Select(item => item.Key));
        var slow = items.Single(item => item.Key == "slow");
        Assert.True(slow.Unavailable);
        Assert.Equal(("Label slow", "/slow", 0), (slow.Label, slow.Href, slow.Count));
    }

    [Fact]
    public async Task Internal_timeout_without_caller_cancellation_is_unavailable_not_thrown() {
        var service = Service(new Source("timeout", _ => throw new TaskCanceledException("internal")));
        Assert.True((await service.ReadAsync(Actor, default)).Single().Unavailable);
    }

    [Fact]
    public async Task Zero_count_and_null_items_are_omitted() {
        var service = Service(Returns("empty", 0, null), new Source("none", _ => Task.FromResult<WorkCount?>(null)), Returns("a", 3, T0));
        Assert.Equal(["a"], (await service.ReadAsync(Actor, default)).Select(item => item.Key));
    }

    [Fact]
    public async Task Nothing_waiting_on_the_first_page_but_more_pages_is_shown_as_to_be_checked() {
        var item = Assert.Single(await Service(Returns("backlog", 0, null, hasMore: true)).ReadAsync(Actor, default));
        Assert.Equal(("backlog", 0, true, false), (item.Key, item.Count, item.HasMore, item.Unavailable));
    }

    [Fact]
    public async Task Items_are_ordered_by_oldest_waiting_first() {
        var service = Service(Returns("recent", 1, T0.AddHours(2)), Returns("undated", 1, null), Returns("oldest", 4, T0),
            Returns("tie-b", 1, T0.AddHours(1)), Returns("tie-a", 1, T0.AddHours(1)));
        Assert.Equal(["oldest", "tie-a", "tie-b", "recent", "undated"], (await service.ReadAsync(Actor, default)).Select(item => item.Key));
    }

    [Fact]
    public async Task Cancellation_propagates() {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var service = Service(new Source("a", ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult<WorkCount?>(new(1, false, T0)); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(Actor, cancelled.Token));
    }
}
