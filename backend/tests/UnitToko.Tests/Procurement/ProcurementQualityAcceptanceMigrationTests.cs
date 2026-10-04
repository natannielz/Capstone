using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementQualityAcceptanceMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_additive_upgrade_resumes_preserves_stronger_guards_and_creates_no_quality_or_stock_owners()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV76Async(data.Database);
        var definition = await DefinitionAsync(data.Database, B.Proposals);
        var guard = new BsonDocument("_id", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", B.Proposals },
            { "validator", new BsonDocument("$and", new BsonArray { definition["options"]["validator"], guard }) } });
        await data.Database.DropCollectionAsync(B.Allocations);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.True(InventorySchemas.Contains((await DefinitionAsync(data.Database, B.Proposals))["options"]["validator"].AsBsonDocument, guard));
        foreach (var table in ProcurementQualityAcceptanceSchemas.Collections) {
            Assert.False(await data.Database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
            await InventoryRepository.RequireStorageAsync(data.Database, table, ProcurementQualityAcceptanceSchemas.Validator(table), ProcurementQualityAcceptanceSchemas.Keys(table), default);
        }
        foreach (var table in new[] { "lots", "stockBalances", "movements", "journalEntries" })
            Assert.False(await data.Database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
        var installed = await SnapshotAsync(data.Database); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(installed, await SnapshotAsync(data.Database));
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Genuine_mixed_held_receipt_survives_upgrade_and_missing_private_source_refuses_before_DDL()
    {
        await using var data = await ProcurementQualityAcceptanceTestData.CreateAsync(fixture, createPolicy: false);
        await RestoreV76Async(data.Database); var before = await SnapshotAsync(data.Database);
        var receipt = await data.Rows(ProcurementStockReceiptBson.Receipts).Find(new BsonDocument("_id", data.ReceiptId)).SingleAsync();
        var inspection = await data.Rows(ProcurementInspectionBson.Submissions).Find(new BsonDocument("_id", receipt["inspectionId"])).SingleAsync();
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("_id", inspection["pinId"])).SingleAsync();
        foreach (var (table, row) in new[] { ("evidencePins", pin), ("movements", await data.Rows("movements").Find(new BsonDocument("_id", receipt["movementId"])).SingleAsync()) }) {
            await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]));
            try { await RefusesAsync(data.Database, data.Clock); }
            finally { await data.Rows(table).InsertOneAsync(row); }
        }
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        AssertSnapshotEqual(WithRetainedSupplierPolicyFloor(before), await SnapshotAsync(data.Database));
        Assert.Equal(receipt, await data.Rows(ProcurementStockReceiptBson.Receipts).Find(new BsonDocument("_id", data.ReceiptId)).SingleAsync());
        Assert.Equal("3", (await data.Stock.Service.ReadAsync(data.Stock.Reader, data.ReceiptId))["held"].ToString());
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Retained_quality_owner_is_reconstructed_and_missing_chain_cannot_be_called_an_empty_predecessor()
    {
        await using var data = await ProcurementQualityAcceptanceTestData.CreateAsync(fixture);
        var result = await data.DecideAsync((await data.ProposeAsync("quality-migration"))["proposalId"].AsInt64, "quality-migration-approved");
        var unchanged = await SnapshotAsync(data.Database);
        Assert.NotNull(await Record.ExceptionAsync(() => RestoreV76Async(data.Database))); AssertSnapshotEqual(unchanged, await SnapshotAsync(data.Database));
        // Simulate a retained owner in a resumable candidate; the production step must prove it before any DDL.
        Assert.Equal(1L, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 77 }, { "state", "applied" } })).DeletedCount);
        foreach (var (table, selected) in new[] { (B.ReceiptHeads, (BsonValue)data.ReceiptId), (B.Allocations, result["allocationId"]), (B.Decisions, result["decisionId"]) }) {
            var row = await data.Rows(table).Find(new BsonDocument("_id", selected)).SingleAsync(); await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", selected));
            try { await RefusesAsync(data.Database, data.Clock); }
            finally { await data.Rows(table).InsertOneAsync(row); }
        }
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); AssertSnapshotEqual(WithRetainedSupplierPolicyFloor(unchanged), await SnapshotAsync(data.Database));
        Assert.Equal(result, (await data.Service.LookupAsync(data.Checker, B.DecideOperation, "quality-migration-approved"))["result"]);
    }

    [Fact]
    public async Task Unsafe_candidate_indexes_and_missing_predecessor_storage_fail_without_DDL()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV76Async(data.Database);
        var rows = data.Database.GetCollection<BsonDocument>(B.Proposals);
        await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("input.receiptId", 1), new CreateIndexOptions<BsonDocument> {
            Name = "unsafe_quality_partial", PartialFilterExpression = new BsonDocument("version", new BsonDocument("$gt", 1L)) }));
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await rows.Indexes.DropOneAsync("unsafe_quality_partial"); }
        const string required = "procurementAdvanceReturnDecisions", parked = "synthetic_parked_return_decisions";
        await data.Database.RenameCollectionAsync(required, parked);
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Database.RenameCollectionAsync(parked, required); }
    }

    // Test-only predecessor isolation: refuse every actual M77 owner, counter and effect.
    // Preserve all shared rows, validators, indexes and collection UUIDs; remove only the empty suffix receipt.
    internal static async Task RestoreV76Async(IMongoDatabase db) {
        var history = await HistoryAsync(db);
        if (history.Count <= 76) { Assert.All(history, row => Assert.Equal("applied", row["state"])); return; }
        Assert.Equal(77, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        foreach (var table in ProcurementQualityAcceptanceSchemas.Collections) {
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", table)).AnyAsync());
        }
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"),
            ("outboxEvents", "sourceKey"), ("idempotencyRecords", "operation"), ("authorizationHistory", "operation"),
            ("movements", "source.namespace"), ("journalEntries", "source.namespace"), ("journalEntries", "sourceEvent") })
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.qualityacceptance(?:[.:]|$)"))).AnyAsync());
        var before = await SnapshotAsync(db);
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 77 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(76)), await HistoryAsync(db)); Assert.Equal(before, await SnapshotAsync(db));
    }
    private static async Task RefusesAsync(IMongoDatabase db, IClock clock) {
        var before = await SnapshotAsync(db);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
        Assert.Equal("Procurement quality acceptance migration preflight failed", error.Message); Assert.Equal(before, await SnapshotAsync(db));
        Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 77 }, { "state", "applied" } }).AnyAsync());
    }
    private static BsonDocument WithRetainedSupplierPolicyFloor(BsonDocument before) {
        // SupplierTestData inserts its explicit synthetic policy ID without allocating it.
        // The retained-source migration legitimately installs that one allocator floor.
        var expected = before.DeepClone().AsBsonDocument;
        var maximum = expected[SupplierTestData.Policies]["rows"].AsBsonArray.Select(row => row["_id"].AsInt64).Max();
        Assert.True(maximum > 0);
        var counters = expected["idCounters"]["rows"].AsBsonArray;
        var previous = counters.SingleOrDefault(row => row["_id"] == SupplierTestData.Policies)?.AsBsonDocument;
        if (previous is null) counters.Add(new BsonDocument { { "_id", SupplierTestData.Policies }, { "value", maximum } });
        else previous["value"] = Math.Max(previous["value"].AsInt64, maximum);
        expected["idCounters"]["rows"] = new BsonArray(counters.OrderBy(row => row["_id"].AsString, StringComparer.Ordinal));
        return expected;
    }
    private static void AssertSnapshotEqual(BsonDocument expected, BsonDocument actual) {
        Assert.Equal(expected.Names.Order(StringComparer.Ordinal).ToArray(), actual.Names.Order(StringComparer.Ordinal).ToArray());
        foreach (var name in expected.Names) {
            var expectedBytes = expected[name].AsBsonDocument.ToBson();
            var actualBytes = actual[name].AsBsonDocument.ToBson();
            if (expectedBytes.SequenceEqual(actualBytes)) continue;
            var settings = new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson };
            var expectedText = expected[name].ToJson(settings); var actualText = actual[name].ToJson(settings);
            var position = 0;
            while (position < Math.Min(expectedText.Length, actualText.Length) && expectedText[position] == actualText[position]) position++;
            static string Context(string text, int position) {
                var start = Math.Max(0, position - 160);
                return text.Substring(start, Math.Min(400, text.Length - start));
            }
            Assert.Fail($"Snapshot collection '{name}' differs in exact BSON bytes ({expectedBytes.Length} expected / {actualBytes.Length} actual). " +
                $"First serialized character difference: {position}; text lengths {expectedText.Length} / {actualText.Length}. " +
                $"Expected context: {Context(expectedText, position)}\nActual context: {Context(actualText, position)}");
        }
    }
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase db) {
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
