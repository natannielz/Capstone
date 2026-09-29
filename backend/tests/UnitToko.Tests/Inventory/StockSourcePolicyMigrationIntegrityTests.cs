using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourcePolicyMigrationTests
{
    [Theory]
    [InlineData("proposal")] [InlineData("decision")] [InlineData("policy")] [InlineData("audit")]
    [InlineData("receipt")] [InlineData("actor")] [InlineData("hash")] [InlineData("source-snapshot")]
    [InlineData("participants")] [InlineData("participant-copy")] [InlineData("foreign-audit")] [InlineData("foreign-receipt")]
    [InlineData("unknown-operation")] [InlineData("duplicate-policy")] [InlineData("duplicate-decision")]
    [InlineData("numeric-type")] [InlineData("unexpected-unique")] [InlineData("ttl")] [InlineData("lookup-hidden")]
    [InlineData("lookup-name")] [InlineData("policy-counter")] [InlineData("original-counter")] [InlineData("source-receipt")]
    public async Task Incompatible_policy_graph_or_storage_fails_before_target_ddl_and_counter_writes(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data);
        var proposal = await Rows(data, StockSourcePolicyBson.Proposals).Find(new BsonDocument()).SingleAsync();
        var decision = await Rows(data, StockSourcePolicyBson.Decisions).Find(new BsonDocument()).SingleAsync();
        var receiptFilter = new BsonDocument("operation", "stock.source.policy.approve");
        switch (fault)
        {
            case "proposal": await Rows(data, StockSourcePolicyBson.Proposals).DeleteManyAsync(new BsonDocument()); break;
            case "decision": await Rows(data, StockSourcePolicyBson.Decisions).DeleteManyAsync(new BsonDocument()); break;
            case "policy": await Rows(data, StockSourcePolicyBson.Policies).DeleteManyAsync(new BsonDocument()); break;
            case "audit": await Rows(data, "auditEvents").DeleteOneAsync(new BsonDocument("_id", proposal["auditId"])); break;
            case "receipt": await Rows(data, "idempotencyRecords").DeleteOneAsync(receiptFilter); break;
            case "actor": await Rows(data, "identities").DeleteOneAsync(new BsonDocument("_id", actors.Reviewer.UserId)); break;
            case "hash":
                proposal["configurationHash"] = new string('a', 64);
                await Rows(data, StockSourcePolicyBson.Proposals).ReplaceOneAsync(new BsonDocument("_id", proposal["_id"]), proposal); break;
            case "source-snapshot":
                proposal["configuration"]["source"]["input"]["label"] = "Forged historical setting"; await ReplaceProposal(data, proposal); break;
            case "participants": await Rows(data, "idempotencyRecords").UpdateOneAsync(receiptFilter, Builders<BsonDocument>.Update.Unset("personApproval")); break;
            case "participant-copy":
                await Rows(data, "idempotencyRecords").UpdateOneAsync(receiptFilter, Builders<BsonDocument>.Update.Set("personApproval.approval.proposedBy", 9999L)); break;
            case "foreign-audit":
                var audit = await Rows(data, "auditEvents").Find(new BsonDocument("_id", proposal["auditId"])).SingleAsync();
                audit["_id"] = 99999L; audit["kind"] = "foreign.audit"; audit["operation"] = "foreign.operation";
                await Rows(data, "auditEvents").InsertOneAsync(audit); break;
            case "foreign-receipt":
                var receipt = await Rows(data, "idempotencyRecords").Find(receiptFilter).SingleAsync();
                receipt.Remove("_id"); receipt["operation"] = "foreign.operation"; receipt["key"] = "copied";
                await Rows(data, "idempotencyRecords").InsertOneAsync(receipt); break;
            case "unknown-operation":
                await Rows(data, "idempotencyRecords").UpdateOneAsync(receiptFilter, Builders<BsonDocument>.Update.Set("operation", "stock.source.policy.unknown")); break;
            case "duplicate-policy": case "duplicate-decision":
                var collection = fault == "duplicate-policy" ? StockSourcePolicyBson.Policies : StockSourcePolicyBson.Decisions;
                await Rows(data, collection).Indexes.DropOneAsync("reviewed_policy_unique");
                var duplicate = await Rows(data, collection).Find(new BsonDocument()).SingleAsync(); duplicate["_id"] = 99999L;
                await Rows(data, collection).InsertOneAsync(duplicate); break;
            case "numeric-type":
                await Rows(data, StockSourcePolicyBson.Proposals).UpdateOneAsync(new BsonDocument("_id", proposal["_id"]),
                    Builders<BsonDocument>.Update.Set("poolId", new BsonInt32(1)), new UpdateOptions { BypassDocumentValidation = true }); break;
            case "unexpected-unique":
                await Rows(data, StockSourcePolicyBson.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("poolId", 1),
                    new CreateIndexOptions { Name = "unrequested_unique", Unique = true })); break;
            case "ttl":
                await Rows(data, StockSourcePolicyBson.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("proposedAt", 1),
                    new CreateIndexOptions { Name = "unsafe_retention", ExpireAfter = TimeSpan.FromDays(10000) })); break;
            case "lookup-hidden": case "lookup-name":
                var index = StockSourcePolicySchemas.LookupIndexes()[0]; await Rows(data, index.Collection).Indexes.DropOneAsync(index.Name);
                await Rows(data, index.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(fault == "lookup-name" ? new BsonDocument("wrong", 1) : index.Key,
                    new CreateIndexOptions { Name = index.Name, Hidden = fault == "lookup-hidden" })); break;
            case "policy-counter":
                await Rows(data, "idCounters").UpdateOneAsync(new BsonDocument("_id", StockSourcePolicyBson.Policies), Builders<BsonDocument>.Update.Set("value", long.MaxValue)); break;
            case "original-counter":
                await Rows(data, "idCounters").UpdateOneAsync(new BsonDocument("_id", "inventoryPools"), Builders<BsonDocument>.Update.Set("value", 1.5),
                    new UpdateOptions { BypassDocumentValidation = true }); break;
            case "source-receipt": await Rows(data, "idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "stock.source.approve")); break;
        }
        await Reject(data);
    }
    private static async Task ReplaceProposal(StoreTestData data, BsonDocument proposal)
    {
        proposal["configurationHash"] = StockSourceSuspensionBson.Hash(proposal["configuration"].AsBsonDocument);
        await Rows(data, StockSourcePolicyBson.Proposals).ReplaceOneAsync(new BsonDocument("_id", proposal["_id"]), proposal);
        await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", proposal["auditId"]),
            Builders<BsonDocument>.Update.Set("record", proposal.DeepClone()).Set("createdAt", proposal["proposedAt"]));
        var body = new { poolId = proposal["poolId"].AsInt64, expected = StockSourcePolicyBson.Expected(proposal),
            input = StockSourcePolicyBson.ReadInput(proposal["input"].AsBsonDocument, StockSourcePolicyBson.Source(proposal)) };
        await Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument { { "operation", "stock.source.policy.propose" }, { "result.proposalId", proposal["_id"] } },
            Builders<BsonDocument>.Update.Set("bodyHash", AuthorizationStore.CommandHash(body, proposal["reason"].AsString))
                .Set("result", StockSourcePolicyBson.ProposalResult(proposal)));
    }
    private CollectionMigrations Observed(StoreTestData data, List<BsonDocument> writes)
    {
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "PolicyMigration-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(e =>
        {
            if (e.CommandName is "create" or "collMod" or "createIndexes" or "dropIndexes" || e.CommandName == "update" && e.Command.GetValue("update", "") == "idCounters")
                lock (writes) writes.Add(e.Command.DeepClone().AsBsonDocument);
        });
        return new(new MongoClient(settings).GetDatabase(data.Database.DatabaseNamespace.DatabaseName), data.Clock);
    }
    private async Task Reject(StoreTestData data)
    {
        var business = await Business(data); var history = await History(data);
        var counters = new BsonArray(await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var writes = new List<BsonDocument>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Observed(data, writes).ApplyAsync());
        Assert.Empty(writes); Assert.Equal(business, await Business(data));
        Assert.Equal(counters, new BsonArray(await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        var after = await History(data); Assert.Equal(history, new BsonArray(after.Take(18))); Assert.Equal("applying", after[18]["state"].AsString);
        Assert.False(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
    }
}