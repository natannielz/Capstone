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
using D = UnitToko.Platform.Procurement.SupplierDocumentBson;
using V = UnitToko.Platform.Procurement.ProcurementStockValuationBson;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementQualityValuationMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_partial_extension_preserves_stronger_guards_and_UUIDs_without_business_or_counter_writes()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV78Async(data.Database);
        var guard = new BsonDocument("_id", new BsonDocument("$ne", 999999L));
        var definition = await DefinitionAsync(data.Database, D.Matches);
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", D.Matches },
            { "validator", new BsonDocument("$and", new BsonArray { definition["options"]["validator"], guard }) } });
        // A completed first collMod followed by interruption must be a valid resumable candidate.
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", V.Proposals },
            { "validator", ProcurementQualityValuationSchemas.CurrentValuationValidator(V.Proposals) } });
        var before = await SnapshotAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        var after = await SnapshotAsync(data.Database); AssertUpgradePreserves(before, after);
        Assert.True(InventorySchemas.Contains(after[D.Matches]["definition"]["options"]["validator"].AsBsonDocument, guard));
        foreach (var table in ProcurementQualityValuationSchemas.Collections.Concat(new[] { "lots", "stockBalances", "movements", "journalEntries" }))
            Assert.Empty(after[table]["rows"].AsBsonArray);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); AssertSnapshotEqual(after, await SnapshotAsync(data.Database));
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Genuine_clean_v1_invoice_and_valuation_history_remain_byte_exact_across_extension()
    {
        await using var data = await ProcurementStockValuationTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync("quality-extension-clean-proposal");
        var result = await data.DecideAsync(proposal["proposalId"].AsInt64, "quality-extension-clean-approved");
        foreach (var table in ProcurementQualityValuationSchemas.Collections)
            Assert.All(await data.Rows(table).Find(new BsonDocument()).ToListAsync(), row => Assert.Equal(1, row["schemaVersion"].AsInt32));
        await RestoreV78Async(data.Database);
        var submission = await data.Rows(D.Submissions).Find(new BsonDocument("_id", data.Documents.DocumentId)).SingleAsync();
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("_id", submission["pinId"])).SingleAsync();
        await data.Rows("evidencePins").DeleteOneAsync(new BsonDocument("_id", pin["_id"]));
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Rows("evidencePins").InsertOneAsync(pin); }
        var before = await SnapshotAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); AssertUpgradePreserves(before, await SnapshotAsync(data.Database));
        Assert.Equal(result, await data.Service.LookupAsync(data.Checker, V.DecideOperation, "quality-extension-clean-approved"));
        Assert.Equal(data.Match, await data.Documents.Service.LookupAsync(data.Documents.Matcher, D.MatchOperation, "valuation-human-invoice-match"));
        await data.NoPaymentOrReleaseAsync();
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Retained_mixed_quality_and_partial_valuation_upgrade_resume_and_refuse_missing_quality_or_private_owners()
    {
        await using var data = await ProcurementStockValuationTestData.CreateAsync(fixture, mixedQuality: true, matchInvoice: false);
        await ProcurementQualityAcceptanceMigrationTests.RestoreV77Async(data.Database);
        // Retained M77 owners are a valid interrupted suffix over the genuine applied76 source.
        Assert.Equal(1L, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 77 }, { "state", "applied" } })).DeletedCount);
        var before = await SnapshotAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        var expected = before.DeepClone().AsBsonDocument;
        // The synthetic supplier policy uses an explicit ID; M77's retained-source floor repair is exact.
        var maximum = expected[SupplierTestData.Policies]["rows"].AsBsonArray.Max(row => row["_id"].AsInt64);
        var counters = expected["idCounters"]["rows"].AsBsonArray;
        var policyCounter = counters.SingleOrDefault(row => row["_id"] == SupplierTestData.Policies)?.AsBsonDocument;
        if (policyCounter is null) counters.Add(new BsonDocument { { "_id", SupplierTestData.Policies }, { "value", maximum } });
        else policyCounter["value"] = Math.Max(policyCounter["value"].AsInt64, maximum);
        expected["idCounters"]["rows"] = new BsonArray(counters.OrderBy(row => row["_id"].AsString, StringComparer.Ordinal));
        AssertUpgradePreserves(expected, await SnapshotAsync(data.Database));

        var mixedInput = data.MixedMatchInput ?? throw new InvalidOperationException("The genuine mixed fixture must expose its quality-backed invoice input");
        data.Match = await data.Documents.MatchAsync("quality-extension-mixed-invoice", mixedInput);
        var proposed = await data.ProposeAsync("quality-extension-mixed-proposal");
        var result = await data.DecideAsync(proposed["proposalId"].AsInt64, "quality-extension-mixed-approved");
        foreach (var table in ProcurementQualityValuationSchemas.Collections)
            Assert.All(await data.Rows(table).Find(new BsonDocument()).ToListAsync(), row => Assert.Equal(2, row["schemaVersion"].AsInt32));
        var original = await SnapshotAsync(data.Database);
        Assert.NotNull(await Record.ExceptionAsync(() => RestoreV78Async(data.Database))); AssertSnapshotEqual(original, await SnapshotAsync(data.Database));
        var match = await data.Rows(D.Matches).Find(new BsonDocument("_id", data.MatchId)).SingleAsync();
        var quality = match["links"][0]["quality"].AsBsonDocument;
        var submission = await data.Rows(D.Submissions).Find(new BsonDocument("_id", data.Documents.DocumentId)).SingleAsync();
        Assert.Equal(1L, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 79 }, { "state", "applied" } })).DeletedCount);
        foreach (var (table, id) in new[] {
            (ProcurementQualityAcceptanceBson.Allocations, quality["allocationId"]),
            (ProcurementQualityAcceptanceBson.Decisions, quality["decisionId"]),
            ("evidencePins", submission["pinId"]) }) {
            var row = await data.Rows(table).Find(new BsonDocument("_id", id)).SingleAsync();
            Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", id))).DeletedCount);
            try { await RefusesAsync(data.Database, data.Clock); }
            finally { await data.Rows(table).InsertOneAsync(row); }
        }
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); AssertSnapshotEqual(original, await SnapshotAsync(data.Database));
        Assert.Equal(result, await data.Service.LookupAsync(data.Checker, V.DecideOperation, "quality-extension-mixed-approved"));
        foreach (var mutate in new Action<BsonDocument>[] {
            row => row["input"]["lines"][0]["qualityAllocationId"] = BsonNull.Value,
            row => row["links"][0]["quality"].AsBsonDocument.Remove("decisionHash"),
            row => row["lines"][0]["quality"]["receiptId"] = quality["receiptId"].AsInt64 + 1L,
            row => row["schemaVersion"] = 1 }) {
            var malformed = match.DeepClone().AsBsonDocument; mutate(malformed);
            await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(D.Matches).ReplaceOneAsync(new BsonDocument("_id", data.MatchId), malformed));
            Assert.Equal(match, await data.Rows(D.Matches).Find(new BsonDocument("_id", data.MatchId)).SingleAsync());
        }
        await data.NoPaymentOrReleaseAsync();
    }

    [Fact]
    public async Task Missing_predecessor_unsafe_index_or_unrecognized_validator_refuses_before_any_DDL()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV78Async(data.Database);
        const string required = "procurementQualityAcceptanceAllocations", parked = "synthetic_parked_quality_allocations";
        await data.Database.RenameCollectionAsync(required, parked);
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Database.RenameCollectionAsync(parked, required); }
        var index = ProcurementQualityValuationSchemas.LookupIndexes[0];
        await data.Database.GetCollection<BsonDocument>(index.Table).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(index.Key,
            new CreateIndexOptions<BsonDocument> { Name = index.Name, PartialFilterExpression = new BsonDocument("schemaVersion", 2) }));
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Database.GetCollection<BsonDocument>(index.Table).Indexes.DropOneAsync(index.Name); }
        var definition = await DefinitionAsync(data.Database, D.Matches);
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", D.Matches }, { "validator", new BsonDocument("_id", new BsonDocument("$gt", 0L)) } });
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", D.Matches }, { "validator", definition["options"]["validator"] } }); }
    }

    // Test-only predecessor isolation. No positive v2 owner can be relabelled as a v1 history.
    internal static async Task RestoreV78Async(IMongoDatabase db) {
        var history = await HistoryAsync(db);
        if (history.Count <= 78) { Assert.All(history, row => Assert.Equal("applied", row["state"])); return; }
        Assert.Equal(79, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        foreach (var table in ProcurementQualityValuationSchemas.Collections) {
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument("$nor", new BsonArray { Legacy(table) })).AnyAsync());
            foreach (var field in new[] { "quality", "qualityCapacity", "input.lines.qualityAllocationId", "lines.quality", "links.quality" })
                Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument(field, new BsonDocument("$exists", true))).AnyAsync());
        }
        // A deleted v2 row must not hide behind retained effect receipts in a shared namespace.
        foreach (var (operation, table, resultId) in new[] { (D.MatchOperation, D.Matches, "matchId"), (V.ProposeOperation, V.Proposals, "proposalId"), (V.DecideOperation, V.Decisions, "decisionId") })
            foreach (var (effects, field, payload) in new[] { ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload"), ("idempotencyRecords", "operation", "result") })
                foreach (var effect in await db.GetCollection<BsonDocument>(effects).Find(new BsonDocument(field, operation)).ToListAsync()) {
                    var id = effect[payload][resultId]; Assert.True(id.IsInt64 && id.AsInt64 > 0);
                    Assert.True(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument { { "_id", id }, { "$and", new BsonArray { Legacy(table) } } }).AnyAsync());
                }
        var before = await SnapshotAsync(db);
        foreach (var table in ProcurementQualityValuationSchemas.Collections) {
            var current = before[table]["definition"]["options"]["validator"].AsBsonDocument;
            var restored = PaymentManualReviewMigration.ExtendValidator(current, Current(table), Legacy(table), table);
            await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", restored } });
        }
        foreach (var (table, name, _) in ProcurementQualityValuationSchemas.LookupIndexes) {
            var indexes = await (await db.GetCollection<BsonDocument>(table).Indexes.ListAsync()).ToListAsync();
            if (indexes.Any(index => index["name"] == name)) await db.GetCollection<BsonDocument>(table).Indexes.DropOneAsync(name);
        }
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 79 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(78)), await HistoryAsync(db));
        var after = await SnapshotAsync(db);
        // Reversing only the schema/index extension must reproduce the exact pre-isolation snapshot.
        AssertUpgradePreserves(after, before);
    }

    internal static async Task VerifyPublishedRequestUpgradeAsync(IMongoDatabase db, IClock clock) {
        Assert.True(await db.GetCollection<BsonDocument>("branchOrderRequests").Find(new BsonDocument()).AnyAsync());
        Assert.True(await db.GetCollection<BsonDocument>("branchOrderRequestDecisions").Find(new BsonDocument()).AnyAsync());
        await RestoreV78Async(db);
        var publishedHistory = await HistoryAsync(db);
        var before = await SnapshotAsync(db);
        await new CollectionMigrations(db, clock).ApplyAsync();
        AssertUpgradePreserves(before, await SnapshotAsync(db));
        Assert.Equal(publishedHistory, new BsonArray((await HistoryAsync(db)).Take(78)));
    }

    private static async Task RefusesAsync(IMongoDatabase db, IClock clock) {
        var before = await SnapshotAsync(db);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
        Assert.Equal("Procurement quality valuation migration preflight failed", error.Message); AssertSnapshotEqual(before, await SnapshotAsync(db));
        Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 79 }, { "state", "applied" } }).AnyAsync());
    }
    private static BsonDocument Legacy(string table) => table == D.Matches ? SupplierDocumentSchemas.Validator(table) : ProcurementStockValuationSchemas.Validator(table);
    private static BsonDocument Current(string table) => table == D.Matches ? ProcurementQualityValuationSchemas.CurrentDocumentValidator(table) : ProcurementQualityValuationSchemas.CurrentValuationValidator(table);
    private static void AssertUpgradePreserves(BsonDocument before, BsonDocument after) {
        var expected = before.DeepClone().AsBsonDocument;
        foreach (var table in ProcurementQualityValuationSchemas.Collections)
            expected[table]["definition"]["options"]["validator"] = PaymentManualReviewMigration.ExtendValidator(
                expected[table]["definition"]["options"]["validator"].AsBsonDocument, Legacy(table), Current(table), table);
        foreach (var (table, name, key) in ProcurementQualityValuationSchemas.LookupIndexes) {
            var found = Assert.Single(after[table]["indexes"].AsBsonArray, index => index["name"] == name);
            Assert.Equal(new BsonDocument { { "v", 2 }, { "key", key }, { "name", name } }, found);
            if (!expected[table]["indexes"].AsBsonArray.Any(index => index["name"] == name))
                expected[table]["indexes"] = new BsonArray(expected[table]["indexes"].AsBsonArray.Append(found).OrderBy(index => index["name"].AsString, StringComparer.Ordinal));
        }
        AssertSnapshotEqual(expected, after);
    }
    private static void AssertSnapshotEqual(BsonDocument expected, BsonDocument actual) {
        Assert.Equal(expected.Names.ToArray(), actual.Names.ToArray());
        foreach (var name in expected.Names) {
            if (expected[name].AsBsonDocument.ToBson().SequenceEqual(actual[name].AsBsonDocument.ToBson())) continue;
            var settings = new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson };
            var left = expected[name].ToJson(settings); var right = actual[name].ToJson(settings); var position = 0;
            while (position < Math.Min(left.Length, right.Length) && left[position] == right[position]) position++;
            var start = Math.Max(0, position - 120);
            Assert.Fail($"Exact snapshot differs in '{name}' at character {position}. Expected: {left.Substring(start, Math.Min(320, left.Length - start))}\nActual: {right.Substring(start, Math.Min(320, right.Length - start))}");
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
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string name) => await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
}
