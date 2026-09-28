using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceRevisionTests
{
    private static async Task<BsonDocument> RevisionBusinessState(StoreTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "inventoryPools", "stockSourceRevisionProposals", "stockSourceRevisionDecisions", "stockSourceRevisions", "auditEvents" })
            result.Add(name, new BsonArray(await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return result;
    }

    [Theory]
    [InlineData("propose")] [InlineData("approve")] [InlineData("reject")] [InlineData("withdraw")]
    public async Task Revision_receipt_deleted_between_lookup_and_replay_never_dispatches_a_new_writer(string action)
    {
        await using var data = await Data(); var normal = Service(data); var (maker, reviewer, pool) = await Setup(data, normal);
        var proposed = await Propose(normal, maker, pool, action == "propose" ? "target" : "revision");
        var actor = action is "approve" or "reject" ? reviewer : maker;
        if (action != "propose") await normal.DecideRevisionAsync(actor, proposed["proposalId"].AsInt64, 1,
            action == "approve" ? StockSourceSetupDecision.Approve : action == "reject" ? StockSourceSetupDecision.Reject : StockSourceSetupDecision.Withdraw,
            "synthetic-review", Meta("target"));
        var operation = "stock.source.revision." + action;
        var before = await RevisionBusinessState(data); var removed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "RevisionRecoveryRace-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(e =>
        {
            var counter = action == "propose" ? "stockSourceRevisionProposals" : "stockSourceRevisionDecisions";
            if (e.CommandName == "findAndModify" && e.Command.GetValue("findAndModify", "") == "idCounters" &&
                e.Command["query"].AsBsonDocument.GetValue("_id", "") == counter && Interlocked.CompareExchange(ref removed, 1, 0) == 0)
                Rows(data, "idempotencyRecords").DeleteOne(new BsonDocument { { "actorId", actor.UserId }, { "operation", operation }, { "key", "target" } });
        });
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new StockSourceSetupService(database, new(client), new(database), data.Clock);
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => service.RecoverRevisionAsync(actor, Location, operation, "target", "recovery"));
        Assert.Equal("operation_receipt_unavailable", error.Code); Assert.Equal(1, removed);
        Assert.Equal(before, await RevisionBusinessState(data));
        Assert.False(await Rows(data, "idempotencyRecords").Find(new BsonDocument { { "operation", operation }, { "key", "target" } }).AnyAsync());
    }

    [Theory]
    [InlineData(StockSourceSetupDecision.Reject)] [InlineData(StockSourceSetupDecision.Withdraw)]
    public async Task Revision_recovery_preserves_terminal_nonapproval_without_creating_settings(StockSourceSetupDecision decision)
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        var proposal = await Propose(service, maker, pool);
        var actor = decision == StockSourceSetupDecision.Withdraw ? maker : reviewer;
        var result = await service.DecideRevisionAsync(actor, proposal["proposalId"].AsInt64, 1, decision, "synthetic-review", Meta("decision"));
        Assert.Equal(result, await service.RecoverRevisionAsync(actor, Location, "stock.source.revision." + decision.ToString().ToLowerInvariant(), "decision", "recovery"));
        Assert.True(result["poolRevision"].IsBsonNull); Assert.Equal(0, await Count(data, "stockSourceRevisions"));
        Assert.Equal(4, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData("hash")] [InlineData("result")] [InlineData("participants")]
    [InlineData("proposal-audit")] [InlineData("decision-audit")] [InlineData("applied")]
    public async Task Revision_recovery_rejects_changed_saved_evidence(string fault)
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        var proposed = await Propose(service, maker, pool); await Approve(service, reviewer, proposed["proposalId"].AsInt64);
        var receipt = new BsonDocument("operation", "stock.source.revision.approve");
        switch (fault)
        {
            case "hash": await Rows(data, "idempotencyRecords").UpdateOneAsync(receipt, Builders<BsonDocument>.Update.Set("bodyHash", new string('0', 64))); break;
            case "result": await Rows(data, "idempotencyRecords").UpdateOneAsync(receipt, Builders<BsonDocument>.Update.Set("result.poolId", 9000L)); break;
            case "participants": await Rows(data, "idempotencyRecords").UpdateOneAsync(receipt, Builders<BsonDocument>.Update.Set("personApproval.approval.proposedBy", 9000L)); break;
            case "proposal-audit": case "decision-audit":
                await Rows(data, "auditEvents").DeleteOneAsync(new BsonDocument("operation", fault == "proposal-audit" ? "stock.source.revision.propose" : "stock.source.revision.approve")); break;
            case "applied": await Rows(data, "stockSourceRevisions").DeleteManyAsync(new BsonDocument()); break;
        }
        var before = await RevisionBusinessState(data);
        await Assert.ThrowsAsync<DomainValidationException>(() => service.RecoverRevisionAsync(reviewer, Location, "stock.source.revision.approve", "approval", "recovery"));
        Assert.Equal(before, await RevisionBusinessState(data)); Assert.Equal(4, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData("revoked")] [InlineData("mfa")] [InlineData("person")]
    public async Task Revision_recovery_rechecks_current_authority_after_the_first_lookup(string change)
    {
        await using var data = await Data(); var normal = Service(data); var (maker, reviewer, pool) = await Setup(data, normal);
        var proposed = await Propose(normal, maker, pool); await Approve(normal, reviewer, proposed["proposalId"].AsInt64);
        var before = await RevisionBusinessState(data); var changed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "RevisionRecoveryAuthority-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(e =>
        {
            if (e.CommandName != "findAndModify" || e.Command.GetValue("findAndModify", "") != "idCounters" ||
                e.Command["query"].AsBsonDocument.GetValue("_id", "") != "stockSourceRevisionDecisions" || Interlocked.CompareExchange(ref changed, 1, 0) != 0) return;
            if (change == "revoked") Rows(data, "authorizationGrants").UpdateOne(new BsonDocument("_id", reviewer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L));
            else if (change == "mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
            else Rows(data, "personAccountBindings").DeleteMany(new BsonDocument());
        });
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new StockSourceSetupService(database, new(client), new(database), data.Clock);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverRevisionAsync(reviewer, Location, "stock.source.revision.approve", "approval", "recovery"));
        Assert.Equal(1, changed); Assert.Equal(before, await RevisionBusinessState(data)); Assert.Equal(4, await Count(data, "idempotencyRecords"));
    }

    [Fact]
    public async Task Revision_history_scope_filters_hide_even_malformed_foreign_history_and_recovery_keeps_actor_isolation()
    {
        await using var data = await Data(); var service = Service(data); var (maker, _, pool) = await Setup(data, service);
        await Propose(service, maker, pool);
        // A foreign row with no audit must never be hydrated for this location, even when IDs are known.
        var foreignPool = await Rows(data, "inventoryPools").Find(new BsonDocument()).SingleAsync();
        foreignPool["_id"] = 9000L; foreignPool["location"]["channelId"] = 104L;
        await Rows(data, "inventoryPools").InsertOneAsync(foreignPool);
        var foreignProposal = await Rows(data, "stockSourceRevisionProposals").Find(new BsonDocument()).SingleAsync();
        foreignProposal["_id"] = 9000L; foreignProposal["poolId"] = 9000L;
        foreignProposal["input"]["location"]["channelId"] = 104L; foreignProposal["baseInput"]["location"]["channelId"] = 104L;
        await Rows(data, "stockSourceRevisionProposals").InsertOneAsync(foreignProposal);
        Assert.Equal(1, (await service.ListSourcesAsync(maker, Location, 0, 100)).Total);
        Assert.Equal(1, (await service.ListRevisionsAsync(maker, Location, null, 0, 100)).Total);
        Assert.Equal(0, (await service.ListRevisionsAsync(maker, Location, 9000, 0, 100)).Total);
        Assert.Null(await service.ReadRevisionAsync(maker, Location, 9000));
        var other = await data.AddActorAsync(3000, Location.ToScope(), Permissions.StockSourcePropose);
        Assert.Null(await service.RecoverRevisionAsync(other, Location, "stock.source.revision.propose", "revision", "recovery"));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.RecoverRevisionAsync(maker, Location, "stock.source.propose", "revision", "recovery"));
        await Rows(data, "authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)));
        var foreignLocation = Location with { ChannelId = new(104) };
        await data.AddGrantAsync(maker, 1001, foreignLocation.ToScope(), [Permissions.StockSourcePropose]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverRevisionAsync(maker, foreignLocation, "stock.source.revision.propose", "revision", "recovery"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListSourcesAsync(other, Location, 0, 10));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListRevisionsAsync(other, Location, null, 0, 10));
    }
}
