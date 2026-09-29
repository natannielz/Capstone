using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourcePolicyMigrationTests
{
    private async Task Link(StoreTestData data, Actors actors, long poolRevision = 1)
    {
        var linked = Owner with { ChannelId = new(104) };
        await Rows(data, "authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)));
        await data.AddGrantAsync(actors.Maker, 1101, linked.ToScope(), [Permissions.StockSourcePropose, Permissions.StockSourceRead, Permissions.StockSourcePolicyPropose, Permissions.StockSourceSuspend]);
        await data.AddGrantAsync(actors.Reviewer, 2201, linked.ToScope(), [Permissions.StockSourceApprove, Permissions.StockSourceRead, Permissions.StockSourcePolicyApprove]);
        var service = Service(data);
        var alias = await service.ProposeAliasAsync(actors.Maker, actors.Pool, poolRevision, linked, 0, true, Meta("mapping"));
        await service.DecideAliasAsync(actors.Reviewer, alias["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic mapping", Meta("mapped"));
    }
    private async Task<long> Next(StoreTestData data, Actors actors, string key)
    {
        var service = Service(data); var preview = await service.PreviewOperatingPolicyAsync(actors.Maker, actors.Pool);
        var proposal = await service.ProposeOperatingPolicyAsync(actors.Maker, actors.Pool, new(preview["poolRevision"].AsInt64,
            preview["suspensionRevision"].AsInt64, preview["policyRevision"].AsInt64, preview["configurationHash"].AsString),
            new(StockMovementWriter.UnitToko, null, 0, "Synthetic reconciliation " + key, "Synthetic outage " + key), Meta(key));
        await service.DecideOperatingPolicyAsync(actors.Reviewer, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic review", Meta(key + "-accepted"));
        return proposal["proposalId"].AsInt64;
    }
    [Theory]
    [InlineData("lost-channel")] [InlineData("unchanged")]
    public async Task Accepted_version_order_preserves_known_history_even_when_all_timestamps_match(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data);
        var first = await Rows(data, StockSourcePolicyBson.Proposals).Find(new BsonDocument("_id", actors.Proposal)).SingleAsync();
        if (fault == "lost-channel") { await Link(data, actors); await Next(data, actors, "linked-policy"); }
        var id = await Next(data, actors, "next");
        var row = await Rows(data, StockSourcePolicyBson.Proposals).Find(new BsonDocument("_id", id)).SingleAsync();
        if (fault == "lost-channel") row["configuration"]["mappings"] = new BsonArray();
        else row["input"] = first["input"].DeepClone();
        await ReplaceProposal(data, row);
        await Reject(data);
        await Assert.ThrowsAsync<DomainValidationException>(() => Service(data).ReadOperatingPolicyAsync(actors.Maker, actors.Pool));
    }
    [Theory]
    [InlineData("settings")] [InlineData("mapping")] [InlineData("suspension")]
    public async Task Coherent_approval_timestamp_cannot_claim_already_replaced_binding(string change)
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data); var service = Service(data);
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        if (change == "settings")
        {
            var revised = await service.ProposeRevisionAsync(actors.Maker, actors.Pool, 1, actors.Source with { SafetyBufferBaseUnits = 4 }, Meta("settings"));
            await service.DecideRevisionAsync(actors.Reviewer, revised["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic settings", Meta("settings-approved"));
        }
        if (change == "mapping") await Link(data, actors);
        if (change == "suspension")
        {
            var preview = await service.PreviewSuspensionAsync(actors.Maker, actors.Pool);
            await service.SuspendAsync(actors.Maker, actors.Pool, new(preview["poolRevision"].AsInt64, preview["suspensionRevision"].AsInt64, preview["configurationHash"].AsString), Meta("stop"));
        }
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        var row = await Rows(data, StockSourcePolicyBson.Decisions).Find(new BsonDocument()).SingleAsync();
        row["decidedAt"] = data.Clock.UtcNow.UtcDateTime;
        await Rows(data, StockSourcePolicyBson.Decisions).ReplaceOneAsync(new BsonDocument("_id", row["_id"]), row);
        await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", row["auditId"]),
            Builders<BsonDocument>.Update.Set("record", row.DeepClone()).Set("createdAt", row["decidedAt"]));
        await Reject(data);
    }
    [Fact]
    public async Task Legitimate_later_settings_mapping_stop_and_policy_preserve_both_old_and_new_provenance()
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data); var service = Service(data);
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        var revised = await service.ProposeRevisionAsync(actors.Maker, actors.Pool, 1, actors.Source with { SafetyBufferBaseUnits = 4 }, Meta("settings"));
        await service.DecideRevisionAsync(actors.Reviewer, revised["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic settings", Meta("settings-approved"));
        await Link(data, actors, 2);
        var preview = await service.PreviewSuspensionAsync(actors.Maker, actors.Pool);
        await service.SuspendAsync(actors.Maker, actors.Pool, new(preview["poolRevision"].AsInt64, preview["suspensionRevision"].AsInt64, preview["configurationHash"].AsString), Meta("stop"));
        await Next(data, actors, "next");
        var before = await Business(data);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await Business(data));
        Assert.Equal(2L, (await service.ReadOperatingPolicyAsync(actors.Maker, actors.Pool))["policy"]["policyRevision"].AsInt64);
    }
    [Fact]
    public async Task Policy_preflight_reaches_corruption_beyond_first_cursor_batch()
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        var original = await Rows(data, StockSourcePolicyBson.Proposals).Find(new BsonDocument()).SingleAsync();
        var originalAudit = await Rows(data, "auditEvents").Find(new BsonDocument("_id", original["auditId"])).SingleAsync();
        var originalReceipt = await Rows(data, "idempotencyRecords").Find(new BsonDocument("operation", "stock.source.policy.propose")).SingleAsync();
        for (var i = 1; i <= 64; i++)
        {
            // Exact synthetic pending proposals could all precede the first acceptance in this same millisecond.
            var proposal = original.DeepClone().AsBsonDocument; proposal["_id"] = 10000L + i; proposal["auditId"] = 20000L + i;
            var audit = originalAudit.DeepClone().AsBsonDocument; audit["_id"] = proposal["auditId"]; audit["record"] = proposal.DeepClone();
            var receipt = originalReceipt.DeepClone().AsBsonDocument; receipt["_id"] = ObjectId.GenerateNewId(); receipt["key"] = "batch-" + i;
            receipt["result"] = StockSourcePolicyBson.ProposalResult(proposal);
            await Rows(data, StockSourcePolicyBson.Proposals).InsertOneAsync(proposal);
            await Rows(data, "auditEvents").InsertOneAsync(audit);
            await Rows(data, "idempotencyRecords").InsertOneAsync(receipt);
        }
        Assert.Equal(65L, await Rows(data, StockSourcePolicyBson.Proposals).CountDocumentsAsync(new BsonDocument()));
        await Rows(data, "auditEvents").DeleteOneAsync(new BsonDocument("_id", 20064L));
        await Reject(data);
    }
}