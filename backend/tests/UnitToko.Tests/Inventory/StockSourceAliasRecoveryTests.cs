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

public sealed partial class StockSourceAliasTests
{
    [Fact]
    public async Task Receipt_only_recovery_returns_original_results_after_later_versions_without_history_permission()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        foreach (var name in new[] { "stockSourceAliasProposals", "stockSourceAliasDecisions", "stockSourceAliases" })
            await Rows(data, "idCounters").UpdateOneAsync(new BsonDocument("_id", name),
                Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        var proposed = await Propose(service, maker, pool);
        Assert.True(proposed["proposalId"].IsInt64); Assert.Equal(9007199254740993L, proposed["proposalId"].AsInt64);
        var approved = await Approve(service, reviewer, proposed["proposalId"].AsInt64);
        var disabled = await Propose(service, maker, pool, "disable", 1, false);
        await Approve(service, reviewer, disabled["proposalId"].AsInt64, "approve-disable");
        foreach (var pair in new[] { (1000L, Permissions.StockSourcePropose), (1001L, Permissions.StockSourcePropose),
            (2000L, Permissions.StockSourceApprove), (2001L, Permissions.StockSourceApprove) })
            await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", pair.Item1),
                Builders<BsonDocument>.Update.Set("permissions", new BsonArray { pair.Item2 }));
        Assert.Equal(proposed, await service.RecoverAliasAsync(maker, pool, Requested, "stock.source.alias.propose", "alias", "recovery"));
        Assert.Equal(approved, await service.RecoverAliasAsync(reviewer, pool, Requested, "stock.source.alias.approve", "approve", "recovery"));
        Assert.Null(await service.RecoverAliasAsync(maker, pool, Requested, "stock.source.alias.propose", "missing", "recovery"));
        Assert.Null(await service.RecoverAliasAsync(maker, pool, Requested, "stock.source.alias.propose", "someone-elses-key", "recovery"));
        Assert.Equal(2L, await Count(data, "stockSourceAliasProposals")); Assert.Equal(2L, await Count(data, "stockSourceAliases"));
        Assert.Equal(6L, await Count(data, "auditEvents")); Assert.Equal(6L, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData(StockSourceSetupDecision.Reject)] [InlineData(StockSourceSetupDecision.Withdraw)]
    public async Task Receipt_only_recovery_preserves_terminal_nonapproval(StockSourceSetupDecision decision)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var proposal = await Propose(service, maker, pool); var actor = decision == StockSourceSetupDecision.Withdraw ? maker : reviewer;
        var result = await service.DecideAliasAsync(actor, proposal["proposalId"].AsInt64, 1, decision, "synthetic-reference", Meta("terminal"));
        Assert.Equal(result, await service.RecoverAliasAsync(actor, pool, Requested, "stock.source.alias." + decision.ToString().ToLowerInvariant(), "terminal", "recovery"));
        Assert.Equal(0L, await Count(data, "stockSourceAliases")); Assert.Equal(4L, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData("propose")] [InlineData("approve")] [InlineData("reject")] [InlineData("withdraw")]
    public async Task A_receipt_deleted_between_lookup_and_replay_cannot_create_replacement_work(string outcome)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        foreach (var name in new[] { "stockSourceAliasProposals", "stockSourceAliasDecisions", "stockSourceAliases" })
            await Rows(data, "idCounters").UpdateOneAsync(new BsonDocument("_id", name),
                Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        var proposed = await Propose(service, maker, pool);
        Assert.True(proposed["proposalId"].IsInt64); Assert.Equal(9007199254740993L, proposed["proposalId"].AsInt64); var actor = maker; var key = "alias";
        if (outcome != "propose")
        {
            var decision = outcome switch { "approve" => StockSourceSetupDecision.Approve, "reject" => StockSourceSetupDecision.Reject, _ => StockSourceSetupDecision.Withdraw };
            actor = decision == StockSourceSetupDecision.Withdraw ? maker : reviewer; key = "decision";
            await service.DecideAliasAsync(actor, proposed["proposalId"].AsInt64, 1, decision, "synthetic-review", Meta(key));
        }
        var operation = "stock.source.alias." + outcome;
        var beforeProposals = await Count(data, "stockSourceAliasProposals"); var beforeDecisions = await Count(data, "stockSourceAliasDecisions");
        var beforeAliases = await Count(data, "stockSourceAliases"); var beforeAudits = await Count(data, "auditEvents"); var beforeReceipts = await Count(data, "idempotencyRecords");
        var observed = 0; var deleted = 0; var captured = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "AliasRecoveryRace-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "find" && e.Command.GetValue("find", "") == "idempotencyRecords") captured = e.RequestId;
                if (e.CommandName == "listCollections" && Volatile.Read(ref observed) == 1 && Interlocked.CompareExchange(ref deleted, 1, 0) == 0)
                    Rows(data, "idempotencyRecords").DeleteOne(new BsonDocument { { "actorId", actor.UserId }, { "operation", operation }, { "key", key } });
            });
            builder.Subscribe<CommandSucceededEvent>(e => { if (e.RequestId == captured) Volatile.Write(ref observed, 1); });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var guarded = new StockSourceSetupService(database, new(client), new(database), data.Clock);
        Assert.Equal("operation_receipt_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(
            () => guarded.RecoverAliasAsync(actor, pool, Requested, operation, key, "recovery"))).Code);
        Assert.Equal(1, deleted); Assert.Equal(beforeProposals, await Count(data, "stockSourceAliasProposals"));
        Assert.Equal(beforeDecisions, await Count(data, "stockSourceAliasDecisions")); Assert.Equal(beforeAliases, await Count(data, "stockSourceAliases"));
        Assert.Equal(beforeAudits, await Count(data, "auditEvents")); Assert.Equal(beforeReceipts - 1, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData("result")] [InlineData("body")] [InlineData("wrong-pool")] [InlineData("wrong-channel")] [InlineData("revoked")]
    public async Task Recovery_rejects_corrupt_receipts_foreign_target_and_lost_authority(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var proposal = await Propose(service, maker, pool); await Approve(service, reviewer, proposal["proposalId"].AsInt64);
        var filter = new BsonDocument { { "actorId", reviewer.UserId }, { "operation", "stock.source.alias.approve" }, { "key", "approve" } };
        if (fault == "result") await Rows(data, "idempotencyRecords").UpdateOneAsync(filter, Builders<BsonDocument>.Update.Set("result.aliasRevision", 99L));
        if (fault == "body") await Rows(data, "idempotencyRecords").UpdateOneAsync(filter, Builders<BsonDocument>.Update.Set("bodyHash", new string('0', 64)));
        if (fault == "revoked") await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 2001L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        var actualPool = fault == "wrong-pool" ? pool + 999 : pool;
        var actualLocation = fault == "wrong-channel" ? Requested with { ChannelId = new(999) } : Requested;
        var failure = await Record.ExceptionAsync(() => service.RecoverAliasAsync(reviewer, actualPool, actualLocation, "stock.source.alias.approve", "approve", "recovery"));
        Assert.True(failure is DomainValidationException or PersistenceConflictException or AuthorizationDeniedException);
        Assert.Equal(4L, await Count(data, "idempotencyRecords")); Assert.Equal(4L, await Count(data, "auditEvents"));
    }
}
