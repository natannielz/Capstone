using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Procurement;
using B = UnitToko.Platform.Inventory.StockSourceReconciliationBson;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class StockSourceReconciliationMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_partial_upgrade_preserves_stronger_guards_and_creates_no_observation_authority_or_stock()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV73Async(data.Database);
        var required = (await DefinitionAsync(data.Database, B.Proposals))["options"]["validator"].AsBsonDocument;
        var guard = new BsonDocument("_id", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", B.Proposals },
            { "validator", new BsonDocument("$and", new BsonArray { required, guard }) } });
        var proposals = data.Database.GetCollection<BsonDocument>(B.Proposals);
        await proposals.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("poolId", 1),
            new CreateIndexOptions<BsonDocument> { Name = "unknown_partial_reconciliation", Unique = true,
                PartialFilterExpression = new BsonDocument("revision", new BsonDocument("$gt", 1L)) }));
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await proposals.Indexes.DropOneAsync("unknown_partial_reconciliation"); }
        await data.Database.DropCollectionAsync(B.Decisions);
        var before = await SnapshotAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); var after = await SnapshotAsync(data.Database);
        foreach (var table in before.Names) Assert.Equal(before[table], after[table]);
        Assert.True(InventorySchemas.Contains((await DefinitionAsync(data.Database, B.Proposals))["options"]["validator"].AsBsonDocument, guard));
        foreach (var table in StockSourceReconciliationSchemas.Collections) {
            Assert.False(await data.Database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
            await InventoryRepository.RequireStorageAsync(data.Database, table, StockSourceReconciliationSchemas.Validator(table), StockSourceReconciliationSchemas.Keys(table), default);
        }
        foreach (var table in new[] { "stockSourcePolicies", "inventoryPools", "lots", "stockBalances", "movements", "journalEntries" })
            Assert.False(await data.Database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); Assert.Equal(after, await SnapshotAsync(data.Database));
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Real_held_stock_scanned_invoice_and_approved_valuation_cost_survive_upgrade_while_missing_retained_proof_refuses_before_DDL()
    {
        await using var data = await ProcurementStockValuationTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync("reconciliation-retained-valuation");
        var approved = await data.DecideAsync(proposal["proposalId"].AsInt64, "reconciliation-retained-valuation-approved");
        var cost = await data.Service.CostAsync(data.Reader, data.ReceiptId);
        Assert.Equal("partially_valued", cost["state"].AsString); Assert.Equal("11250.00", cost["capitalizedAmount"].AsString);
        var physical = await data.HeldAsync();
        var receipt = await data.Rows("procurementStockReceipts").Find(new BsonDocument("_id", data.ReceiptId)).SingleAsync();
        await RestoreV73Async(data.Database);
        foreach (var (table, filter) in new[] {
            ("supplierDocumentExtractionAudits", new BsonDocument()),
            ("procurementStockValuationAllocations", new BsonDocument("_id", approved["allocationId"])),
            ("accountingAuditEvents", new BsonDocument { { "journalId", approved["journalId"] }, { "kind", "journal.posted" } }),
            ("movements", new BsonDocument("_id", data.Receipt["movementId"])),
            ("procurementQuarantineDispositionHeads", new BsonDocument("_id", receipt["disposition"]["headId"])) }) {
            var original = await data.Rows(table).Find(filter).SingleAsync();
            Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", original["_id"]))).DeletedCount);
            try { await RefusesAsync(data.Database, data.Clock); }
            finally { await data.Rows(table).InsertOneAsync(original); }
        }
        var before = await SnapshotAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await SnapshotAsync(data.Database));
        Assert.Equal(cost, await data.Service.CostAsync(data.Reader, data.ReceiptId));
        Assert.Equal(physical, await data.HeldAsync());
        Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, ProcurementStockValuationTestData.DecideOperation, "reconciliation-retained-valuation-approved"));
        await data.NoPaymentOrReleaseAsync();
    }

    internal static async Task RestoreV73Async(IMongoDatabase db)
    {
        var history = await HistoryAsync(db);
        if (history.Count <= 73) { Assert.All(history, row => Assert.Equal("applied", row["state"])); return; }
        Assert.Equal(74, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        foreach (var table in StockSourceReconciliationSchemas.Collections) {
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", table)).AnyAsync());
        }
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"),
            ("outboxEvents", "sourceKey"), ("idempotencyRecords", "operation"), ("authorizationHistory", "operation"),
            ("movements", "source.namespace"), ("journalEntries", "source.namespace") })
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^stock\.source\.reconciliation(?:[.:]|$)"))).AnyAsync());
        var before = await SnapshotAsync(db);
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 74 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(73)), await HistoryAsync(db)); Assert.Equal(before, await SnapshotAsync(db));
    }
    private static async Task RefusesAsync(IMongoDatabase db, IClock clock)
    {
        var before = await SnapshotAsync(db); var history = new BsonArray((await HistoryAsync(db)).Take(73));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
        Assert.Equal("Stock source reconciliation migration preflight failed", error.Message);
        Assert.Equal(before, await SnapshotAsync(db)); Assert.Equal(history, new BsonArray((await HistoryAsync(db)).Take(73)));
        Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 74 }, { "state", "applied" } }).AnyAsync());
    }
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase db)
    {
        var result = new BsonDocument();
        foreach (var definition in (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; if (name is "schemaMigrations" or "migrationLocks") continue;
            result[name] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray((await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) },
                { "rows", new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string table) => await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
}
