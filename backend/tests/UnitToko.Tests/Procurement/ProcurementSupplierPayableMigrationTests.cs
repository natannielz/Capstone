using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementSupplierPayableMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_resumable_upgrade_keeps_stronger_guards_and_creates_no_payable_advance_policy_or_journal()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV74Async(data.Database);
        var required = (await DefinitionAsync(data.Database, B.Proposals))["options"]["validator"].AsBsonDocument;
        var guard = new BsonDocument("_id", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", B.Proposals },
            { "validator", new BsonDocument("$and", new BsonArray { required, guard }) } });
        var proposals = data.Database.GetCollection<BsonDocument>(B.Proposals);
        await proposals.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("policyId", 1),
            new CreateIndexOptions<BsonDocument> { Name = "unknown_partial_payable", Unique = true,
                PartialFilterExpression = new BsonDocument("version", new BsonDocument("$gt", 1L)) }));
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await proposals.Indexes.DropOneAsync("unknown_partial_payable"); }
        await data.Database.DropCollectionAsync(B.Applications); var before = await SnapshotAsync(data.Database);
        await Upgrade75Async(data.Database, data.Clock); var after = await SnapshotAsync(data.Database);
        foreach (var table in before.Names) Assert.Equal(before[table], after[table]);
        Assert.True(InventorySchemas.Contains((await DefinitionAsync(data.Database, B.Proposals))["options"]["validator"].AsBsonDocument, guard));
        foreach (var table in B.Collections) {
            Assert.False(await data.Database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
            await InventoryRepository.RequireStorageAsync(data.Database, table, ProcurementSupplierPayableSchemas.Validator(table), ProcurementSupplierPayableSchemas.Keys(table), default);
        }
        foreach (var table in new[] { "journalEntries", "accountingBooks", "bankTransactionClaims", "procurementSettlements", "lots", "stockBalances", "movements" })
            Assert.False(await data.Database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
        await Upgrade75Async(data.Database, data.Clock); Assert.Equal(after, await SnapshotAsync(data.Database));
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Real_valuation_private_invoice_and_verified_bank_advance_survive_upgrade_and_missing_proof_refuses_before_DDL()
    {
        await using var data = await ProcurementSupplierPayableTestData.CreateAsync(fixture, createPolicy: false);
        var originals = await data.ProtectedAsync(); var cost = await data.Valuation.Service.CostAsync(data.Valuation.Reader, data.Valuation.ReceiptId);
        var settlement = await data.Rows("procurementSettlements").Find(new BsonDocument("_id", data.SettlementId)).SingleAsync();
        var submission = await data.Rows("supplierDocumentSubmissions").Find(new BsonDocument("_id", data.Documents.DocumentId)).SingleAsync();
        Assert.Equal(2, (await data.Rows("bankTransactionClaims").Find(new BsonDocument("_id", settlement["canonicalIdentity"])).SingleAsync())["schemaVersion"].AsInt32);
        Assert.Equal(cost, await data.Valuation.Service.CostAsync(data.Valuation.Reader, data.Valuation.ReceiptId));
        Assert.Equal(data.Settlement, await data.Settlements.LookupAsync(data.BankVerifier, "procurement.settlement.verify", "payable-source-settlement"));
        await RestoreV74Async(data.Database);
        foreach (var (table, filter) in new[] {
            ("procurementStockValuationAllocations", new BsonDocument("_id", data.AllocationId)),
            ("accountingAuditEvents", new BsonDocument { { "journalId", data.ApprovedValuation["journalId"] }, { "kind", "journal.posted" } }),
            ("bankTransactionClaims", new BsonDocument("_id", settlement["canonicalIdentity"])),
            ("procurementSettlements", new BsonDocument("_id", data.SettlementId)),
            ("evidencePins", new BsonDocument("_id", submission["pinId"])) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync();
            Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]))).DeletedCount);
            try { await RefusesAsync(data.Database, data.Clock); }
            finally { await data.Rows(table).InsertOneAsync(row); }
        }
        var before = await SnapshotAsync(data.Database);
        await Upgrade75Async(data.Database, data.Clock); Assert.Equal(before, await SnapshotAsync(data.Database));
        Assert.Equal(originals, await data.ProtectedAsync());
        // Exact unchanged owner rows, journals, effects and definitions preserve the current-runtime proofs above.
        foreach (var table in B.Collections) Assert.False(await data.Rows(table).Find(new BsonDocument()).AnyAsync());
    }

    internal static async Task RestoreV74Async(IMongoDatabase db)
    {
        await ProcurementAdvanceReturnMigrationTests.RestoreV75Async(db);
        var history = await HistoryAsync(db);
        if (history.Count <= 74) { Assert.All(history, row => Assert.Equal("applied", row["state"])); return; }
        Assert.Equal(75, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        foreach (var table in B.Collections) {
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", table)).AnyAsync());
        }
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"),
            ("outboxEvents", "sourceKey"), ("idempotencyRecords", "operation"), ("authorizationHistory", "operation"),
            ("movements", "source.namespace"), ("journalEntries", "source.namespace"), ("journalEntries", "sourceEvent") })
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.payable(?:[.:]|$)"))).AnyAsync());
        var before = await SnapshotAsync(db);
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 75 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(74)), await HistoryAsync(db)); Assert.Equal(before, await SnapshotAsync(db));
    }
    private static async Task RefusesAsync(IMongoDatabase db, IClock clock)
    {
        var before = await SnapshotAsync(db); var history = new BsonArray((await HistoryAsync(db)).Take(74));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Upgrade75Async(db, clock));
        Assert.Equal("Procurement supplier payable migration preflight failed", error.Message);
        Assert.Equal(before, await SnapshotAsync(db)); Assert.Equal(history, new BsonArray((await HistoryAsync(db)).Take(74)));
        Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 75 }, { "state", "applied" } }).AnyAsync());
    }
    private static async Task Upgrade75Async(IMongoDatabase database, IClock clock)
    {
        var db = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        var lease = await new MigrationGate(db, clock, 75).AcquireAsync(75, default);
        if (lease is null) return;
        try { await new ProcurementSupplierPayableMigration(db, clock).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
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
