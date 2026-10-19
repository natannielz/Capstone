using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Work;

namespace UnitToko.Tests.Work;

public sealed class WorkSummaryServiceTests
{
    private static readonly ActorContext Actor = new(7, "https://approved.example/", "subject-7", 1, DateTimeOffset.UnixEpoch, FactorAssurance.MultiFactor);
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);

    private sealed class Source(string key, Func<CancellationToken, Task<WorkCount?>> read) : IWorkSource
    {
        public string Key => key;
        public string Area => "finance";
        public string Label => "Label " + key;
        public string Href => "/" + key;
        public Task<WorkCount?> ReadAsync(ActorContext actor, CancellationToken ct) => read(ct);
    }
    private static Source Returns(string key, int count, DateTimeOffset? oldest, bool hasMore = false) =>
        new(key, _ => Task.FromResult<WorkCount?>(new(count, hasMore, oldest)));

    [Fact]
    public async Task Denied_source_is_omitted() {
        var service = new WorkSummaryService([Returns("a", 2, T0), new Source("denied", _ => throw new AuthorizationDeniedException("access_denied"))]);
        var items = await service.ReadAsync(Actor, default);
        Assert.Equal(["a"], items.Select(item => item.Key));
        Assert.Equal(("Label a", "/a", 2), (items[0].Label, items[0].Href, items[0].Count));
    }

    [Fact]
    public async Task Faulted_source_becomes_unavailable_others_still_shown() {
        var service = new WorkSummaryService([new Source("slow", _ => throw new TimeoutException("slow")), Returns("a", 1, T0)]);
        var items = await service.ReadAsync(Actor, default);
        Assert.Equal(["a", "slow"], items.Select(item => item.Key));
        var slow = items.Single(item => item.Key == "slow");
        Assert.True(slow.Unavailable);
        Assert.Equal(("Label slow", "/slow", 0), (slow.Label, slow.Href, slow.Count));
    }

    [Fact]
    public async Task Internal_timeout_without_caller_cancellation_is_unavailable_not_thrown() {
        var service = new WorkSummaryService([new Source("timeout", _ => throw new TaskCanceledException("internal"))]);
        Assert.True((await service.ReadAsync(Actor, default)).Single().Unavailable);
    }

    [Fact]
    public async Task Zero_count_and_null_items_are_omitted() {
        var service = new WorkSummaryService([Returns("empty", 0, null), new Source("none", _ => Task.FromResult<WorkCount?>(null)), Returns("a", 3, T0)]);
        Assert.Equal(["a"], (await service.ReadAsync(Actor, default)).Select(item => item.Key));
    }

    [Fact]
    public async Task Items_are_ordered_by_oldest_waiting_first() {
        var service = new WorkSummaryService([Returns("recent", 1, T0.AddHours(2)), Returns("undated", 1, null), Returns("oldest", 4, T0),
            Returns("tie-b", 1, T0.AddHours(1)), Returns("tie-a", 1, T0.AddHours(1))]);
        Assert.Equal(["oldest", "tie-a", "tie-b", "recent", "undated"], (await service.ReadAsync(Actor, default)).Select(item => item.Key));
    }

    [Fact]
    public async Task Cancellation_propagates() {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var service = new WorkSummaryService([new Source("a", ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult<WorkCount?>(new(1, false, T0)); })]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(Actor, cancelled.Token));
    }
}
