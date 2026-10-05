using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

/// <summary>UNEXECUTED M95 draft. Uses the real future registry and StoreTestData writer.
/// A pending proposal has no produced AfterSales setup head; no accepted source or legacy return
/// is seeded. Populated94 return acceptance requires a separately preserved real94 runtime.</summary>
[Collection("Mongo")]
public sealed class AfterSalesStockSourceMigrationTests(MongoFixture fixture)
{
    private static readonly StockLocation Location = new(new(100), new(101), new(102), new(103));
    private const string ProposalKey = "m95-migration-pending-original";
    private static string[] NewOwners => [.. AfterSalesStockSourceSchemas.Collections, ReturnedLotVerificationRecords.Collection];
    private static IMongoCollection<BsonDocument> Rows(IMongoDatabase database, string name) => database.GetCollection<BsonDocument>(name);

    [Fact]
    public async Task Exact94_pending_real_owner_and_safe_prospective_indexes_survive95_apply_and_replay_without_backfill()
    {
        var pending = await PendingAsync(); await using var data = pending.Data;
        await RestoreEmptyV94Async(data);
        await Rows(data.Database, "stockSourceProposals").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("proposedBy", 1),
            new CreateIndexOptions { Name = "operator_retained_m95_pending_lookup", Unique = false }));
        var name = AfterSalesStockSourceRecords.Heads;
        await CreateProspectiveAsync(data.Database, name);
        await Rows(data.Database, name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("location.shopId", 1),
            new CreateIndexOptions { Name = "operator_safe_m95_empty_head_lookup", Unique = false }));
        // Zero is explicitly unused. This prospective counter is preserved, never advanced by DDL.
        await Rows(data.Database, "idCounters").InsertOneAsync(new BsonDocument { { "_id", name }, { "value", 0L } });
        var predecessor = await HistoryAsync(data.Database); Assert.Equal(94, predecessor.Count);
        var before = await SnapshotAsync(data.Database);
        Assert.NotEmpty(before["stockSourceProposals"]["rows"].AsBsonArray);
        Assert.NotEmpty(before["idempotencyRecords"]["rows"].AsBsonArray);
        Assert.NotEmpty(before["auditEvents"]["rows"].AsBsonArray);
        await RequirePendingOnlyAsync(data.Database);
        var migrations = new CollectionMigrations(data.Database, data.Clock); Assert.False(await migrations.IsCurrentAsync());
        await migrations.ApplyAsync(); Assert.True(await migrations.IsCurrentAsync());
        var after = await SnapshotAsync(data.Database);
        foreach (var retained in before.Names) Assert.Equal(before[retained].AsBsonDocument.ToBson(), after[retained].AsBsonDocument.ToBson());
        Assert.Equal(before.ElementCount + NewOwners.Length - 1, after.ElementCount);
        foreach (var target in NewOwners) Assert.Empty(after[target]["rows"].AsBsonArray);
        await AfterSalesStockSourceStorage.RequireAsync(data.Database, default);
        var applied = await HistoryAsync(data.Database); Assert.Equal(95, applied.Count);
        Assert.Equal(HistoryBytes(predecessor), HistoryBytes(new BsonArray(applied.Take(94))));
        Assert.Equal(95L, applied[94]["_id"].ToInt64()); Assert.Equal("applied", applied[94]["state"].AsString);
        await migrations.ApplyAsync(); Assert.Equal(HistoryBytes(applied), HistoryBytes(await HistoryAsync(data.Database)));
        Assert.Equal(after.ToBson(), (await SnapshotAsync(data.Database)).ToBson());
        // Original-key recovery happens after the apply/replay snapshot assertion; it may audit the read.
        var proposal = await Rows(data.Database, "stockSourceProposals").Find(new BsonDocument("_id", pending.Proposal["proposalId"])).SingleAsync();
        var receipt = await Rows(data.Database, "idempotencyRecords").Find(new BsonDocument {
            { "actorId", pending.Maker.UserId }, { "operation", "stock.source.propose" }, { "key", ProposalKey } }).SingleAsync();
        var restarted = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        Assert.Equal(pending.Proposal, await restarted.RecoverAsync(pending.Maker, Location, "stock.source.propose", ProposalKey, "m95-pending-recovery"));
        Assert.Equal(proposal.ToBson(), (await Rows(data.Database, "stockSourceProposals").Find(new BsonDocument("_id", proposal["_id"])).SingleAsync()).ToBson());
        Assert.Equal(receipt.ToBson(), (await Rows(data.Database, "idempotencyRecords").Find(new BsonDocument("_id", receipt["_id"])).SingleAsync()).ToBson());
        await RequirePendingOnlyAsync(data.Database);
    }

    [Theory]
    [InlineData("spent-counter")]
    [InlineData("unsafe-index")]
    [InlineData("populated-prospective-owner")]
    public async Task Unsafe_prospective_counter_index_or_explicit_owner_corruption_refuses_before_DDL_and_preserves94(string fault)
    {
        var pending = await PendingAsync(); await using var data = pending.Data;
        await RestoreEmptyV94Async(data);
        if (fault == "spent-counter") {
            Assert.Equal(1L, (await new IdAllocator(data.Database).NextAsync(AfterSalesStockSourceRecords.Transitions)).Value);
        } else {
            var name = AfterSalesStockSourceRecords.Heads; await CreateProspectiveAsync(data.Database, name);
            if (fault == "unsafe-index") await Rows(data.Database, name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("setupDecisionId", 1),
                new CreateIndexOptions { Name = "operator_unsafe_m95_extra_uniqueness", Unique = true }));
            else {
                Assert.Equal("populated-prospective-owner", fault);
                // Explicit negative corruption ONLY. A schema-valid descriptive head does not prove
                // a pool, setup, transition or custody. Never use this row as a positive fixture.
                var corrupt = new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 }, { "kind", "aftersales.stocksource.head" },
                    { "location", StockSourceSetupBson.Location(Location) }, { "setupDecisionId", 1L }, { "revision", 1L },
                    { "lastTransitionId", 1L }, { "lastHash", new string('0', 64) }, { "updatedAt", data.Clock.UtcNow.UtcDateTime }, { "fence", 0L } };
                await Rows(data.Database, name).InsertOneAsync(corrupt);
                Assert.Equal(corrupt, await Rows(data.Database, name).Find(AfterSalesStockSourceSchemas.Validator(name)).SingleAsync());
            }
        }
        var predecessor = await HistoryAsync(data.Database); Assert.Equal(94, predecessor.Count);
        var before = await SnapshotAsync(data.Database); var ddl = new ConcurrentQueue<string>();
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "AfterSalesMigration-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command => {
            if (command.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName &&
                command.CommandName is "create" or "collMod" or "createIndexes" or "dropIndexes" or "drop") ddl.Enqueue(command.CommandName);
        });
        var observed = new MongoClient(settings).GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(observed, data.Clock).ApplyAsync());
        Assert.Equal("AfterSales source migration requires exact safe retained history and empty new owners", error.Message);
        Assert.Empty(ddl); Assert.Equal(before.ToBson(), (await SnapshotAsync(data.Database)).ToBson());
        var failed = await HistoryAsync(data.Database); Assert.Equal(95, failed.Count);
        Assert.Equal(HistoryBytes(predecessor), HistoryBytes(new BsonArray(failed.Take(94))));
        Assert.Equal(95L, failed[94]["_id"].ToInt64()); Assert.Equal("applying", failed[94]["state"].AsString);
        Assert.False(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
        Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, AfterSalesStockSourceRecords.Transitions, default));
        Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, ReturnedLotVerificationRecords.Collection, default));
        await RequirePendingOnlyAsync(data.Database, requireUnusedProspectiveOwners: false);
    }

    [Theory]
    [InlineData("genuine-setup")]
    [InlineData("spent-only")]
    public async Task Rehearsal_helper_refuses_a_produced95_setup_or_spent95_allocator_without_erasing_any_history(string state)
    {
        var pending = await PendingAsync(); await using var data = pending.Data;
        if (state == "genuine-setup") {
            var checker = await data.AddActorAsync(950720, Location.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, checker.UserId);
            var owner = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
            var approved = await owner.DecideAsync(checker, pending.Proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
                "Independent synthetic review", new("m95-produced-setup-guard", "Synthetic local source review", "m95-migration-guard"));
            Assert.Equal(1L, await Rows(data.Database, AfterSalesStockSourceRecords.Heads).CountDocumentsAsync(new BsonDocument("_id", approved["poolId"])));
            Assert.Equal(1L, await Rows(data.Database, AfterSalesStockSourceRecords.Transitions).CountDocumentsAsync(new BsonDocument("sourceKind", "setup")));
        } else {
            Assert.Equal("spent-only", state);
            Assert.Equal(1L, (await new IdAllocator(data.Database).NextAsync(AfterSalesStockSourceRecords.Transitions)).Value);
        }
        var before = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database);
        Assert.NotNull(await Record.ExceptionAsync(() => RestoreEmptyV94Async(data)));
        Assert.Equal(before.ToBson(), (await SnapshotAsync(data.Database)).ToBson());
        Assert.Equal(HistoryBytes(history), HistoryBytes(await HistoryAsync(data.Database)));
        Assert.True(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
    }

    private async Task<(StoreTestData Data, ActorContext Maker, BsonDocument Proposal)> PendingAsync()
    {
        Assert.Equal(95, CollectionMigrations.CurrentVersion);
        var data = await StoreTestData.CreateAsync(fixture);
        try {
            Assert.True(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
            var maker = await data.AddActorAsync(950710, Location.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId);
            var owner = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
            var proposed = await owner.ProposeAsync(maker, new(Location, "m95-pending-retained-source", "Synthetic pending source before migration rehearsal",
                StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60000, 0, null, null, "Synthetic discrepancy owner", "Synthetic handoff"),
                new(ProposalKey, "Synthetic local pending source proof", "m95-migration-tests"));
            await RequirePendingOnlyAsync(data.Database); return (data, maker, proposed);
        } catch { await data.DisposeAsync(); throw; }
    }

    private async Task RestoreEmptyV94Async(StoreTestData data)
    {
        var database = data.Database;
        Assert.Same(fixture.Client, database.Client);
        Assert.Matches(@"\Aunittoko_test_[0-9a-f]{32}_[0-9a-f]{8}\z", database.DatabaseNamespace.DatabaseName);
        Assert.StartsWith(fixture.DatabaseName + "_", database.DatabaseNamespace.DatabaseName, StringComparison.Ordinal);
        Assert.Equal(95, CollectionMigrations.CurrentVersion);
        var history = await HistoryAsync(database); Assert.Equal(95, history.Count);
        Assert.True(MigrationHistory.IsValid(history.Select(row => row.AsBsonDocument).ToArray(), 95));
        Assert.All(history, row => Assert.Equal("applied", row["state"].AsString)); Assert.Equal(95L, history[94]["_id"].ToInt64());
        await AfterSalesStockSourceStorage.RequireAsync(database, default);
        var emptyCounters = new List<BsonDocument>();
        // Check every owner, definition, index, counter and operation FIRST. A later failure
        // cannot follow an earlier drop. Real setup-created heads always refuse this helper.
        foreach (var name in NewOwners) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, default); Assert.NotNull(definition);
            var validator = Validator(name); Assert.Equal(validator, definition["options"]["validator"].AsBsonDocument);
            Assert.True(AccountContactStorage.SafeDefinition(definition, validator));
            Assert.Equal("strict", definition["options"]["validationLevel"].AsString); Assert.Equal("error", definition["options"]["validationAction"].AsString);
            var indexes = await (await Rows(database, name).Indexes.ListAsync()).ToListAsync(); var required = Keys(name);
            Assert.True(OrderStockSourceStorage.SafeIndexes(indexes, required));
            Assert.All(required, key => Assert.Contains(indexes, row => AccountContactStorage.Matches(row, key)));
            Assert.Equal(1 + required.Length, indexes.Count); // Only exact new95 DDL, no operator-owned suffix state is erased.
            Assert.Equal(0L, await Rows(database, name).CountDocumentsAsync(new BsonDocument()));
            var counter = await Rows(database, "idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync();
            if (counter is not null) { Assert.Equal(new BsonDocument { { "_id", name }, { "value", 0L } }, counter); emptyCounters.Add(counter); }
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            Assert.False(await Rows(database, table).Find(new BsonDocument(field, ReturnedLotVerificationRecords.Operation)).AnyAsync());
        var before = await SnapshotAsync(database);
        foreach (var name in NewOwners) { await database.DropCollectionAsync(name); before.Remove(name); }
        foreach (var counter in emptyCounters) Assert.Equal(1L, (await Rows(database, "idCounters").DeleteOneAsync(counter)).DeletedCount);
        before["idCounters"]["rows"] = new BsonArray(before["idCounters"]["rows"].AsBsonArray.Where(row => !NewOwners.Contains(row["_id"].AsString)));
        Assert.Equal(1L, (await Rows(database, "schemaMigrations").DeleteOneAsync(history[94].AsBsonDocument)).DeletedCount);
        var retained = await HistoryAsync(database); Assert.Equal(94, retained.Count);
        Assert.True(MigrationHistory.IsValid(retained.Select(row => row.AsBsonDocument).ToArray(), 94));
        Assert.Equal(HistoryBytes(new BsonArray(history.Take(94))), HistoryBytes(retained));
        Assert.Equal(before.ToBson(), (await SnapshotAsync(database)).ToBson());
    }

    private static BsonDocument Validator(string name) => name == ReturnedLotVerificationRecords.Collection
        ? ReturnedLotVerificationSchemas.Validator() : AfterSalesStockSourceSchemas.Validator(name);
    private static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name == ReturnedLotVerificationRecords.Collection
        ? [("ux_returned_lot_verification_request", ReturnedLotVerificationSchemas.Key(), true)] : AfterSalesStockSourceSchemas.Keys(name);
    private static Task CreateProspectiveAsync(IMongoDatabase database, string name) => database.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument> {
        Validator = Validator(name), ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error });
    private static async Task RequirePendingOnlyAsync(IMongoDatabase database, bool requireUnusedProspectiveOwners = true)
    {
        Assert.Equal(1L, await Rows(database, "stockSourceProposals").CountDocumentsAsync(new BsonDocument()));
        foreach (var name in new[] { "stockSourceDecisions", "inventoryPools", "inventoryPoolRegistrations", AfterSalesCaseRecords.Collection,
            ReturnQuarantineLedger.Collection, ReturnInspectionRecords.Collection, ReturnResolutionRecords.Collection })
            Assert.Equal(0L, await Rows(database, name).CountDocumentsAsync(new BsonDocument()));
        if (requireUnusedProspectiveOwners) foreach (var name in NewOwners) Assert.Equal(0L, await Rows(database, name).CountDocumentsAsync(new BsonDocument()));
    }
    private static byte[] HistoryBytes(BsonArray history) => new BsonDocument("history", history).ToBson();
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await Rows(database, "schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase database)
    {
        var result = new BsonDocument(); var definitions = await (await database.ListCollectionsAsync()).ToListAsync();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection").OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; var rows = Rows(database, name);
            var indexes = (await (await rows.Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal);
            var state = new BsonDocument { { "definition", definition.DeepClone() }, { "indexes", new BsonArray(indexes) } };
            // The complete immutable1–94 marker prefix is compared separately; new95 lease ownership is legitimate.
            if (name != "schemaMigrations") state.Add("rows", new BsonArray(await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            result.Add(name, state);
        }
        return result;
    }
}
