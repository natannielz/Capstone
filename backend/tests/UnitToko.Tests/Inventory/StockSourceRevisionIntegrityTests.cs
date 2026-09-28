using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceRevisionTests
{
    [Fact]
    public async Task A_new_proposal_cannot_predate_the_current_approved_settings()
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        var id = (await Propose(service, maker, pool))["proposalId"].AsInt64;
        var earlier = data.Clock.UtcNow;
        data.Clock.UtcNow = earlier.AddMinutes(1);
        await Approve(service, reviewer, id);
        data.Clock.UtcNow = earlier;
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ProposeRevisionAsync(maker, pool, 2,
            Changed() with { Label = "Backdated proposal" }, Meta("backdated")));
        Assert.Equal(1, await Count(data, "stockSourceRevisionProposals")); Assert.Equal(4, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData("revision")] [InlineData("proposal-audit")] [InlineData("decision-audit")]
    [InlineData("registration")] [InlineData("current-input")] [InlineData("original-audit")] [InlineData("pool-rewind")] [InlineData("pool-skip")]
    public async Task Unusable_current_provenance_cannot_supply_settings_or_accept_a_new_proposal(string fault)
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        var id = (await Propose(service, maker, pool))["proposalId"].AsInt64;
        await Approve(service, reviewer, id);
        switch (fault)
        {
            case "revision": await Rows(data, "stockSourceRevisions").DeleteManyAsync(new BsonDocument()); break;
            case "proposal-audit": case "decision-audit": case "original-audit":
                var operation = fault == "original-audit" ? "stock.source.propose" :
                    fault == "proposal-audit" ? "stock.source.revision.propose" : "stock.source.revision.approve";
                await Rows(data, "auditEvents").DeleteOneAsync(new BsonDocument("operation", operation)); break;
            case "registration": await Rows(data, "inventoryPoolRegistrations").DeleteOneAsync(new BsonDocument("_id", pool)); break;
            case "current-input": await Rows(data, "stockSourceRevisions").UpdateOneAsync(new BsonDocument("poolId", pool),
                Builders<BsonDocument>.Update.Set("input.label", "Unapproved label")); break;
            case "pool-rewind": case "pool-skip": await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument("_id", pool),
                Builders<BsonDocument>.Update.Set("revision", fault == "pool-rewind" ? 1L : 3L)); break;
        }
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ReadCurrentSourceAsync(maker, Location, pool));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ProposeRevisionAsync(maker, pool, 2,
            Changed() with { Label = "Next settings" }, Meta("next")));
        Assert.Equal(1, await Count(data, "stockSourceRevisionProposals")); Assert.Equal(4, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData("no-change")] [InlineData("owner-change")] [InlineData("key-change")] [InlineData("fractional-version")]
    [InlineData("overflow-version")] [InlineData("unknown-field")] [InlineData("invalid-source")]
    public async Task Database_rejects_invalid_revision_proposals_without_relying_on_application_validation(string fault)
    {
        await using var data = await Data(); var service = Service(data); var (maker, _, pool) = await Setup(data, service);
        await Propose(service, maker, pool);
        var row = await Rows(data, "stockSourceRevisionProposals").Find(new BsonDocument()).SingleAsync();
        row["_id"] = 9000L;
        switch (fault)
        {
            case "no-change": row["input"] = row["baseInput"].DeepClone(); break;
            case "owner-change": row["input"]["location"]["channelId"] = 104L; break;
            case "key-change": row["input"]["physicalKey"] = "another-pool"; break;
            case "fractional-version": row["basePoolRevision"] = 1.5; break;
            case "overflow-version": row["basePoolRevision"] = long.MaxValue; break;
            case "unknown-field": row.Add("extra", true); break;
            case "invalid-source": row["input"]["mode"] = "ExternalLedger"; break;
        }
        var error = await Assert.ThrowsAsync<MongoWriteException>(() => Rows(data, "stockSourceRevisionProposals").InsertOneAsync(row));
        Assert.Equal(121, error.WriteError.Code); Assert.Equal(1, await Count(data, "stockSourceRevisionProposals"));
    }

    [Fact]
    public async Task Two_approvals_started_from_the_same_snapshot_cannot_both_commit()
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        var first = (await Propose(service, maker, pool, "first"))["proposalId"].AsInt64;
        var second = (await service.ProposeRevisionAsync(maker, pool, 1, Changed() with { Label = "Second candidate" }, Meta("second")))["proposalId"].AsInt64;
        using var barrier = new Barrier(2);
        var arrivals = 0;
        StockSourceSetupService Racing()
        {
            var captured = 0; var held = 0;
            var settings = fixture.Client.Settings.Clone();
            settings.ApplicationName = "SourceRevisionRace-" + Guid.NewGuid().ToString("N");
            settings.ClusterConfigurator = builder =>
            {
                builder.Subscribe<CommandStartedEvent>(e =>
                {
                    if (e.CommandName == "find" && e.Command.GetValue("find", "") == "stockSourceRevisionProposals" && e.Command.Contains("projection")) captured = e.RequestId;
                });
                builder.Subscribe<CommandSucceededEvent>(e =>
                {
                    if (e.RequestId != captured || Interlocked.CompareExchange(ref held, 1, 0) != 0) return;
                    Interlocked.Increment(ref arrivals);
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Both approval transactions must start before the authorization write fences.");
                });
            };
            var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
            return new(database, new(client), new(database), data.Clock);
        }
        async Task<bool> Attempt(StockSourceSetupService candidate, long id, string key)
        {
            try { await Approve(candidate, reviewer, id, key); return true; }
            catch (PersistenceConflictException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Attempt(Racing(), first, "approve-first")),
            Task.Run(() => Attempt(Racing(), second, "approve-second")));
        Assert.Equal(2, arrivals); Assert.Single(results, value => value);
        Assert.Equal(1, await Count(data, "stockSourceRevisions")); Assert.Equal(1, await Count(data, "stockSourceRevisionDecisions"));
        Assert.Equal(2, (await service.ReadCurrentSourceAsync(maker, Location, pool))["pool"]["revision"].AsInt64);
        Assert.Equal(5, await Count(data, "idempotencyRecords")); Assert.Equal(5, await Count(data, "auditEvents"));
    }

    [Theory]
    [InlineData("revoke")] [InlineData("mfa-expiry")]
    public async Task Authorization_changed_after_business_work_rolls_back_the_whole_approval(string change)
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        var id = (await Propose(service, maker, pool))["proposalId"].AsInt64;
        var before = await Rows(data, "inventoryPools").Find(new BsonDocument("_id", pool)).SingleAsync();
        var captured = 0; var changed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "SourceRevisionAuthority-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "insert" && e.Command.GetValue("insert", "") == "auditEvents" &&
                    e.Command["documents"].AsBsonArray[0]["operation"] == "stock.source.revision.approve") captured = e.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.RequestId != captured || Interlocked.CompareExchange(ref changed, 1, 0) != 0) return;
                if (change == "mfa-expiry") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(5).AddMilliseconds(1);
                else Rows(data, "authorizationGrants").UpdateOne(new BsonDocument("_id", reviewer.UserId),
                    Builders<BsonDocument>.Update.Set("status", "revoked"));
            });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var guarded = new StockSourceSetupService(database, new(client), new(database), data.Clock);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Approve(guarded, reviewer, id));
        Assert.Equal(1, changed);
        Assert.Equal(before, await Rows(data, "inventoryPools").Find(new BsonDocument("_id", pool)).SingleAsync());
        Assert.Equal(0, await Count(data, "stockSourceRevisions")); Assert.Equal(0, await Count(data, "stockSourceRevisionDecisions"));
        Assert.Equal(3, await Count(data, "auditEvents")); Assert.Equal(3, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData("missing-earlier-revision")]
    [InlineData("predecessor-audit")]
    [InlineData("predecessor-input")]
    public async Task Current_settings_require_an_unbroken_revision_sequence_and_the_actual_approved_predecessor(string fault)
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        var first = (await Propose(service, maker, pool))["proposalId"].AsInt64;
        await Approve(service, reviewer, first);
        var nextInput = Changed() with { Label = "Third source version" };
        var second = (await service.ProposeRevisionAsync(maker, pool, 2, nextInput, Meta("second")))["proposalId"].AsInt64;
        await Approve(service, reviewer, second, "second-approval");
        if (fault == "missing-earlier-revision")
            await Rows(data, "stockSourceRevisions").DeleteOneAsync(new BsonDocument { { "poolId", pool }, { "poolRevision", 2L } });
        else if (fault == "predecessor-audit")
            await Rows(data, "auditEvents").DeleteOneAsync(new BsonDocument { { "operation", "stock.source.revision.approve" }, { "record.proposalId", first } });
        else
            await Rows(data, "stockSourceRevisions").UpdateOneAsync(new BsonDocument { { "poolId", pool }, { "poolRevision", 2L } },
                Builders<BsonDocument>.Update.Set("input.label", "Unreviewed predecessor"));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ReadCurrentSourceAsync(maker, Location, pool));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ProposeRevisionAsync(maker, pool, 3,
            nextInput with { Label = "Fourth source version" }, Meta("third")));
        Assert.Equal(2, await Count(data, "stockSourceRevisionProposals")); Assert.Equal(6, await Count(data, "idempotencyRecords"));
    }
}
