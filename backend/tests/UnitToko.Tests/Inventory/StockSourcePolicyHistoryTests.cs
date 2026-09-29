using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourcePolicyServiceTests
{
    private static async Task RewriteProposal(StoreTestData data, BsonDocument row)
    {
        await Rows(data, StockSourcePolicyBson.Proposals).ReplaceOneAsync(new BsonDocument("_id", row["_id"]), row);
        await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", row["auditId"]),
            Builders<BsonDocument>.Update.Set("record", row.DeepClone()).Set("createdAt", row["proposedAt"]));
        var body = new { poolId = row["poolId"].AsInt64, expected = StockSourcePolicyBson.Expected(row),
            input = StockSourcePolicyBson.ReadInput(row["input"].AsBsonDocument, StockSourcePolicyBson.Source(row)) };
        await Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument {
            { "operation", "stock.source.policy.propose" }, { "result.proposalId", row["_id"] } },
            Builders<BsonDocument>.Update.Set("bodyHash", AuthorizationStore.CommandHash(body, row["reason"].AsString)));
    }

    [Theory]
    [InlineData("audit")] [InlineData("receipt")] [InlineData("policy")] [InlineData("hash")]
    [InlineData("source")] [InlineData("extra-audit")] [InlineData("extra-receipt")] [InlineData("decision")]
    public async Task Incomplete_or_tampered_policy_graph_never_becomes_trusted_history_or_recovery(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var proposed = await Propose(data, pool, maker);
        await service.DecideOperatingPolicyAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic review", Meta("approve"));
        var proposal = await Rows(data, StockSourcePolicyBson.Proposals).Find(new BsonDocument()).SingleAsync();
        if (fault == "audit") await Rows(data, "auditEvents").DeleteOneAsync(new BsonDocument("_id", proposal["auditId"]));
        if (fault == "receipt") await Rows(data, "idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "stock.source.policy.propose"));
        if (fault == "policy") await Rows(data, StockSourcePolicyBson.Policies).DeleteManyAsync(new BsonDocument());
        if (fault == "decision") await Rows(data, StockSourcePolicyBson.Decisions).DeleteManyAsync(new BsonDocument());
        if (fault == "hash")
            await Rows(data, StockSourcePolicyBson.Proposals).UpdateOneAsync(new BsonDocument("_id", proposal["_id"]), Builders<BsonDocument>.Update.Set("configurationHash", new string('a', 64)));
        if (fault == "source")
        {
            proposal["configuration"]["source"]["input"]["label"] = "Substituted source";
            proposal["configurationHash"] = StockSourceSuspensionBson.Hash(proposal["configuration"].AsBsonDocument);
            await RewriteProposal(data, proposal);
        }
        if (fault == "extra-audit")
        {
            var audit = await Rows(data, "auditEvents").Find(new BsonDocument("_id", proposal["auditId"])).SingleAsync();
            audit["_id"] = 99999L; audit["kind"] = "foreign.audit"; audit["operation"] = "foreign.operation";
            await Rows(data, "auditEvents").InsertOneAsync(audit);
        }
        if (fault == "extra-receipt")
        {
            var receipt = await Rows(data, "idempotencyRecords").Find(new BsonDocument("operation", "stock.source.policy.approve")).SingleAsync();
            receipt.Remove("_id"); receipt["operation"] = "foreign.operation"; receipt["key"] = "copied";
            await Rows(data, "idempotencyRecords").InsertOneAsync(receipt);
        }
        var before = await Effects(data);
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ReadOperatingPolicyAsync(maker, pool));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.RecoverOperatingPolicyAsync(reviewer, pool, "stock.source.policy.approve", "approve", "recover"));
        Assert.Equal(before, await Effects(data));
    }

    [Theory]
    [InlineData("proposal-settings")] [InlineData("decision-settings")]
    [InlineData("decision-mapping")] [InlineData("decision-suspension")]
    public async Task Coherent_copies_cannot_claim_review_of_a_binding_already_superseded_at_that_time(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data, fault == "decision-mapping"); var service = Service(data);
        var proposed = await Propose(data, pool, maker);
        if (fault != "proposal-settings")
            await service.DecideOperatingPolicyAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic review", Meta("approve"));
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        if (fault.EndsWith("settings", StringComparison.Ordinal))
        {
            var changed = await service.ProposeRevisionAsync(maker, pool, 1, Source() with { SafetyBufferBaseUnits = 3 }, Meta("settings"));
            await service.DecideRevisionAsync(reviewer, changed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic change", Meta("changed"));
        }
        if (fault == "decision-mapping")
        {
            var changed = await service.ProposeAliasAsync(maker, pool, 1, Linked, 1, false, Meta("mapping"));
            await service.DecideAliasAsync(reviewer, changed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic change", Meta("changed"));
        }
        if (fault == "decision-suspension")
        {
            var preview = await service.PreviewSuspensionAsync(maker, pool);
            await service.SuspendAsync(maker, pool, new(preview["poolRevision"].AsInt64, preview["suspensionRevision"].AsInt64, preview["configurationHash"].AsString), Meta("stop"));
        }
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        if (fault == "proposal-settings")
        {
            var row = await Rows(data, StockSourcePolicyBson.Proposals).Find(new BsonDocument()).SingleAsync();
            row["proposedAt"] = data.Clock.UtcNow.UtcDateTime; await RewriteProposal(data, row);
        }
        else
        {
            var row = await Rows(data, StockSourcePolicyBson.Decisions).Find(new BsonDocument()).SingleAsync();
            row["decidedAt"] = data.Clock.UtcNow.UtcDateTime;
            await Rows(data, StockSourcePolicyBson.Decisions).ReplaceOneAsync(new BsonDocument("_id", row["_id"]), row);
            await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", row["auditId"]),
                Builders<BsonDocument>.Update.Set("record", row.DeepClone()).Set("createdAt", row["decidedAt"]));
        }
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ReadOperatingPolicyAsync(maker, pool));
    }

    [Fact]
    public async Task Enabled_alias_must_be_rebound_before_a_new_policy_and_disabled_history_retains_authority_scope()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data, true); var service = Service(data);
        var changed = await service.ProposeRevisionAsync(maker, pool, 1, Source() with { SafetyBufferBaseUnits = 4 }, Meta("settings"));
        await service.DecideRevisionAsync(reviewer, changed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic change", Meta("changed"));
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => service.PreviewOperatingPolicyAsync(maker, pool));
        var alias = await service.ProposeAliasAsync(maker, pool, 2, Linked, 1, false, Meta("disable"));
        await service.DecideAliasAsync(reviewer, alias["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic disable", Meta("disabled"));
        await service.PreviewOperatingPolicyAsync(maker, pool);
        await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 1101L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.PreviewOperatingPolicyAsync(maker, pool));
    }

    [Fact]
    public async Task Active_flag_is_never_a_substitute_for_stopped_policy_review()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var preview = await service.PreviewOperatingPolicyAsync(maker, pool);
        var proposal = await Propose(data, pool, maker);
        await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument("_id", pool), Builders<BsonDocument>.Update.Set("active", true));
        var before = await Effects(data);
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => service.PreviewOperatingPolicyAsync(maker, pool));
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => service.ProposeOperatingPolicyAsync(maker, pool, Expected(preview), Input(), Meta("active")));
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => service.DecideOperatingPolicyAsync(reviewer,
            proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic review", Meta("active-review")));
        Assert.Equal(before, await Effects(data));
    }
}