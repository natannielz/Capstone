using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Procurement;

/// <summary>Registered M93 rehearsal through the real CollectionMigrations owner.
/// Only a genuine pending setup is retained; no approved source, pool or retrospective head is seeded.</summary>
[Collection("Mongo")]
public sealed class ProcurementStockSourceMigrationTests(MongoFixture fixture)
{
    private static readonly StockLocation Location = new(new(100), new(101), new(102), new(103));
    private const string ProposalKey = "m93-pending-source-propose";

    [Fact]
    public async Task Genuine_pending_setup_and_all_retained_bytes_survive92_to94_and_original_key_recovers()
    {
        var pending = await PendingAsync(); await using var data = pending.Data;
        await RestoreEmptyV92Async(data.Database);
        await data.Database.GetCollection<BsonDocument>("stockSourceProposals").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument("proposedBy", 1), new CreateIndexOptions {
                Name = "operator_retained_m93_proposal_lookup", Unique = false }));
        var predecessor = await HistoryAsync(data.Database); Assert.Equal(92, predecessor.Count);
        var before = await SnapshotAsync(data.Database);
        await RequirePendingOnlyAsync(data.Database);

        var migration = new CollectionMigrations(data.Database, data.Clock);
        Assert.False(await migration.IsCurrentAsync());
        await migration.ApplyAsync(); Assert.True(await migration.IsCurrentAsync());
        var applied = await HistoryAsync(data.Database); Assert.Equal(94, applied.Count);
        Assert.Equal(predecessor, new BsonArray(applied.Take(92)));
        Assert.Equal(93L, applied[92]["_id"].ToInt64()); Assert.Equal("applied", applied[92]["state"].AsString);
        Assert.Equal(94L, applied[93]["_id"].ToInt64()); Assert.Equal("applied", applied[93]["state"].AsString);
        var after = await SnapshotAsync(data.Database);
        Assert.Equal(before.ElementCount + ProcurementStockSourceSchemas.Collections.Length + PurchaseOrderInboundBindingSchemas.Collections.Length, after.ElementCount);
        foreach (var name in before.Names) Assert.Equal(before[name], after[name]);
        await ProcurementStockSourceStorage.RequireAsync(data.Database, default);
        await RequireEmptyOwnersAsync(data.Database);
        await PurchaseOrderInboundBindingStorage.RequireAsync(data.Database, default);
        foreach (var name in PurchaseOrderInboundBindingSchemas.Collections) Assert.Empty(after[name]["rows"].AsBsonArray);
        await migration.ApplyAsync();
        Assert.Equal(applied, await HistoryAsync(data.Database));
        Assert.Equal(after, await SnapshotAsync(data.Database));

        var proposals = data.Database.GetCollection<BsonDocument>("stockSourceProposals");
        var proposal = await proposals.Find(new BsonDocument("_id", pending.Proposal["proposalId"])).SingleAsync();
        var receipts = data.Database.GetCollection<BsonDocument>("idempotencyRecords");
        var receipt = await receipts.Find(new BsonDocument { { "actorId", pending.Maker.UserId },
            { "operation", "stock.source.propose" }, { "key", ProposalKey } }).SingleAsync();
        var restarted = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        Assert.Equal(pending.Proposal, await restarted.RecoverAsync(pending.Maker, Location,
            "stock.source.propose", ProposalKey, "m93-original-key-recovery"));
        Assert.Equal(proposal, await proposals.Find(new BsonDocument("_id", proposal["_id"])).SingleAsync());
        Assert.Equal(receipt, await receipts.Find(new BsonDocument("_id", receipt["_id"])).SingleAsync());
        await RequirePendingOnlyAsync(data.Database);
        await RequireEmptyOwnersAsync(data.Database);
    }

    [Theory]
    [InlineData("spent-counter")]
    [InlineData("unsafe-index")]
    [InlineData("populated-prospective-owner")]
    public async Task Prospective_allocator_constraint_or_explicit_owner_corruption_refuses_before_target_DDL(string fault)
    {
        var pending = await PendingAsync(); await using var data = pending.Data;
        await RestoreEmptyV92Async(data.Database);
        if (fault == "spent-counter") {
            // Spend the actual allocator outside business transactions; do not invent an allocator row.
            Assert.Equal(1L, (await new IdAllocator(data.Database).NextAsync(ProcurementStockSourceRecords.Transitions)).Value);
        } else {
            var name = ProcurementStockSourceRecords.Heads;
            await data.Database.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument> {
                Validator = ProcurementStockSourceSchemas.Validator(name), ValidationLevel = DocumentValidationLevel.Strict,
                ValidationAction = DocumentValidationAction.Error });
            var rows = data.Database.GetCollection<BsonDocument>(name);
            if (fault == "unsafe-index") {
                await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("setupDecisionId", 1),
                    new CreateIndexOptions { Name = "operator_unreviewed_m93_uniqueness", Unique = true }));
            } else {
                Assert.Equal("populated-prospective-owner", fault);
                // Explicit negative corruption ONLY: this schema-valid descriptive head has no real
                // approval/pool/transition. It is never a positive fixture or migration authority.
                var corrupt = new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 },
                    { "kind", "procurement.heldsource.head" }, { "location", StockSourceSetupBson.Location(Location) },
                    { "setupDecisionId", 1L }, { "revision", 1L }, { "lastTransitionId", 1L },
                    { "lastHash", new string('0', 64) }, { "updatedAt", data.Clock.UtcNow.UtcDateTime }, { "fence", 0L } };
                await rows.InsertOneAsync(corrupt);
                Assert.Equal(corrupt, await rows.Find(ProcurementStockSourceSchemas.Validator(name)).SingleAsync());
            }
        }
        await RequirePendingOnlyAsync(data.Database);
        await RequirePreDdlRefusalAsync(data.Database, data.Clock);
        Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, ProcurementStockSourceRecords.Transitions, default));
        if (fault == "spent-counter") Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, ProcurementStockSourceRecords.Heads, default));
        else Assert.NotNull(await AccountContactStorage.DefinitionAsync(data.Database, ProcurementStockSourceRecords.Heads, default));
    }

    private async Task<(StoreTestData Data, ActorContext Maker, BsonDocument Proposal)> PendingAsync()
    {
        // No direct migration-step shortcut: the future root registry must actually create M93.
        Assert.Equal(94, CollectionMigrations.CurrentVersion);
        var data = await StoreTestData.CreateAsync(fixture);
        try {
            Assert.True(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
            var maker = await data.AddActorAsync(930100, Location.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId);
            var service = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
            var proposal = await service.ProposeAsync(maker, new(Location, "m93-pending-held-source", "Synthetic pending held source",
                StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60000, 0, null, null,
                "Synthetic discrepancy owner", "Synthetic source handoff"),
                new(ProposalKey, "Synthetic local pending source proof", "m93-procurement-migration-tests"));
            await RequirePendingOnlyAsync(data.Database);
            await RequireEmptyOwnersAsync(data.Database);
            return (data, maker, proposal);
        } catch { await data.DisposeAsync(); throw; }
    }

    private async Task RequirePreDdlRefusalAsync(IMongoDatabase database, MutableAuthorizationClock clock)
    {
        var predecessor = await HistoryAsync(database); Assert.Equal(92, predecessor.Count);
        var before = await SnapshotAsync(database);
        var ddl = new ConcurrentQueue<string>();
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "ProcurementMigration-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command => {
            if (command.CommandName is "create" or "collMod" or "createIndexes" or "dropIndexes" or "drop") ddl.Enqueue(command.CommandName);
        });
        var observed = new MongoClient(settings).GetDatabase(database.DatabaseNamespace.DatabaseName);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(observed, clock).ApplyAsync());
        Assert.Equal("Procurement held-source migration requires exact safe retained history and empty new owners", error.Message);
        Assert.Empty(ddl); Assert.Equal(before, await SnapshotAsync(database));
        var failed = await HistoryAsync(database); Assert.Equal(93, failed.Count);
        Assert.Equal(predecessor, new BsonArray(failed.Take(92)));
        Assert.Equal(93L, failed[92]["_id"].ToInt64()); Assert.Equal("applying", failed[92]["state"].AsString);
        Assert.False(await new CollectionMigrations(database, clock).IsCurrentAsync());
    }

    internal static async Task RestoreEmptyV92Async(IMongoDatabase database)
    {
        Assert.Matches(@"\Aunittoko_test_[0-9a-f]{32}_[0-9a-f]{8}\z", database.DatabaseNamespace.DatabaseName);
        Assert.Equal(94, CollectionMigrations.CurrentVersion);
        await PurchaseOrderInboundBindingMigrationTests.RestoreEmptyV93Async(database);
        var history = await HistoryAsync(database); Assert.Equal(93, history.Count);
        Assert.True(MigrationHistory.IsValid(history.Select(row => row.AsBsonDocument).ToArray(), 93));
        Assert.All(history, row => Assert.Equal("applied", row["state"].AsString));
        Assert.Equal(93L, history[92]["_id"].ToInt64());
        await RequireEmptyOwnersAsync(database);
        var before = await SnapshotAsync(database);
        var counters = database.GetCollection<BsonDocument>("idCounters");
        var emptyCounters = new List<BsonDocument>();
        // Prove every target before removing any suffix state. Only exact unused counters qualify.
        foreach (var name in ProcurementStockSourceSchemas.Collections) {
            var row = await counters.Find(new BsonDocument("_id", name)).SingleOrDefaultAsync();
            if (row is null) continue;
            Assert.Equal(new BsonDocument { { "_id", name }, { "value", 0L } }, row); emptyCounters.Add(row);
        }
        Assert.Equal(1L, (await database.GetCollection<BsonDocument>("schemaMigrations")
            .DeleteOneAsync(history[92].AsBsonDocument)).DeletedCount);
        foreach (var row in emptyCounters) Assert.Equal(1L, (await counters.DeleteOneAsync(row)).DeletedCount);
        foreach (var name in ProcurementStockSourceSchemas.Collections) { await database.DropCollectionAsync(name); before.Remove(name); }
        before["idCounters"]["rows"] = new BsonArray(before["idCounters"]["rows"].AsBsonArray
            .Where(row => !ProcurementStockSourceSchemas.Collections.Contains(row["_id"].AsString)));
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Equal(new BsonArray(history.Take(92)), await HistoryAsync(database));
    }

    private static async Task RequireEmptyOwnersAsync(IMongoDatabase database)
    {
        foreach (var name in ProcurementStockSourceSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, default); Assert.NotNull(definition);
            Assert.Equal(ProcurementStockSourceSchemas.Validator(name), definition["options"]["validator"].AsBsonDocument);
            Assert.True(AccountContactStorage.SafeDefinition(definition, ProcurementStockSourceSchemas.Validator(name)));
            Assert.Equal("strict", definition["options"]["validationLevel"].AsString);
            Assert.Equal("error", definition["options"]["validationAction"].AsString);
            Assert.Equal(0L, await database.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
            var counter = await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync();
            if (counter is not null) Assert.Equal(new BsonDocument { { "_id", name }, { "value", 0L } }, counter);
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync();
            var required = ProcurementStockSourceSchemas.Keys(name);
            Assert.True(OrderStockSourceStorage.SafeIndexes(indexes, required));
            Assert.All(required, key => Assert.Contains(indexes, row => AccountContactStorage.Matches(row, key)));
            Assert.Equal(1 + required.Length, indexes.Count);
        }
    }

    private static async Task RequirePendingOnlyAsync(IMongoDatabase database)
    {
        Assert.Equal(1L, await database.GetCollection<BsonDocument>("stockSourceProposals").CountDocumentsAsync(new BsonDocument()));
        foreach (var name in new[] { "stockSourceDecisions", "inventoryPools", "inventoryPoolRegistrations", "procurementStockReceipts" })
            Assert.Equal(0L, await database.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
    }

    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(
        await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());

    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase database)
    {
        var result = new BsonDocument();
        var definitions = await (await database.ListCollectionsAsync()).ToListAsync();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection")
            .OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; var rows = database.GetCollection<BsonDocument>(name);
            var indexes = (await (await rows.Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal);
            var state = new BsonDocument { { "definition", definition.DeepClone() }, { "indexes", new BsonArray(indexes) } };
            // Lease markers are compared separately; preserve every retained UUID, option, index,
            // counter and business document exactly through application, refusal and replay.
            if (name != "schemaMigrations") state.Add("rows", new BsonArray(await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            result.Add(name, state);
        }
        return result;
    }
}
