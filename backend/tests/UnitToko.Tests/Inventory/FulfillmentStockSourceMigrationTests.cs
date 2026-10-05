using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Procurement;

namespace UnitToko.Tests.Inventory;

/// <summary>Real pending setup survives the exact empty92/93 suffix upgrade. This does not claim a positive
/// old-binary approved-source handoff or retrospective producer reconstruction.</summary>
[Collection("Mongo")]
public sealed class FulfillmentStockSourceMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Genuine_pending_setup_and_all_retained_bytes_survive91_to94_without_creating_authority_and_original_key_recovers()
    {
        await using var data = await FulfillmentStockSourceTestData.CreateAsync(fixture, approve: false);
        await RestoreEmptyV91Async(data.Data.Database);
        await data.Rows("stockSourceProposals").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("proposedBy", 1), new CreateIndexOptions { Name = "operator_retained_m92_proposal_lookup", Unique = false }));
        var predecessor = await HistoryAsync(data.Data.Database);
        var before = await SnapshotAsync(data.Data.Database);
        Assert.Equal(91, predecessor.Count);
        Assert.Equal(1L, await data.Rows("stockSourceProposals").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows("stockSourceDecisions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows("inventoryPools").CountDocumentsAsync(new BsonDocument()));

        var migration = new CollectionMigrations(data.Data.Database, data.Data.Clock);
        Assert.False(await migration.IsCurrentAsync());
        await migration.ApplyAsync(); Assert.True(await migration.IsCurrentAsync());
        var applied = await HistoryAsync(data.Data.Database);
        Assert.Equal(94, applied.Count);
        Assert.Equal(predecessor, new BsonArray(applied.Take(91)));
        Assert.Equal(92L, applied[91]["_id"].ToInt64()); Assert.Equal("applied", applied[91]["state"].AsString);
        Assert.Equal(93L, applied[92]["_id"].ToInt64()); Assert.Equal("applied", applied[92]["state"].AsString);
        Assert.Equal(94L, applied[93]["_id"].ToInt64()); Assert.Equal("applied", applied[93]["state"].AsString);
        var after = await SnapshotAsync(data.Data.Database);
        Assert.Equal(before.ElementCount + FulfillmentStockSourceSchemas.Collections.Length + ProcurementStockSourceSchemas.Collections.Length + PurchaseOrderInboundBindingSchemas.Collections.Length, after.ElementCount);
        foreach (var name in before.Names) Assert.Equal(before[name], after[name]);
        await FulfillmentStockSourceStorage.RequireAsync(data.Data.Database, default);
        await RequireEmptyOwnersAsync(data.Data.Database);
        await RequireEmptyProcurementOwnersAsync(data.Data.Database);
        await PurchaseOrderInboundBindingStorage.RequireAsync(data.Data.Database, default);
        foreach (var name in PurchaseOrderInboundBindingSchemas.Collections) Assert.Empty(after[name]["rows"].AsBsonArray);
        await migration.ApplyAsync();
        Assert.Equal(applied, await HistoryAsync(data.Data.Database));
        Assert.Equal(after, await SnapshotAsync(data.Data.Database));

        // Receipt-only recovery uses the server's actual saved request. No reconstructed approval
        // or fake source head is provided to the migration or this command.
        var proposal = await data.Rows("stockSourceProposals").Find(new BsonDocument("_id", data.Proposal["proposalId"])).SingleAsync();
        var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument {
            { "actorId", data.Maker.UserId }, { "operation", "stock.source.propose" }, { "key", "m92-source-propose" } }).SingleAsync();
        var restarted = new StockSourceSetupService(data.Data.Database, data.Unit, new(data.Data.Database), data.Data.Clock);
        Assert.Equal(data.Proposal, await restarted.RecoverAsync(data.Maker, FulfillmentStockSourceTestData.Location,
            "stock.source.propose", "m92-source-propose", "m92-original-key-recovery"));
        Assert.Equal(proposal, await data.Rows("stockSourceProposals").Find(new BsonDocument("_id", proposal["_id"])).SingleAsync());
        Assert.Equal(receipt, await data.Rows("idempotencyRecords").Find(new BsonDocument("_id", receipt["_id"])).SingleAsync());
        Assert.Equal(1L, await data.Rows("stockSourceProposals").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows("stockSourceDecisions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows("inventoryPools").CountDocumentsAsync(new BsonDocument()));
        await RequireEmptyOwnersAsync(data.Data.Database);
        await RequireEmptyProcurementOwnersAsync(data.Data.Database);
    }

    [Theory]
    [InlineData("spent-counter")]
    [InlineData("unsafe-index")]
    [InlineData("malformed-retained-proposal")]
    public async Task Isolated_allocator_constraint_or_actual_retained_corruption_refuses_before_new_owner_DDL(string fault)
    {
        await using var data = await FulfillmentStockSourceTestData.CreateAsync(fixture, approve: false);
        await RestoreEmptyV91Async(data.Data.Database);
        if (fault == "spent-counter")
        {
            // Spend the genuine allocator, rather than inventing a prospective counter or source row.
            Assert.Equal(1L, (await new IdAllocator(data.Data.Database).NextAsync(FulfillmentStockSourceRecords.Transitions)).Value);
        }
        if (fault == "unsafe-index")
        {
            var name = FulfillmentStockSourceRecords.Heads;
            await data.Data.Database.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument> {
                Validator = FulfillmentStockSourceSchemas.Validator(name), ValidationLevel = DocumentValidationLevel.Strict,
                ValidationAction = DocumentValidationAction.Error });
            await data.Rows(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("setupDecisionId", 1),
                new CreateIndexOptions { Name = "operator_unreviewed_m92_uniqueness", Unique = true }));
        }
        if (fault == "malformed-retained-proposal")
        {
            // Negative corruption of an actual command-generated retained row; no replacement body,
            // synthetic provenance, approval or receipt is inserted.
            Assert.Equal(1L, (await data.Rows("stockSourceProposals").UpdateOneAsync(
                new BsonDocument("_id", data.Proposal["proposalId"]), Builders<BsonDocument>.Update.Set("kind", "invalid.retained.source"),
                new UpdateOptions { BypassDocumentValidation = true })).ModifiedCount);
            Assert.True(await data.Rows("stockSourceProposals").Find(new BsonDocument("$nor",
                new BsonArray { StockSourceSetupSchemas.Validator("stockSourceProposals") })).AnyAsync());
        }
        await RequirePreDdlRefusalAsync(data.Data.Database, data.Data.Clock);
        Assert.Null(await AccountContactStorage.DefinitionAsync(data.Data.Database, FulfillmentStockSourceRecords.Transitions, default));
        if (fault == "unsafe-index") Assert.NotNull(await AccountContactStorage.DefinitionAsync(data.Data.Database, FulfillmentStockSourceRecords.Heads, default));
        else Assert.Null(await AccountContactStorage.DefinitionAsync(data.Data.Database, FulfillmentStockSourceRecords.Heads, default));
    }

    private async Task RequirePreDdlRefusalAsync(IMongoDatabase database, MutableAuthorizationClock clock)
    {
        var predecessor = await HistoryAsync(database); Assert.Equal(91, predecessor.Count);
        var before = await SnapshotAsync(database);
        var ddl = new ConcurrentQueue<string>();
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "FulfillmentMigration-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command => {
            if (command.CommandName is "create" or "collMod" or "createIndexes" or "dropIndexes" or "drop") ddl.Enqueue(command.CommandName);
        });
        var observed = new MongoClient(settings).GetDatabase(database.DatabaseNamespace.DatabaseName);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(observed, clock).ApplyAsync());
        Assert.Equal("Fulfillment source migration requires exact safe retained history and empty new owners", error.Message);
        Assert.Empty(ddl);
        Assert.Equal(before, await SnapshotAsync(database));
        var failed = await HistoryAsync(database); Assert.Equal(92, failed.Count);
        Assert.Equal(predecessor, new BsonArray(failed.Take(91)));
        Assert.Equal(92L, failed[91]["_id"].ToInt64()); Assert.Equal("applying", failed[91]["state"].AsString);
        Assert.False(await new CollectionMigrations(database, clock).IsCurrentAsync());
    }

    private static async Task RestoreEmptyV91Async(IMongoDatabase database)
    {
        await RequireEmptyOwnersAsync(database);
        await ProcurementStockSourceMigrationTests.RestoreEmptyV92Async(database);
        var history = await RequireApplied92Async(database);
        // The only destructive rehearsal removes an exact EMPTY future-owner suffix in this
        // fixture's random database. Actual approved producer history is never removed.
        Assert.Equal(1L, (await database.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(history[91].AsBsonDocument)).DeletedCount);
        foreach (var name in FulfillmentStockSourceSchemas.Collections) await database.DropCollectionAsync(name);
        Assert.Equal(new BsonArray(history.Take(91)), await HistoryAsync(database));
    }

    private static async Task<BsonArray> RequireApplied92Async(IMongoDatabase database)
    {
        Assert.Matches(@"\Aunittoko_test_[0-9a-f]{32}_[0-9a-f]{8}\z", database.DatabaseNamespace.DatabaseName);
        Assert.Equal(94, CollectionMigrations.CurrentVersion);
        var history = await HistoryAsync(database);
        Assert.Equal(92, history.Count);
        Assert.True(MigrationHistory.IsValid(history.Select(row => row.AsBsonDocument).ToArray(), 92));
        Assert.All(history, row => Assert.Equal("applied", row["state"].AsString));
        return history;
    }

    private static async Task RequireEmptyOwnersAsync(IMongoDatabase database)
    {
        foreach (var name in FulfillmentStockSourceSchemas.Collections)
        {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, default); Assert.NotNull(definition);
            Assert.Equal(FulfillmentStockSourceSchemas.Validator(name), definition["options"]["validator"].AsBsonDocument);
            Assert.True(AccountContactStorage.SafeDefinition(definition, FulfillmentStockSourceSchemas.Validator(name)));
            Assert.Equal("strict", definition["options"]["validationLevel"].AsString);
            Assert.Equal("error", definition["options"]["validationAction"].AsString);
            Assert.Equal(0L, await database.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
            Assert.False(await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync();
            Assert.True(OrderStockSourceStorage.SafeIndexes(indexes, FulfillmentStockSourceSchemas.Keys(name)));
            Assert.All(FulfillmentStockSourceSchemas.Keys(name), key => Assert.Contains(indexes, row => AccountContactStorage.Matches(row, key)));
            Assert.Equal(1 + FulfillmentStockSourceSchemas.Keys(name).Length, indexes.Count);
        }
    }

    private static async Task RequireEmptyProcurementOwnersAsync(IMongoDatabase database)
    {
        await ProcurementStockSourceStorage.RequireAsync(database, default);
        foreach (var name in ProcurementStockSourceSchemas.Collections) {
            Assert.Equal(0L, await database.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
            Assert.False(await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
    }

    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(
        await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());

    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase database)
    {
        var result = new BsonDocument();
        var definitions = await (await database.ListCollectionsAsync()).ToListAsync();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection")
            .OrderBy(row => row["name"].AsString, StringComparer.Ordinal))
        {
            var name = definition["name"].AsString; var rows = database.GetCollection<BsonDocument>(name);
            var indexes = (await (await rows.Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal);
            var state = new BsonDocument { { "definition", definition.DeepClone() }, { "indexes", new BsonArray(indexes) } };
            // Lease history is verified separately. Every retained collection UUID/options/index
            // and all other documents, including allocator counters, are compared byte-exact.
            if (name != "schemaMigrations") state.Add("rows", new BsonArray(await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            result.Add(name, state);
        }
        return result;
    }
}
