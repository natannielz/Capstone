using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Procurement.ProcurementStockValuationBson;
using D = UnitToko.Tests.Procurement.ProcurementStockValuationTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementStockValuationMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_partial_upgrade_preserves_stronger_guards_and_installs_no_finance_policy_or_accounting_facts()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV72Async(data.Database);
        var guard = new BsonDocument("_id", new BsonDocument("$ne", 999999L));
        var current = (await DefinitionAsync(data.Database, B.Proposals))["options"]["validator"];
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", B.Proposals }, { "validator", new BsonDocument("$and", new BsonArray { current, guard }) } });
        var allocations = data.Database.GetCollection<BsonDocument>(B.Allocations);
        await allocations.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("invoiceMatchId", 1),
            new CreateIndexOptions<BsonDocument> { Name = "unknown_partial_valuation", Unique = true, PartialFilterExpression = new BsonDocument("packages", new BsonDocument("$gt", 1)) }));
        try { await RefusesAsync(data.Database, data.Clock); } finally { await allocations.Indexes.DropOneAsync("unknown_partial_valuation"); }
        Assert.False(await data.Database.GetCollection<BsonDocument>(B.MatchHeads).Find(new BsonDocument()).AnyAsync());
        await data.Database.DropCollectionAsync(B.MatchHeads);
        var before = await SnapshotAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); var after = await SnapshotAsync(data.Database);
        foreach (var table in before.Names) Assert.Equal(before[table], after[table]);
        Assert.True(InventorySchemas.Contains((await DefinitionAsync(data.Database, B.Proposals))["options"]["validator"].AsBsonDocument, guard));
        foreach (var table in ProcurementStockValuationSchemas.Collections) {
            Assert.False(await data.Database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
            await InventoryRepository.RequireStorageAsync(data.Database, table, ProcurementStockValuationSchemas.Validator(table), ProcurementStockValuationSchemas.Keys(table), default);
        }
        foreach (var table in new[] { "journalEntries", "lots", "stockBalances", "movements", "accountingBooks", "accountingChartVersions" })
            Assert.False(await data.Database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); Assert.Equal(after, await SnapshotAsync(data.Database));
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Real_clean_held_receipt_and_scanned_matched_invoice_survive_upgrade_and_missing_source_proof_refuses_before_DDL()
    {
        await using var data = await D.CreateAsync(fixture, createValuationPolicy: false); var preserved = await data.PreservedAsync();
        await RestoreV72Async(data.Database);
        foreach (var (table, filter) in new[] {
            ("movements", new BsonDocument("_id", data.Receipt["movementId"])),
            ("supplierDocumentExtractionAudits", new BsonDocument()) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync(); var exact = new BsonDocument("_id", row["_id"]);
            Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(exact)).DeletedCount);
            try { await RefusesAsync(data.Database, data.Clock); } finally { await data.Rows(table).InsertOneAsync(row); }
        }
        var before = await SnapshotAsync(data.Database); var expected = before.DeepClone().AsBsonDocument;
        // This genuine supplier-policy fixture is operator input and has no allocator receipt yet.
        var supplierPolicy = await data.Rows(SupplierTestData.Policies).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstAsync();
        var counters = expected["idCounters"]["rows"].AsBsonArray;
        Assert.DoesNotContain(counters, row => row["_id"] == SupplierTestData.Policies);
        expected["idCounters"]["rows"] = new BsonArray(counters.Append(new BsonDocument { { "_id", SupplierTestData.Policies }, { "value", supplierPolicy["_id"] } }).OrderBy(row => row["_id"].AsString, StringComparer.Ordinal));
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); Assert.Equal(expected, await SnapshotAsync(data.Database));
        Assert.Equal(preserved, await data.PreservedAsync());
        var extraction = await data.Documents.Evidence.ReadExtractionAsync(data.Documents.EvidenceReader, data.Documents.DraftId, 1, data.Documents.Reference,
            new("supplier-document-ocr-view", "synthetic-valuation-human-invoice-review"));
        Assert.Equal("succeeded", extraction.State); Assert.Equal("34000.00", Assert.Single(extraction.Fields, field => field.Field == "total_amount").Value);
        Assert.Equal(data.Receipt, await data.Receipts.LookupAsync(data.Maker, ProcurementStockReceiptTestData.Operation, "valuation-held-receipt"));
        Assert.Equal(data.Match, await data.Documents.Service.LookupAsync(data.Documents.Matcher, SupplierDocumentTestData.MatchOperation, "valuation-human-invoice-match"));
        foreach (var table in ProcurementStockValuationSchemas.Collections) Assert.False(await data.Rows(table).Find(new BsonDocument()).AnyAsync());
        await data.NoPaymentOrReleaseAsync();
    }

    internal static async Task RestoreV72Async(IMongoDatabase db)
    {
        var history = await HistoryAsync(db);
        if (history.Count <= 72) { Assert.All(history, row => Assert.Equal("applied", row["state"])); return; }
        Assert.Equal(73, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        foreach (var table in ProcurementStockValuationSchemas.Collections) {
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", table)).AnyAsync());
        }
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"),
            ("idempotencyRecords", "operation"), ("authorizationHistory", "operation"), ("journalEntries", "source.namespace"), ("journalEntries", "sourceEvent"),
            ("movements", "source.namespace"), ("movements", "sourceEvent"), ("accountingAuditEvents", "sourceEvent") })
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.stockvaluation(?:[.:]|$)"))).AnyAsync());
        var before = await SnapshotAsync(db);
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 73 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(72)), await HistoryAsync(db)); Assert.Equal(before, await SnapshotAsync(db));
    }
    private static async Task RefusesAsync(IMongoDatabase db, IClock clock)
    {
        var before = await SnapshotAsync(db); var history = new BsonArray((await HistoryAsync(db)).Take(72));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
        Assert.Equal("Procurement stock valuation migration preflight failed", error.Message);
        Assert.Equal(before, await SnapshotAsync(db)); Assert.Equal(history, new BsonArray((await HistoryAsync(db)).Take(72)));
        Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 73 }, { "state", "applied" } }).AnyAsync());
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
