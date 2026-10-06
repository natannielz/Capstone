using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Inventory;

/// <summary>Required genuine stopped102 runtime input, never a current-code fixture
/// relabelled as102. No future owners/history/counters or original data are removed.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class StockReleaseGenuinePredecessorTests(MongoFixture fixture) {
    internal const string HandoffEnvironment = "UNITTOKO_STOCK_RELEASE_PREDECESSOR102";
    private static string[] Future => [.. StockReleaseSchemas.Collections, .. LotValueStore.Collections];
    private static readonly Dictionary<string, string> Pins = new(StringComparer.Ordinal) {
        ["UnitToko.Api"] = "AEDB16FF983CED54CB0520DB15EC62E1A1548C0870C0E4770D25E3E56200C59F",
        ["UnitToko.Platform"] = "32CDCC9C3D64D6CA55540309949735007277A6325D611928F770D77A7416E7FE",
        ["UnitToko.Modules"] = "4608B876F18443AC5721264BEC8FB7D8F0B829C1A15EE8DD90C736490FE2736D",
        ["MongoDB.Driver"] = "4B920940FD9C325772830F0180EC7B28DF099647E23B79D732EBBCF93E6FE1A1",
        ["MongoDB.Bson"] = "162F2E0F82195544D97A96765C9CB9DEAEDBFD9DD1600CE1B3D128DB9FCB4DC8" };

    [WindowsStorageFact]
    public async Task Genuine_stopped102_activated_held_quality_posted_value_upgrade_to103_preserves_all_predecessor_bytes_and_original_keys() {
        var path = Environment.GetEnvironmentVariable(HandoffEnvironment);
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Stopped genuine102 held/quality/posted-value producer handoff is required; genuine102 migration acceptance is unavailable");
        var writer = Path.GetFullPath(Path.Combine(FindRoot(), "tmp/production/stock-release-genuine102-producer-release1"));
        path = Path.GetFullPath(path); RequireChild(writer, path); Assert.Equal("HANDOFF.json", Path.GetFileName(path));
        var directory = Path.GetDirectoryName(path)!;
        Assert.Equal(writer, Path.GetDirectoryName(directory)); Assert.Matches(@"\Acapsule-[a-z0-9-]{1,48}\z", Path.GetFileName(directory));
        using var readyDocument = JsonDocument.Parse(await File.ReadAllTextAsync(path)); var ready = readyDocument.RootElement;
        using var stopDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "PRODUCER-STOPPED.json"))); var stop = stopDocument.RootElement;
        using var observedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "PRODUCER-EXTERNAL-OBSERVATION.json"))); var observed = observedDocument.RootElement;
        Assert.Equal("GENUINE102_HELD_QUALITY_POSTED_VALUE_READY", Text(ready, "status"));
        Assert.True(ready.GetProperty("syntheticLocalOnly").GetBoolean()); Assert.False(ready.GetProperty("productionAcceptance").GetBoolean());
        var name = Text(ready, "databaseName"); Assert.Matches(@"\Aunittoko_test_[0-9a-f]{32}_[0-9a-f]{8}\z", name);
        foreach (var facts in new[] { ready, stop, observed }) {
            Assert.Equal(102, facts.GetProperty("schema").GetInt32()); Assert.Equal(name, Text(facts, "databaseName"));
            Assert.Equal("ABSENT_NOT_REMOVED", Text(facts, "futureOwnersOrCounters"));
        }
        Assert.True(stop.GetProperty("producerWritesStopped").GetBoolean()); Assert.True(stop.GetProperty("databaseRetained").GetBoolean());
        Assert.False(stop.GetProperty("fixtureDisposed").GetBoolean()); Assert.Equal(0, stop.GetProperty("producerExitCode").GetInt32());
        Assert.True(observed.GetProperty("processStopped").GetBoolean()); Assert.Equal(0, observed.GetProperty("observedProducerExitCode").GetInt32());
        var pid = stop.GetProperty("processId").GetInt32(); Assert.True(pid > 0); Assert.Equal(pid, observed.GetProperty("processId").GetInt32());
        try { using var producer = Process.GetProcessById(pid); Assert.Fail("The exact genuine102 producer PID is still running"); }
        catch (ArgumentException) { }
        CheckFile(writer, path, Text(observed, "handoffSha256"));
        foreach (var (pathField, hashField) in new[] { ("executedProgramPath", "executedProgramSha256"), ("executedHostPath", "executedHostSha256"),
            ("retainedSnapshotPath", "retainedSnapshotSha256"), ("preservedBindingPath", "preservedBindingSha256"), ("preparationManifestPath", "preparationManifestSha256") }) {
            CheckFile(writer, Text(ready, pathField), Text(ready, hashField));
            if (stop.TryGetProperty(hashField, out var stopHash)) Assert.Equal(Text(ready, hashField), stopHash.GetString());
            if (observed.TryGetProperty(hashField, out var observedHash)) Assert.Equal(Text(ready, hashField), observedHash.GetString());
        }
        foreach (var binary in ready.GetProperty("runtimeBinaries").EnumerateArray()) {
            Assert.Equal(Pins[Text(binary, "name")], Text(binary, "sha256")); CheckFile(writer, Text(binary, "path"), Text(binary, "sha256"));
        }
        Assert.Equal(Pins.Count, ready.GetProperty("runtimeBinaries").GetArrayLength());
        using var preparation = JsonDocument.Parse(await File.ReadAllTextAsync(Text(ready, "preparationManifestPath")));
        Assert.Equal(102, preparation.RootElement.GetProperty("schema").GetInt32());
        foreach (var file in preparation.RootElement.GetProperty("files").EnumerateArray())
            CheckFile(writer, Path.Combine(writer, Text(file, "relativePath")), Text(file, "sha256"));
        var database = fixture.Client.GetDatabase(name); var clock = new MutableAuthorizationClock(DateTimeOffset.Parse(Text(ready, "businessClockUtc"), CultureInfo.InvariantCulture));
        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
        var before = await Snapshot(database); var history = before["schemaMigrations"]["rows"].AsBsonArray;
        Assert.Equal(102, history.Count); Assert.True(MigrationHistory.IsValid(history.Select(row => row.AsBsonDocument).ToArray(), 102));
        Assert.All(history, row => Assert.Equal("applied", row["state"]));
        using var capturedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Text(ready, "retainedSnapshotPath"))); var captured = capturedDocument.RootElement;
        Assert.Equal(102, captured.GetProperty("schema").GetInt32()); Assert.Equal(name, Text(captured, "databaseName"));
        Assert.Equal(before.ElementCount, captured.GetProperty("collections").GetArrayLength());
        foreach (var collection in captured.GetProperty("collections").EnumerateArray()) {
            var table = Text(collection, "name"); Assert.True(before.Contains(table)); var actual = before[table].AsBsonDocument;
            foreach (var part in new[] { "definition", "indexes", "rows" }) {
                var bytes = Convert.FromBase64String(Text(collection, part + "BsonBase64")); Assert.Equal(Text(collection, part + "Sha256"), Hash(bytes));
                var expected = BsonSerializer.Deserialize<BsonDocument>(bytes);
                Same(expected, part == "definition" ? actual[part].AsBsonDocument : new BsonDocument(part, actual[part]), "genuine102 " + table + " " + part);
            }
        }
        foreach (var table in Future) { Assert.False(before.Contains(table)); Assert.False(await Rows(database, "idCounters").Find(new BsonDocument("_id", table)).AnyAsync()); }
        Assert.NotEmpty(before["stockSourceActivations"]["rows"].AsBsonArray); Assert.NotEmpty(before["procurementStockReceipts"]["rows"].AsBsonArray);
        Assert.NotEmpty(before["procurementQualityAcceptanceAllocations"]["rows"].AsBsonArray); Assert.NotEmpty(before["procurementStockValuationAllocations"]["rows"].AsBsonArray);
        Assert.NotEmpty(before["journalEntries"]["rows"].AsBsonArray); Assert.NotEmpty(before["evidencePins"]["rows"].AsBsonArray);
        await StoreTestData.ApplyRegisteredThroughAsync(database, clock, 103); Assert.True(await new MigrationGate(database, clock, 103).IsCurrentAsync(default));
        var after = await Snapshot(database); Assert.Equal(before.ElementCount + Future.Length, after.ElementCount);
        foreach (var table in before.Names) {
            if (table == "schemaMigrations") { Same(before[table]["definition"].AsBsonDocument, after[table]["definition"].AsBsonDocument, "history UUID/options"); Assert.Equal(before[table]["indexes"], after[table]["indexes"]); }
            else Same(before[table].AsBsonDocument, after[table].AsBsonDocument, "genuine102 retained " + table);
        }
        var applied = after["schemaMigrations"]["rows"].AsBsonArray; Assert.Equal(103, applied.Count); Assert.Equal(history, new BsonArray(applied.Take(102)));
        Assert.Equal("applied", applied[102]["state"]); await FutureEmpty(database);
        await StoreTestData.ApplyRegisteredThroughAsync(database, clock, 103); var replayed = await Snapshot(database);
        foreach (var table in after.Names) Same(after[table].AsBsonDocument, replayed[table].AsBsonDocument, "103 no-op replay " + table);

        // Exact byte preservation is proved before authorized recoveries/replays can
        // legitimately advance only their old actor/source coordination fences/counters.
        string[] protectedTables = ["procurementStockReceipts", "procurementQualityAcceptanceProposals", "procurementQualityAcceptanceDecisions", "procurementQualityAcceptanceAllocations",
            "procurementStockValuationProposals", "procurementStockValuationDecisions", "procurementStockValuationAllocations", "procurementInspectionSubmissions",
            "supplierDocumentSubmissions", "supplierDocumentMatches", "lots", "stockBalances", "movements", "journalEntries", "evidencePins", "evidencePinAudits"];
        var original = new Dictionary<string, BsonArray>(); foreach (var table in protectedTables) original[table] = await RowArray(database, table);
        var unit = new MongoUnitOfWork(fixture.Client); var ids = new IdAllocator(database); var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var requests = new ProcurementRequestService(database, unit, ids, clock, catalog); var supplier = new SupplierService(database, unit, ids, clock, catalog);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, requests, supplier); var sources = new StockSourceSetupService(database, unit, ids, clock);
        var receiving = new ProcurementReceivingService(database, unit, ids, clock, catalog, orders, requests);
        var receipts = new ProcurementStockReceiptService(database, unit, ids, clock, catalog, orders, receiving, sources);
        var quality = new ProcurementQualityAcceptanceService(database, unit, ids, clock, catalog, orders, receiving, sources);
        var valuation = new ProcurementStockValuationService(database, unit, ids, clock, catalog, orders, receiving, sources);
        ActorContext Actor(string field) => JsonSerializer.Deserialize<ActorContext>(ready.GetProperty("actors").GetProperty(field).GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Original synthetic actor descriptor is required");
        var receipt = await Rows(database, "procurementStockReceipts").Find(new BsonDocument("_id", long.Parse(Text(ready, "receiptId"), CultureInfo.InvariantCulture))).SingleAsync();
        var qualityDecision = await Rows(database, "procurementQualityAcceptanceDecisions").Find(new BsonDocument("_id", long.Parse(Text(ready, "qualityDecisionId"), CultureInfo.InvariantCulture))).SingleAsync();
        var valueDecision = await Rows(database, "procurementStockValuationDecisions").Find(new BsonDocument("_id", long.Parse(Text(ready, "valuationDecisionId"), CultureInfo.InvariantCulture))).SingleAsync();
        Assert.Equal(Text(ready, "receiptOriginalKey"), receipt["requestKey"]); Assert.Equal(Text(ready, "qualityOriginalKey"), qualityDecision["requestKey"]); Assert.Equal(Text(ready, "valuationOriginalKey"), valueDecision["requestKey"]);
        Assert.Equal(ProcurementStockReceiptBson.Result(receipt), await receipts.LookupAsync(Actor("receiptMaker"), ProcurementStockReceiptBson.Operation, receipt["requestKey"].AsString));
        Assert.Equal(ProcurementStockReceiptBson.Result(receipt), await receipts.ReceiveAsync(Actor("receiptMaker"), ProcurementStockReceiptBson.Input(receipt["input"]), receipt["binding"].AsString, Metadata(receipt)));
        var recoveredQuality = await quality.LookupAsync(Actor("qualityChecker"), ProcurementQualityAcceptanceBson.DecideOperation, qualityDecision["requestKey"].AsString);
        Assert.Equal("confirmed", recoveredQuality["state"]); Assert.Equal(ProcurementQualityAcceptanceBson.Result(qualityDecision), recoveredQuality["result"]);
        // These owning commands deliberately recover through lookup rather than
        // replay their approval. Keep their exact refusal and original-key result.
        Assert.Equal("procurement_quality_acceptance_lookup_required", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            quality.DecideAsync(Actor("qualityChecker"), qualityDecision["proposalId"].AsInt64, 1, ProcurementQualityAcceptanceDecision.Approve,
                ProcurementQualityAcceptanceBson.Observation(qualityDecision["observation"]), qualityDecision["binding"].AsString, Metadata(qualityDecision)))).Code);
        var qualityAfterRetry = await quality.LookupAsync(Actor("qualityChecker"), ProcurementQualityAcceptanceBson.DecideOperation, qualityDecision["requestKey"].AsString);
        Assert.Equal("confirmed", qualityAfterRetry["state"]); Assert.Equal(ProcurementQualityAcceptanceBson.Result(qualityDecision), qualityAfterRetry["result"]);
        Assert.Equal(ProcurementStockValuationBson.Result(valueDecision), await valuation.LookupAsync(Actor("valueChecker"), ProcurementStockValuationBson.DecideOperation, valueDecision["requestKey"].AsString));
        Assert.Equal("procurement_stock_valuation_lookup_required", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            valuation.DecideAsync(Actor("valueChecker"), valueDecision["proposalId"].AsInt64, 1, ProcurementStockValuationDecision.Approve,
                valueDecision["binding"].AsString, Metadata(valueDecision)))).Code);
        Assert.Equal(ProcurementStockValuationBson.Result(valueDecision), await valuation.LookupAsync(Actor("valueChecker"),
            ProcurementStockValuationBson.DecideOperation, valueDecision["requestKey"].AsString));
        foreach (var (table, rows) in original) Assert.Equal(rows, await RowArray(database, table));
        Assert.Equal(applied, await RowArray(database, "schemaMigrations")); await FutureEmpty(database);
    }
    private static AuthorizationCommandMetadata Metadata(BsonDocument row) => new(row["requestKey"].AsString, row["reason"].AsString, row["correlationId"].AsString);
    private static async Task FutureEmpty(IMongoDatabase database) {
        await StockReleaseSchemas.RequireStorageAsync(database, default); await LotValueStore.RequireStorageAsync(database, default);
        foreach (var table in Future) {
            Assert.Empty(await RowArray(database, table));
            var counter = await Rows(database, "idCounters").Find(new BsonDocument("_id", table)).SingleOrDefaultAsync();
            Assert.True(counter is null || counter == new BsonDocument { { "_id", table }, { "value", 0L } });
        }
    }
    private static IMongoCollection<BsonDocument> Rows(IMongoDatabase database, string table) => database.GetCollection<BsonDocument>(table);
    private static async Task<BsonArray> RowArray(IMongoDatabase database, string table) => new(await Rows(database, table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> Snapshot(IMongoDatabase database) {
        var result = new BsonDocument();
        foreach (var definition in (await (await database.ListCollectionsAsync()).ToListAsync()).Where(row => row.GetValue("type", "") == "collection").OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var table = definition["name"].AsString;
            result[table] = new BsonDocument { { "definition", definition }, { "indexes", new BsonArray((await (await Rows(database, table).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) }, { "rows", await RowArray(database, table) } };
        }
        return result;
    }
    private static string Text(JsonElement row, string field) => row.GetProperty(field).GetString() ?? throw new InvalidOperationException("Required capsule field missing");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void RequireChild(string owner, string path) => Assert.StartsWith(owner + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    private static void CheckFile(string owner, string path, string expected) { RequireChild(owner, path); Assert.Equal(expected, Hash(File.ReadAllBytes(path))); }
    private static void Same(BsonDocument expected, BsonDocument actual, string label) => Assert.True(expected.ToBson().SequenceEqual(actual.ToBson()), label + " changed");
    private static string FindRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend/UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Authoritative checkout is required");
    }
}
