using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceAliasMigrationTests
{
    [Theory]
    [InlineData("missing-alias")] [InlineData("missing-decision")] [InlineData("missing-proposal")]
    [InlineData("receipt-hash")] [InlineData("receipt-result")] [InlineData("receipt-participants")]
    [InlineData("missing-participants")] [InlineData("missing-actor")] [InlineData("original-receipt")]
    [InlineData("audit-record")] [InlineData("duplicate-audit")] [InlineData("orphan-audit")]
    [InlineData("duplicate-receipt")] [InlineData("orphan-receipt")] [InlineData("unknown-alias-operation")]
    [InlineData("unknown-source-operation")] [InlineData("misclassified-audit")]
    [InlineData("physical-key")] [InlineData("requested-channel-parent")] [InlineData("decision-channel")]
    [InlineData("active-pool")] [InlineData("unexplained-settings-head")] [InlineData("future-settings")]
    [InlineData("exhausted-counter")] [InlineData("source-counter")] [InlineData("malformed-record")]
    [InlineData("duplicate-version")] [InlineData("duplicate-decision")] [InlineData("extra-unique")] [InlineData("ttl")]
    public async Task Broken_graph_or_storage_stops_before_any_target_ddl_or_counter_change(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        var proposal = await Rows(data, "stockSourceAliasProposals").Find(new BsonDocument()).SingleAsync();
        var decision = await Rows(data, "stockSourceAliasDecisions").Find(new BsonDocument()).SingleAsync();
        switch (fault)
        {
            case "missing-alias": await Rows(data, "stockSourceAliases").DeleteManyAsync(new BsonDocument()); break;
            case "missing-decision": await Rows(data, "stockSourceAliasDecisions").DeleteManyAsync(new BsonDocument()); break;
            case "missing-proposal": await Rows(data, "stockSourceAliasProposals").DeleteManyAsync(new BsonDocument()); break;
            case "receipt-hash": await ReceiptChange("bodyHash", new string('0', 64)); break;
            case "receipt-result": await ReceiptChange("result.aliasRevision", 999L); break;
            case "receipt-participants": await ReceiptChange("personApproval.approval.proposedBy", 999L); break;
            case "missing-participants": await Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument("operation", "stock.source.alias.approve"), Builders<BsonDocument>.Update.Unset("personApproval")); break;
            case "missing-actor": await Rows(data, "identities").DeleteOneAsync(new BsonDocument("_id", 2000L)); break;
            case "original-receipt": await Rows(data, "idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "stock.source.approve")); break;
            case "audit-record": await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", decision["auditId"]),
                Builders<BsonDocument>.Update.Set("record.reason", "Changed reason")); break;
            case "duplicate-audit": case "orphan-audit": case "misclassified-audit":
                var audit = await Rows(data, "auditEvents").Find(new BsonDocument("_id", decision["auditId"])).SingleAsync();
                if (fault == "misclassified-audit")
                {
                    audit["kind"] = "stock.source.audit";
                    await Rows(data, "auditEvents").ReplaceOneAsync(new BsonDocument("_id", audit["_id"]), audit);
                }
                else
                {
                    audit["_id"] = 9999L;
                    if (fault == "orphan-audit") audit["record"]["_id"] = 9999L;
                    await Rows(data, "auditEvents").InsertOneAsync(audit);
                }
                break;
            case "duplicate-receipt": case "orphan-receipt": case "unknown-alias-operation": case "unknown-source-operation":
                var receipt = await Rows(data, "idempotencyRecords").Find(new BsonDocument("operation", "stock.source.alias.propose")).SingleAsync();
                receipt["_id"] = ObjectId.GenerateNewId(); receipt["key"] = "unexplained";
                if (fault == "orphan-receipt") receipt["result"]["proposalId"] = 9999L;
                if (fault == "unknown-alias-operation") receipt["operation"] = "stock.source.alias.unsupported";
                if (fault == "unknown-source-operation") receipt["operation"] = "stock.source.unsupported";
                await Rows(data, "idempotencyRecords").InsertOneAsync(receipt); break;
            case "physical-key":
                proposal["physicalKey"] = "unapproved-pool";
                await Rows(data, "stockSourceAliasProposals").ReplaceOneAsync(new BsonDocument("_id", proposal["_id"]), proposal);
                await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", proposal["auditId"]), Builders<BsonDocument>.Update.Set("record", proposal));
                await Rows(data, "stockSourceAliases").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("physicalKey", "unapproved-pool")); break;
            case "requested-channel-parent": await Rows(data, "authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 104L), Builders<BsonDocument>.Update.Set("parentId", 999L)); break;
            case "decision-channel": await Rows(data, "stockSourceAliasDecisions").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("requestedLocation.channelId", 105L)); break;
            case "active-pool": await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", true)); break;
            case "unexplained-settings-head": await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("revision", 2L)); break;
            case "future-settings": await Rows(data, "stockSourceAliasProposals").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("poolRevision", 2L)); break;
            case "exhausted-counter": await Rows(data, "idCounters").UpdateOneAsync(new BsonDocument("_id", "stockSourceAliases"), Builders<BsonDocument>.Update.Set("value", long.MaxValue)); break;
            case "source-counter": await Rows(data, "idCounters").UpdateOneAsync(new BsonDocument("_id", "inventoryPools"), Builders<BsonDocument>.Update.Set("value", long.MaxValue)); break;
            case "malformed-record":
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "stockSourceAliasProposals" }, { "validator", new BsonDocument() } });
                await Rows(data, "stockSourceAliasProposals").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("poolRevision", 1.5)); break;
            case "duplicate-version":
                await Rows(data, "stockSourceAliases").Indexes.DropOneAsync("ux_alias_draft");
                var alias = await Rows(data, "stockSourceAliases").Find(new BsonDocument()).SingleAsync();
                alias["_id"] = 9999L; await Rows(data, "stockSourceAliases").InsertOneAsync(alias); break;
            case "duplicate-decision":
                await Rows(data, "stockSourceAliasDecisions").Indexes.DropOneAsync("ux_alias_draft");
                decision["_id"] = 9999L; await Rows(data, "stockSourceAliasDecisions").InsertOneAsync(decision); break;
            case "extra-unique": await Rows(data, "stockSourceAliasProposals").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument("poolId", 1), new CreateIndexOptions { Unique = true, Name = "unexpected_unique" })); break;
            case "ttl": await Rows(data, "stockSourceAliasProposals").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument("proposedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(10000), Name = "unexpected_ttl" })); break;
        }
        await RejectWithoutMutation(data);
        Task ReceiptChange(string field, BsonValue value) => Rows(data, "idempotencyRecords").UpdateOneAsync(
            new BsonDocument("operation", "stock.source.alias.approve"), Builders<BsonDocument>.Update.Set(field, value));
    }

    [Theory]
    [InlineData("tail")] [InlineData("middle")]
    public async Task Missing_applied_mapping_versions_cannot_be_hidden_by_a_valid_remaining_head(string missing)
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data);
        await AddAlias(data, actors, 1, false, 1); await AddAlias(data, actors, 2, true, 1);
        await Rows(data, "stockSourceAliases").DeleteOneAsync(new BsonDocument("aliasRevision", missing == "tail" ? 3L : 2L));
        await RejectWithoutMutation(data);
    }

    [Fact]
    public async Task Migration_checks_old_chronology_beyond_runtime_head_and_predecessor()
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data);
        // The original source and first mapping were approved at the fixture epoch.
        // Move all first mapping timestamps together to one second, keeping their retained receipt coherent.
        var first = await Rows(data, "stockSourceAliasProposals").Find(new BsonDocument()).SingleAsync();
        var firstDecision = await Rows(data, "stockSourceAliasDecisions").Find(new BsonDocument()).SingleAsync();
        var oneSecond = data.Clock.UtcNow.AddSeconds(1);
        first["proposedAt"] = oneSecond.UtcDateTime; firstDecision["decidedAt"] = oneSecond.UtcDateTime;
        await ReplaceHistoryRecord(data, first, true); await ReplaceHistoryRecord(data, firstDecision, false);
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(2); await AddAlias(data, actors, 1, false, 1);
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1); await AddAlias(data, actors, 2, true, 1);
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1); await AddAlias(data, actors, 3, false, 1);
        var older = await Rows(data, "stockSourceAliasProposals").Find(new BsonDocument("baseAliasRevision", 1L)).SingleAsync();
        older["proposedAt"] = oneSecond.AddMilliseconds(-1).UtcDateTime;
        await ReplaceHistoryRecord(data, older, true);
        Assert.Equal(4L, (await Service(data).ReadAliasAsync(actors.Maker, actors.Pool, Requested))["mapping"]["alias"]["aliasRevision"].AsInt64);
        await RejectWithoutMutation(data);
    }
    private static async Task ReplaceHistoryRecord(StoreTestData data, BsonDocument row, bool proposal)
    {
        await Rows(data, proposal ? "stockSourceAliasProposals" : "stockSourceAliasDecisions").ReplaceOneAsync(new BsonDocument("_id", row["_id"]), row);
        var at = row[proposal ? "proposedAt" : "decidedAt"];
        await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", row["auditId"]),
            Builders<BsonDocument>.Update.Set("record", row).Set("createdAt", at));
        await Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument
            { { "operation", proposal ? "stock.source.alias.propose" : "stock.source.alias.approve" }, { "result." + (proposal ? "proposalId" : "decisionId"), row["_id"] } },
            Builders<BsonDocument>.Update.Set("createdAt", at));
    }
    private CollectionMigrations ObservedMigration(StoreTestData data, List<BsonDocument> writes)
    {
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "AliasMigration-" + Guid.NewGuid().ToString("N");
        var targets = StockSourceSetupSchemas.Collections.Concat(StockSourceRevisionSchemas.Collections).Concat(StockSourceAliasSchemas.Collections).ToHashSet(StringComparer.Ordinal);
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(e =>
        {
            if ((e.CommandName is "create" or "collMod" or "createIndexes" or "dropIndexes" && targets.Contains(e.Command.GetElement(0).Value.AsString)) ||
                e.CommandName == "update" && e.Command.GetValue("update", "") == "idCounters") lock (writes) writes.Add(e.Command.DeepClone().AsBsonDocument);
        });
        return new(new MongoClient(settings).GetDatabase(data.Database.DatabaseNamespace.DatabaseName), data.Clock);
    }
    private async Task RejectWithoutMutation(StoreTestData data)
    {
        var before = await BusinessRows(data);
        var historyBefore = await History(data);
        var counters = new BsonArray(await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var writes = new List<BsonDocument>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ObservedMigration(data, writes).ApplyAsync());
        Assert.Empty(writes); Assert.Equal(before, await BusinessRows(data));
        Assert.Equal(counters, new BsonArray(await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        var history = await History(data);
        Assert.Equal(historyBefore, new BsonArray(history.Take(16))); Assert.Equal("applying", history[16]["state"].AsString);
        Assert.False(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
    }
}
