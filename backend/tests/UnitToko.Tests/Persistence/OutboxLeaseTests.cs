using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Persistence;

[Collection("Mongo")]
public sealed class OutboxLeaseTests(MongoFixture fixture)
{
    private const string Kind = "unit-toko.test.validation.requested";
    private static OutboxClaimPolicy Policy(int attempts = 3, int leaseSeconds = 5, int maximumSeconds = 15) =>
        new([Kind], attempts, TimeSpan.FromSeconds(leaseSeconds), TimeSpan.FromSeconds(maximumSeconds));

    private async Task InDatabase(Func<IMongoDatabase, MutableClock, OutboxStore, Task> work)
    {
        var db = fixture.Client.GetDatabase("ut_outbox_bounded_" + Guid.NewGuid().ToString("N"));
        var clock = new MutableClock(new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));
        try { await new CollectionMigrations(db, clock).ApplyAsync(); await work(db, clock, new(db, clock)); }
        finally { await fixture.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName); }
    }
    private Task Add(IMongoDatabase db, OutboxStore store, string key, string kind = Kind) =>
        new MongoUnitOfWork(fixture.Client).ExecuteAsync(async (tx, ct) =>
        { await store.EnqueueAsync(tx, key, kind, new BsonDocument("versionRowId", 10L), ct); return 0; });
    private static Task<BsonDocument> Row(IMongoDatabase db, string key) =>
        db.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument("sourceKey", key)).SingleAsync();

    [Fact]
    public Task Claims_only_explicit_kind_and_leaves_foreign_exhaustion_untouched() => InDatabase(async (db, clock, store) =>
    {
        await Add(db, store, "foreign", "accounting.posted"); await Add(db, store, "ours");
        await db.GetCollection<BsonDocument>("outboxEvents").UpdateOneAsync(new BsonDocument("sourceKey", "foreign"), Builders<BsonDocument>.Update.Set("attempts", 999));
        var claim = await store.ClaimAsync("validator", Policy());
        Assert.Equal("ours", claim!["sourceKey"].AsString); Assert.Equal(1, claim["attempts"].AsInt32);
        Assert.Null(await store.ClaimAsync("validator", Policy()));
        Assert.Equal("pending", (await Row(db, "foreign"))["status"].AsString);
    });

    [Fact]
    public Task Concurrent_consumers_have_one_live_token() => InDatabase(async (db, clock, store) =>
    {
        await Add(db, store, "one");
        var claims = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => store.ClaimAsync("worker-" + i, Policy())));
        Assert.Single(claims, claim => claim is not null);
        Assert.Equal(1, (await Row(db, "one"))["attempts"].AsInt32);
    });

    [Fact]
    public Task Renewal_is_owner_bound_and_cannot_cross_absolute_processing_deadline() => InDatabase(async (db, clock, store) =>
    {
        await Add(db, store, "renew"); var claim = (await store.ClaimAsync("worker", Policy()))!;
        var deadline = claim["claimDeadline"];
        var wrong = claim.DeepClone().AsBsonDocument; wrong["leaseOwner"] = "other";
        Assert.Null(await store.RenewAsync(wrong, TimeSpan.FromSeconds(10)));
        Assert.False(await store.CompleteAsync(wrong));
        Assert.False(await store.FailAsync(wrong, "decode_timeout", true, TimeSpan.Zero));
        clock.Advance(TimeSpan.FromSeconds(4));
        var renewed = await store.RenewAsync(claim, TimeSpan.FromSeconds(20));
        Assert.NotNull(renewed); Assert.Equal(deadline, renewed["leaseUntil"]); Assert.Equal(deadline, renewed["claimDeadline"]);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Null(await store.RenewAsync(renewed, TimeSpan.FromSeconds(1)));
        Assert.False(await store.CompleteAsync(renewed));
        Assert.False(await store.FailAsync(renewed, "decode_timeout", true, TimeSpan.Zero));
    });

    [Fact]
    public Task Retry_waits_then_dead_letters_at_persisted_limit_even_if_later_policy_increases() => InDatabase(async (db, clock, store) =>
    {
        await Add(db, store, "retry"); var first = (await store.ClaimAsync("worker-a", Policy(2)))!;
        Assert.True(await store.FailAsync(first, "scanner_unavailable", true, TimeSpan.FromSeconds(2)));
        Assert.Null(await store.ClaimAsync("worker-b", Policy(9)));
        clock.Advance(TimeSpan.FromSeconds(2)); var second = (await store.ClaimAsync("worker-b", Policy(9)))!;
        Assert.Equal(2, second["attemptLimit"].AsInt32); Assert.Equal(2, second["attempts"].AsInt32);
        Assert.False(await store.CompleteAsync(first));
        Assert.True(await store.FailAsync(second, "scanner_unavailable", true, TimeSpan.Zero));
        var final = await Row(db, "retry"); Assert.Equal("dead", final["status"].AsString);
        Assert.Equal("scanner_unavailable", final["lastFailureCode"].AsString); Assert.True(final["deadAt"].IsBsonDateTime);
        Assert.False(final.Contains("leaseToken")); Assert.Null(await store.ClaimAsync("worker-c", Policy(9)));
    });

    [Fact]
    public Task Expired_last_attempt_is_dead_and_next_job_can_be_claimed() => InDatabase(async (db, clock, store) =>
    {
        await Add(db, store, "first"); var stale = (await store.ClaimAsync("worker-a", Policy(1)))!;
        await Add(db, store, "second"); clock.Advance(TimeSpan.FromSeconds(6));
        var next = await store.ClaimAsync("worker-b", Policy(9));
        Assert.Equal("second", next!["sourceKey"].AsString);
        var dead = await Row(db, "first"); Assert.Equal("dead", dead["status"].AsString);
        Assert.Equal("attempt_limit_reached", dead["lastFailureCode"].AsString); Assert.Equal(1, dead["attempts"].AsInt32);
        Assert.False(await store.CompleteAsync(stale)); Assert.Null(await store.RenewAsync(stale, TimeSpan.FromSeconds(1)));
    });

    [Fact]
    public Task Expired_claim_can_be_reacquired_but_stale_token_cannot_mutate() => InDatabase(async (db, clock, store) =>
    {
        await Add(db, store, "expired"); var first = (await store.ClaimAsync("worker", Policy()))!;
        clock.Advance(TimeSpan.FromSeconds(6)); var next = (await store.ClaimAsync("worker", Policy()))!;
        Assert.NotEqual(first["leaseToken"], next["leaseToken"]);
        Assert.False(await store.CompleteAsync(first)); Assert.False(await store.RetryAsync(first, TimeSpan.Zero));
        Assert.Null(await store.RenewAsync(first, TimeSpan.FromSeconds(2)));
        Assert.True(await store.CompleteAsync(next)); Assert.False(await store.CompleteAsync(next));
    });

    [Fact]
    public Task Permanent_failure_is_terminal_and_stores_only_safe_reason() => InDatabase(async (db, clock, store) =>
    {
        await Add(db, store, "bad"); var claim = (await store.ClaimAsync("worker", Policy()))!;
        await Assert.ThrowsAsync<DomainValidationException>(() => store.FailAsync(claim, "image has account 123", false, TimeSpan.Zero));
        Assert.Equal("leased", (await Row(db, "bad"))["status"].AsString);
        Assert.True(await store.FailAsync(claim, "invalid_media", false, TimeSpan.Zero));
        var dead = await Row(db, "bad"); Assert.Equal("dead", dead["status"].AsString); Assert.False(dead.Contains("claimDeadline"));
        Assert.Null(await store.ClaimAsync("worker", Policy()));
    });

    [Fact]
    public Task Earlier_infrastructure_jobs_without_limits_adopt_bounded_policy() => InDatabase(async (db, clock, store) =>
    {
        await Add(db, store, "old");
        var claim = (await store.ClaimAsync("worker", Policy(2)))!;
        Assert.Equal(2, claim["attemptLimit"].AsInt32);
        Assert.True(await store.RetryAsync(claim, TimeSpan.Zero));
        var lowered = (await store.ClaimAsync("worker", Policy(1)));
        Assert.Null(lowered); Assert.Equal("dead", (await Row(db, "old"))["status"].AsString);
    });

    [Fact]
    public Task Caller_mutated_claim_cannot_raise_persisted_limit() => InDatabase(async (db, clock, store) =>
    {
        await Add(db, store, "tamper"); var claim = (await store.ClaimAsync("worker", Policy(1)))!;
        claim["attempts"] = 0; claim["attemptLimit"] = 1000;
        Assert.True(await store.RetryAsync(claim, TimeSpan.Zero));
        Assert.Equal("dead", (await Row(db, "tamper"))["status"].AsString);
    });

    [Fact]
    public Task Short_renewal_never_shortens_live_lease() => InDatabase(async (db, clock, store) =>
    {
        await Add(db, store, "short"); var first = (await store.ClaimAsync("worker", Policy()))!;
        var renewed = await store.RenewAsync(first, TimeSpan.FromSeconds(1));
        Assert.Equal(first["leaseUntil"], renewed!["leaseUntil"]);
    });

    [Fact]
    public Task Hard_deadline_expiry_terminalizes_without_granting_another_attempt() => InDatabase(async (db, clock, store) =>
    {
        await Add(db, store, "deadline"); var first = (await store.ClaimAsync("worker", Policy()))!;
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Null(await store.ClaimAsync("worker", Policy(10)));
        var row = await Row(db, "deadline"); Assert.Equal("dead", row["status"].AsString);
        Assert.Equal("processing_deadline_exceeded", row["lastFailureCode"].AsString); Assert.Equal(1, row["attempts"].AsInt32);
        Assert.False(await store.CompleteAsync(first));
    });

    [Fact]
    public Task Terminal_scan_is_bounded_and_later_poll_reaches_remaining_work() => InDatabase(async (db, clock, store) =>
    {
        for (var i = 0; i < 17; i++) await Add(db, store, "exhausted-" + i);
        await db.GetCollection<BsonDocument>("outboxEvents").UpdateManyAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("attempts", 3));
        clock.Advance(TimeSpan.FromSeconds(1)); await Add(db, store, "eligible");
        Assert.Null(await store.ClaimAsync("worker", Policy()));
        Assert.Equal(16, await db.GetCollection<BsonDocument>("outboxEvents").CountDocumentsAsync(new BsonDocument("status", "dead")));
        Assert.Equal("eligible", (await store.ClaimAsync("worker", Policy()))!["sourceKey"].AsString);
        Assert.Equal(17, await db.GetCollection<BsonDocument>("outboxEvents").CountDocumentsAsync(new BsonDocument("status", "dead")));
    });

    [Fact]
    public Task Consumer_expression_text_remains_literal_and_claim_kind_cannot_be_substituted() => InDatabase(async (db, clock, store) =>
    {
        await Add(db, store, "literal"); var claim = (await store.ClaimAsync("$payload", Policy()))!;
        Assert.Equal("$payload", claim["leaseOwner"].AsString);
        var wrong = claim.DeepClone().AsBsonDocument; wrong["kind"] = "accounting.posted";
        Assert.False(await store.CompleteAsync(wrong)); Assert.Null(await store.RenewAsync(wrong, TimeSpan.FromSeconds(1)));
        Assert.True(await store.CompleteAsync(claim));
    });

    [Fact]
    public Task Delayed_and_terminal_jobs_are_not_reclaimed_and_last_attempt_can_succeed() => InDatabase(async (db, clock, store) =>
    {
        await Add(db, store, "success"); var last = (await store.ClaimAsync("worker", Policy(1)))!;
        Assert.True(await store.CompleteAsync(last)); Assert.Null(await store.ClaimAsync("worker", Policy()));
        var row = await Row(db, "success"); Assert.Equal("completed", row["status"].AsString);
        Assert.False(row.Contains("claimDeadline")); Assert.False(row.Contains("leaseToken"));
    });
    [Theory]
    [InlineData(false, 5)] [InlineData(true, 5)] [InlineData(false, 15)] [InlineData(true, 15)]
    public async Task Delayed_response_withholds_expired_handle_and_preserves_recorded_attempt(bool renewal, int elapsedSeconds)
    {
        var clock = new MutableClock(new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));
        var armed = 0;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ApplicationName = "OutboxLateResponse-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(e =>
        {
            if (e.CommandName == "findAndModify" && Interlocked.Exchange(ref armed, 0) == 1)
                clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        });
        var client = new MongoClient(settings);
        var db = client.GetDatabase("ut_outbox_late_" + Guid.NewGuid().ToString("N"));
        try
        {
            await new CollectionMigrations(db, clock).ApplyAsync(); var store = new OutboxStore(db, clock);
            await new MongoUnitOfWork(client).ExecuteAsync(async (tx, ct) =>
            { await store.EnqueueAsync(tx, "delayed", Kind, new BsonDocument("versionRowId", 10L), ct); return 0; });
            BsonDocument? original = renewal ? await store.ClaimAsync("worker", Policy()) : null;
            Volatile.Write(ref armed, 1);
            var returned = renewal ? await store.RenewAsync(original!, TimeSpan.FromSeconds(1)) : await store.ClaimAsync("worker", Policy());
            Assert.Null(returned);
            var persisted = await Row(db, "delayed"); Assert.Equal("leased", persisted["status"].AsString); Assert.Equal(1, persisted["attempts"].AsInt32);
            var recovered = await store.ClaimAsync("recovery", Policy());
            if (elapsedSeconds == 5)
            { Assert.NotNull(recovered); Assert.Equal(2, recovered["attempts"].AsInt32); Assert.True(await store.CompleteAsync(recovered)); }
            else
            { Assert.Null(recovered); Assert.Equal("dead", (await Row(db, "delayed"))["status"].AsString); }
        }
        finally { await client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName); }
    }
    [Theory]
    [InlineData(0, 5, 15)] [InlineData(1001, 5, 15)] [InlineData(2, 0, 15)]
    [InlineData(2, 3601, 3601)] [InlineData(2, 5, 4)] [InlineData(2, 5, 3601)]
    public void Invalid_budgets_cannot_register_consumer(int attempts, int lease, int deadline) =>
        Assert.Throws<DomainValidationException>(() => Policy(attempts, lease, deadline));

    [Fact]
    public void Kinds_must_be_explicit_bounded_distinct_and_immutable()
    {
        foreach (var kinds in new string[][] { [], [" "], ["x", "x"], [" x"], [new string('x',101)], Enumerable.Range(0,33).Select(i=>"kind."+i).ToArray() })
            Assert.Throws<DomainValidationException>(() => new OutboxClaimPolicy(kinds, 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
        string[] input = [Kind]; var policy = new OutboxClaimPolicy(input, 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        input[0] = "accounting.posted"; Assert.Equal(Kind, Assert.Single(policy.Kinds));
    }
}