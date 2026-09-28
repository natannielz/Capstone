using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceRevisionMigrationTests
{
    [Theory]
    [InlineData("base-snapshot")] [InlineData("missing-decision")] [InlineData("missing-applied")]
    [InlineData("orphan-applied")] [InlineData("unexplained-head")] [InlineData("active-pool")]
    [InlineData("receipt-hash")] [InlineData("receipt-result")] [InlineData("receipt-participants")]
    [InlineData("missing-participants")] [InlineData("missing-actor")] [InlineData("original-receipt")]
    [InlineData("audit-record")] [InlineData("orphan-audit")] [InlineData("orphan-receipt")]
    [InlineData("unknown-operation")] [InlineData("registration-key")] [InlineData("exhausted-counter")]
    [InlineData("unsafe-unique")] [InlineData("ttl")] [InlineData("malformed-record")]
    public async Task Broken_history_or_storage_stops_before_target_ddl_or_counter_writes(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourceRevisionMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        var proposal = await Rows(data, "stockSourceRevisionProposals").Find(new BsonDocument()).SingleAsync();
        var decision = await Rows(data, "stockSourceRevisionDecisions").Find(new BsonDocument()).SingleAsync();
        switch (fault)
        {
            case "base-snapshot":
                proposal["baseInput"]["label"] = "Unapproved base";
                await Rows(data, "stockSourceRevisionProposals").ReplaceOneAsync(new BsonDocument("_id", proposal["_id"]), proposal);
                await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", proposal["auditId"]),
                    Builders<BsonDocument>.Update.Set("record", proposal)); break;
            case "missing-decision": await Rows(data, "stockSourceRevisionDecisions").DeleteManyAsync(new BsonDocument()); break;
            case "missing-applied": await Rows(data, "stockSourceRevisions").DeleteManyAsync(new BsonDocument()); break;
            case "orphan-applied":
                var duplicate = await Rows(data, "stockSourceRevisions").Find(new BsonDocument()).SingleAsync();
                duplicate["_id"] = 9000L; duplicate["poolRevision"] = 3L;
                await Rows(data, "stockSourceRevisions").InsertOneAsync(duplicate); break;
            case "unexplained-head": await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument(),
                Builders<BsonDocument>.Update.Set("revision", 3L)); break;
            case "active-pool": await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument(),
                Builders<BsonDocument>.Update.Set("active", true)); break;
            case "receipt-hash": await Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument("operation", "stock.source.revision.approve"),
                Builders<BsonDocument>.Update.Set("bodyHash", new string('0', 64))); break;
            case "receipt-result": await Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument("operation", "stock.source.revision.approve"),
                Builders<BsonDocument>.Update.Set("result.poolRevision", 999L)); break;
            case "receipt-participants": await Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument("operation", "stock.source.revision.approve"),
                Builders<BsonDocument>.Update.Set("personApproval.approval.proposedBy", 999L)); break;
            case "missing-participants": await Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument("operation", "stock.source.revision.approve"),
                Builders<BsonDocument>.Update.Unset("personApproval")); break;
            case "missing-actor": await Rows(data, "identities").DeleteOneAsync(new BsonDocument("_id", 2000L)); break;
            case "original-receipt": await Rows(data, "idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "stock.source.approve")); break;
            case "audit-record": await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", decision["auditId"]),
                Builders<BsonDocument>.Update.Set("record.reason", "Different record")); break;
            case "orphan-audit":
                var audit = await Rows(data, "auditEvents").Find(new BsonDocument("_id", decision["auditId"])).SingleAsync();
                audit["_id"] = 9000L; await Rows(data, "auditEvents").InsertOneAsync(audit); break;
            case "orphan-receipt": case "unknown-operation":
                var receipt = await Rows(data, "idempotencyRecords").Find(new BsonDocument("operation", "stock.source.revision.propose")).SingleAsync();
                receipt["_id"] = ObjectId.GenerateNewId(); receipt["key"] = "unexplained-receipt";
                if (fault == "unknown-operation") receipt["operation"] = "stock.source.unsupported";
                else receipt["result"]["proposalId"] = 9000L;
                await Rows(data, "idempotencyRecords").InsertOneAsync(receipt); break;
            case "registration-key": await Rows(data, "inventoryPoolRegistrations").UpdateOneAsync(new BsonDocument(),
                Builders<BsonDocument>.Update.Set("physicalKey", "unapproved-key")); break;
            case "exhausted-counter": await Rows(data, "idCounters").UpdateOneAsync(new BsonDocument("_id", "stockSourceRevisions"),
                Builders<BsonDocument>.Update.Set("value", long.MaxValue)); break;
            case "unsafe-unique": await Rows(data, "stockSourceRevisionProposals").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument("poolId", 1), new CreateIndexOptions { Unique = true, Name = "unexpected_unique" })); break;
            case "ttl": await Rows(data, "stockSourceRevisionProposals").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument("proposedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(10000), Name = "unexpected_ttl" })); break;
            case "malformed-record":
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "stockSourceRevisionProposals" }, { "validator", new BsonDocument() } });
                await Rows(data, "stockSourceRevisionProposals").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("basePoolRevision", 1.5)); break;
        }
        await RejectWithoutMutation(data);
    }

    [Fact]
    public async Task Preflight_checks_older_history_beyond_the_runtime_current_and_predecessor_window()
    {
        await using var data = await StoreTestData.CreateForStockSourceRevisionMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data);
        var service = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        for (long version = 2; version <= 3; version++)
        {
            var current = await service.ReadCurrentSourceAsync(actors.Maker, new(new(100), new(101), new(102), new(103)), actors.Pool);
            var input = StockSourceSetupBson.ReadInput(current["input"].AsBsonDocument) with { Label = "Version " + (version + 1) };
            var proposed = await service.ProposeRevisionAsync(actors.Maker, actors.Pool, version, input, new("propose-" + version, "Synthetic later version", "migration"));
            await service.DecideRevisionAsync(actors.Reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
                "synthetic-review", new("approve-" + version, "Synthetic approval", "migration"));
        }
        var old = await Rows(data, "stockSourceRevisionProposals").Find(new BsonDocument("basePoolRevision", 1L)).SingleAsync();
        old["input"]["label"] = "Changed oldest approved input";
        await Rows(data, "stockSourceRevisionProposals").ReplaceOneAsync(new BsonDocument("_id", old["_id"]), old);
        await Rows(data, "stockSourceRevisions").UpdateOneAsync(new BsonDocument("poolRevision", 2L), Builders<BsonDocument>.Update.Set("input", old["input"]));
        await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", old["auditId"]), Builders<BsonDocument>.Update.Set("record", old));
        var poolId = actors.Pool; long expectedPoolRevision = 1; var changedInput = StockSourceSetupBson.ReadInput(old["input"].AsBsonDocument);
        await Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument
            { { "operation", "stock.source.revision.propose" }, { "result.proposalId", old["_id"] } },
            Builders<BsonDocument>.Update.Set("bodyHash", AuthorizationStore.CommandHash(new { poolId, expectedPoolRevision, input = changedInput }, old["reason"].AsString)));
        // Runtime's current and immediate predecessor remain coherent; the deployment audit must go further.
        Assert.Equal(4, (await service.ReadCurrentSourceAsync(actors.Maker, new(new(100), new(101), new(102), new(103)), actors.Pool))["pool"]["revision"].AsInt64);
        await RejectWithoutMutation(data);
    }

    [Fact]
    public async Task Historical_participants_survive_current_person_binding_loss_without_inventing_a_historical_person_proof()
    {
        await using var data = await StoreTestData.CreateForStockSourceRevisionMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        await Rows(data, "personAccountBindings").DeleteManyAsync(new BsonDocument());
        await Rows(data, "identities").UpdateOneAsync(new BsonDocument("_id", 1000L), Builders<BsonDocument>.Update.Set("status", "suspended"));
        await Rows(data, "authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 102L), Builders<BsonDocument>.Update.Set("status", "suspended"));
        var before = await BusinessRows(data);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await BusinessRows(data));
        Assert.True(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
    }

    private CollectionMigrations ObservedMigration(StoreTestData data, List<BsonDocument> writes)
    {
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "RevisionMigration-" + Guid.NewGuid().ToString("N");
        var targets = StockSourceSetupSchemas.Collections.Concat(StockSourceRevisionSchemas.Collections).ToHashSet(StringComparer.Ordinal);
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(e =>
        {
            if ((e.CommandName is "create" or "collMod" or "createIndexes" or "dropIndexes" &&
                    targets.Contains(e.Command.GetElement(0).Value.AsString)) ||
                e.CommandName == "update" && e.Command.GetValue("update", "") == "idCounters")
                lock (writes) writes.Add(e.Command.DeepClone().AsBsonDocument);
        });
        return new(new MongoClient(settings).GetDatabase(data.Database.DatabaseNamespace.DatabaseName), data.Clock);
    }
    private async Task RejectWithoutMutation(StoreTestData data)
    {
        var before = await BusinessRows(data);
        var counters = new BsonArray(await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var writes = new List<BsonDocument>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ObservedMigration(data, writes).ApplyAsync());
        Assert.Empty(writes);
        Assert.Equal(before, await BusinessRows(data));
        Assert.Equal(counters, new BsonArray(await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        var history = await History(data);
        Assert.Equal("applying", history[15]["state"].AsString);
        Assert.False(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
    }
}
