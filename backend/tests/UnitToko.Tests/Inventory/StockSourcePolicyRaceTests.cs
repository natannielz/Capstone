using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourcePolicyServiceTests
{
    [Theory]
    [InlineData("owner")] [InlineData("linked")] [InlineData("membership")] [InlineData("mfa")]
    public async Task Final_review_authority_change_rolls_back_policy_decision_audit_receipt_and_pool_fence(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data, true);
        var proposed = await Propose(data, pool, maker); var before = await Effects(data);
        var captured = 0; var changed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "PolicyFinal-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "insert" && e.Command.GetValue("insert", "") == "auditEvents" &&
                    e.Command["documents"].AsBsonArray[0]["operation"] == "stock.source.policy.approve") captured = e.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.RequestId != captured || Interlocked.CompareExchange(ref changed, 1, 0) != 0) return;
                if (fault == "mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(5).AddMilliseconds(1);
                else if (fault == "membership") Rows(data, "memberships").UpdateOne(new BsonDocument("_id", 2201L), Builders<BsonDocument>.Update.Set("status", "revoked"));
                else Rows(data, "authorizationGrants").UpdateOne(new BsonDocument("_id", fault == "owner" ? 2200L : 2201L), Builders<BsonDocument>.Update.Set("status", "revoked"));
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new StockSourceSetupService(db, new(client), new(db), data.Clock)
            .DecideOperatingPolicyAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic review", Meta("rollback")));
        Assert.Equal(1, changed); Assert.Equal(before, await Effects(data));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Distinct_reviewers_with_competing_snapshots_commit_exactly_one_policy_version(bool sameProposal)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var other = await data.AddActorAsync(3300, Owner.ToScope(), Permissions.StockSourcePolicyApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, other.UserId);
        var first = (await Propose(data, pool, maker))["proposalId"].AsInt64;
        var second = sameProposal ? first : (await Propose(data, pool, maker, "other"))["proposalId"].AsInt64;
        var before = (await Pool(data, pool))["fence"].AsInt64;
        using var barrier = new Barrier(2); var arrivals = 0;
        StockSourceSetupService Racing()
        {
            var captured = 0; var held = 0;
            var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "PolicyRace-" + Guid.NewGuid().ToString("N");
            settings.ClusterConfigurator = builder =>
            {
                builder.Subscribe<CommandStartedEvent>(e =>
                {
                    if (e.CommandName == "find" && e.Command.GetValue("find", "") == "inventoryPools" && e.Command.Contains("projection")) captured = e.RequestId;
                });
                builder.Subscribe<CommandSucceededEvent>(e =>
                {
                    if (e.RequestId != captured || Interlocked.CompareExchange(ref held, 1, 0) != 0) return;
                    Interlocked.Increment(ref arrivals);
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Both policy reviewers must capture the original snapshot.");
                });
            };
            var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
            return new(db, new(client), new(db), data.Clock);
        }
        async Task<bool> Attempt(StockSourceSetupService candidate, ActorContext actor, long id, string key)
        {
            try { await candidate.DecideOperatingPolicyAsync(actor, id, 1, StockSourceSetupDecision.Approve, "Synthetic competing review", Meta(key)); return true; }
            catch (PersistenceConflictException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Attempt(Racing(), reviewer, first, "one")), Task.Run(() => Attempt(Racing(), other, second, "two")));
        Assert.Equal(2, arrivals); Assert.Single(results, value => value);
        Assert.Equal(before + 1, (await Pool(data, pool))["fence"].AsInt64);
        Assert.Equal(1L, await Rows(data, StockSourcePolicyBson.Policies).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await Rows(data, StockSourcePolicyBson.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, (await service.ReadOperatingPolicyAsync(maker, pool))["policy"]["policyRevision"].AsInt64);
    }

    [Fact]
    public async Task Source_change_committed_during_review_forces_retry_and_rejects_older_snapshot()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var proposed = await Propose(data, pool, maker);
        var settingsProposal = await service.ProposeRevisionAsync(maker, pool, 1, Source() with { SafetyBufferBaseUnits = 5 }, Meta("settings"));
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); var captured = 0; var held = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "PolicySourceRace-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "find" && e.Command.GetValue("find", "") == "inventoryPools" && e.Command.Contains("projection")) captured = e.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.RequestId != captured || Interlocked.CompareExchange(ref held, 1, 0) != 0) return;
                loaded.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Source change must commit before policy review resumes.");
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var pending = Task.Run(() => new StockSourceSetupService(db, new(client), new(db), data.Clock)
            .DecideOperatingPolicyAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic old review", Meta("older")));
        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await service.DecideRevisionAsync(reviewer, settingsProposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic settings", Meta("settings-approved"));
        }
        finally { release.Set(); }
        await Assert.ThrowsAsync<PersistenceConflictException>(() => pending);
        Assert.Equal(1, held); Assert.Equal(2L, (await Pool(data, pool))["revision"].AsInt64);
        Assert.Equal(0L, await Rows(data, StockSourcePolicyBson.Policies).CountDocumentsAsync(new BsonDocument()));
        Assert.False(await Rows(data, "idempotencyRecords").Find(new BsonDocument("key", "older")).AnyAsync());
    }

    [Fact]
    public async Task Receipt_removed_after_recovery_read_never_reexecutes_approval()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var proposed = await Propose(data, pool, maker);
        await service.DecideOperatingPolicyAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic review", Meta("lost"));
        var poolBefore = await Pool(data, pool); var removed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "PolicyRecoveryRace-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(e =>
        {
            if (e.CommandName != "commitTransaction" || Interlocked.CompareExchange(ref removed, 1, 0) != 0) return;
            Rows(data, "idempotencyRecords").DeleteOne(new BsonDocument { { "operation", "stock.source.policy.approve" }, { "key", "lost" } });
        });
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAsync<DomainValidationException>(() => new StockSourceSetupService(db, new(client), new(db), data.Clock)
            .RecoverOperatingPolicyAsync(reviewer, pool, "stock.source.policy.approve", "lost", "recover"));
        Assert.Equal(1, removed); Assert.Equal(poolBefore, await Pool(data, pool));
        Assert.Equal(1L, await Rows(data, StockSourcePolicyBson.Policies).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await Rows(data, StockSourcePolicyBson.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.False(await Rows(data, "idempotencyRecords").Find(new BsonDocument("key", "lost")).AnyAsync());
    }
}