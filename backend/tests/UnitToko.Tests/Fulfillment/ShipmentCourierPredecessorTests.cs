using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Fulfillment;

/// <summary>Required genuine stopped117 input, following the existing stopped-predecessor
/// convention. This never fabricates source rows, removes a migration suffix or seeds118.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ShipmentCourierPredecessorTests(MongoFixture fixture)
{
    internal const string HandoffEnvironment = "UNITTOKO_COURIER_PREDECESSOR117";
    // Intentionally unconfigured: exact117 has not been built/accepted yet. The release
    // coordinator must pin the actually executed117 API/Platform/Modules/Tests and producer
    // host before this acceptance fact can run. An empty map is a hard failure, never a skip.
    private static readonly Dictionary<string, string> Accepted117ProductPins = new(StringComparer.Ordinal);

    [WindowsStorageFact(Skip = Retired.HistoricalUpgrade)]
    public async Task Genuine_stopped117_open_visit_pending_QR_and_received_shipment_survive118_with_exact_historical_bindings() {
        if (Accepted117ProductPins.Count != 5)
            throw new InvalidOperationException("Exact accepted117 product and producer hashes are not pinned; populated117 courier migration acceptance is unavailable");
        var path = Environment.GetEnvironmentVariable(HandoffEnvironment);
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("A genuinely produced, stopped and retained117 shipment capsule is required");
        var owner = Path.GetFullPath(Path.Combine(Root(), "tmp", "production", "courier-genuine117-predecessor"));
        path = Path.GetFullPath(path); Child(owner, path); Assert.Equal("HANDOFF.json", Path.GetFileName(path));
        var directory = Path.GetDirectoryName(path)!; Assert.Equal(owner, Path.GetDirectoryName(directory));
        Assert.Matches(@"\Acapsule-[a-z0-9-]{1,48}\z", Path.GetFileName(directory));
        using var handoffDocument = JsonDocument.Parse(await File.ReadAllTextAsync(path)); var ready = handoffDocument.RootElement;
        using var terminalDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "PRODUCER-STOPPED.json"))); var terminal = terminalDocument.RootElement;
        using var observedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "PRODUCER-EXTERNAL-OBSERVATION.json"))); var observed = observedDocument.RootElement;
        Assert.Equal("GENUINE117_COURIER_HISTORY_READY", Text(ready, "status"));
        Assert.True(ready.GetProperty("syntheticLocalOnly").GetBoolean()); Assert.False(ready.GetProperty("productionAcceptance").GetBoolean());
        var name = Text(ready, "databaseName"); Assert.Matches(@"\Aunittoko_test_[0-9a-f]{32}_[0-9a-f]{8}\z", name);
        foreach (var value in new[] { ready, terminal, observed }) {
            Assert.Equal(119, value.GetProperty("schema").GetInt32()); Assert.Equal(name, Text(value, "databaseName"));
            Assert.Equal("ABSENT_NOT_REMOVED", Text(value, "futureOwnersOrCounters"));
        }
        Assert.True(terminal.GetProperty("producerWritesStopped").GetBoolean()); Assert.True(terminal.GetProperty("databaseRetained").GetBoolean());
        Assert.False(terminal.GetProperty("fixtureDisposed").GetBoolean()); Assert.Equal(0, terminal.GetProperty("producerExitCode").GetInt32());
        Assert.True(observed.GetProperty("processStopped").GetBoolean()); Assert.Equal(0, observed.GetProperty("observedProducerExitCode").GetInt32());
        var pid = terminal.GetProperty("processId").GetInt32(); Assert.True(pid > 0); Assert.Equal(pid, observed.GetProperty("processId").GetInt32());
        try { using var process = Process.GetProcessById(pid); Assert.Fail("The recorded117 producer process is still running"); } catch (ArgumentException) { }
        CheckFile(owner, path, Text(observed, "handoffSha256"));
        foreach (var binary in ready.GetProperty("runtimeBinaries").EnumerateArray()) {
            Assert.Equal(Accepted117ProductPins[Text(binary, "name")], Text(binary, "sha256")); CheckFile(owner, Text(binary, "path"), Text(binary, "sha256"));
        }
        Assert.Equal(Accepted117ProductPins.Count, ready.GetProperty("runtimeBinaries").GetArrayLength());
        foreach (var (file, hash) in new[] { ("executedProgramPath", "executedProgramSha256"), ("retainedSnapshotPath", "retainedSnapshotSha256"), ("sourceManifestPath", "sourceManifestSha256") }) {
            CheckFile(owner, Text(ready, file), Text(ready, hash)); Assert.Equal(Text(ready, hash), Text(terminal, hash));
        }
        var database = fixture.Client.GetDatabase(name);
        var clock = new MutableAuthorizationClock(DateTimeOffset.Parse(Text(ready, "businessClockUtc"), CultureInfo.InvariantCulture));
        Assert.Equal(clock.UtcNow, DateTimeOffset.Parse(Text(terminal, "forwardMigrationClockUtc"), CultureInfo.InvariantCulture));
        var before = await ShipmentCourierMigrationTests.SnapshotAsync(database);
        using var retainedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Text(ready, "retainedSnapshotPath")));
        var retained = retainedDocument.RootElement; Assert.Equal(name, Text(retained, "databaseName")); Assert.Equal(119, retained.GetProperty("schema").GetInt32());
        Assert.Equal(before.ElementCount, retained.GetProperty("collections").GetArrayLength());
        foreach (var collection in retained.GetProperty("collections").EnumerateArray()) {
            var collectionName = Text(collection, "name"); Assert.True(before.Contains(collectionName));
            var bytes = Convert.FromBase64String(Text(collection, "stateBsonBase64")); Assert.Equal(Text(collection, "sha256"), Hash(bytes));
            Assert.Equal(BsonSerializer.Deserialize<BsonDocument>(bytes), before[collectionName]);
        }
        var historyRows = database.GetCollection<BsonDocument>("schemaMigrations");
        var history = await historyRows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(119, history.Count); Assert.True(MigrationHistory.IsValid(history, 119)); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        foreach (var collectionName in ShipmentCourierSchemas.Collections) {
            Assert.False(before.Contains(collectionName));
            Assert.False(await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", collectionName)).AnyAsync());
        }
        foreach (var collectionName in new[] { "shipments", "issuedDocuments", DeliveryAttemptRecords.Attempts, ShipmentReceiptRecords.Challenges, ShipmentReceiptRecords.Receipts })
            Assert.NotEmpty(before[collectionName]["rows"].AsBsonArray);
        await StoreTestData.ApplyRegisteredThroughAsync(database, clock, 120);
        Assert.True(await new MigrationGate(database, clock, 120).IsCurrentAsync(default));
        var after = await ShipmentCourierMigrationTests.SnapshotAsync(database);
        foreach (var collectionName in before.Names) Assert.Equal(before[collectionName], after[collectionName]);
        Assert.Equal(before.ElementCount + ShipmentCourierSchemas.Collections.Length, after.ElementCount);
        var applied = await historyRows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(new BsonArray(history), new BsonArray(applied.Take(119))); Assert.Equal(120, applied.Count);
        await StoreTestData.ApplyRegisteredThroughAsync(database, clock, 120); Assert.Equal(after, await ShipmentCourierMigrationTests.SnapshotAsync(database));
        var unit = new MongoUnitOfWork(fixture.Client); var orders = new OrderSubmissionService(database, unit, new(database), clock);
        var preparation = new ShipmentPreparationService(database, unit, new(database), clock, orders);
        var queries = new ShipmentQueryService(database, unit, clock, orders, preparation);
        var documents = new ShipmentDocumentService(database, unit, new(database), clock, queries);
        var receipts = new ShipmentReceiptService(database, unit, new(database), clock, queries, documents);
        var attempts = new DeliveryAttemptService(database, unit, new(database), clock, queries, receipts);
        var courier = JsonSerializer.Deserialize<ActorContext>(ready.GetProperty("courierActor").GetRawText()) ?? throw new InvalidOperationException();
        var openId = long.Parse(Text(ready, "openShipmentId"), CultureInfo.InvariantCulture);
        var receivedId = long.Parse(Text(ready, "receivedShipmentId"), CultureInfo.InvariantCulture); Assert.NotEqual(openId, receivedId);
        foreach (var id in new[] { openId, receivedId }) {
            var shipment = await queries.ReadAsync(courier, id);
            Assert.Equal(1, shipment.CurrentAssignment.Revision); Assert.Equal(shipment.CourierId, shipment.CurrentAssignment.CourierId);
        }
        Assert.NotEmpty((await attempts.ReadAsync(courier, openId)).Items); Assert.Null(await receipts.ReadAsync(courier, openId));
        Assert.NotNull(await receipts.ReadAsync(courier, receivedId));
        Assert.NotNull(await attempts.RecoverAsync(courier, Text(ready, "attemptOriginalKey")));
        Assert.NotNull(await receipts.RecoverChallengeAsync(courier, Text(ready, "pendingChallengeOriginalKey")));
        Assert.Empty(await database.GetCollection<BsonDocument>(ShipmentCourierRecords.Events).Find(new BsonDocument()).ToListAsync());
        var bindings = await database.GetCollection<BsonDocument>(ShipmentCourierRecords.Bindings).Find(new BsonDocument()).ToListAsync();
        Assert.NotEmpty(bindings); Assert.All(bindings, binding => { Assert.Equal(1L, binding["assignmentRevision"].AsInt64); Assert.True(binding["auditId"].IsBsonNull); });
        // Retained capsule is never dropped/disposed by this consumer; it remains available
        // for independent migration/source review and recovery evidence.
    }
    private static string Text(JsonElement value, string field) => value.GetProperty(field).GetString() ?? throw new InvalidOperationException("Missing " + field);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Child(string owner, string path) => Assert.StartsWith(owner + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    private static void CheckFile(string owner, string path, string hash) { Child(owner, path); Assert.Equal(hash, Hash(File.ReadAllBytes(path))); }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Authoritative checkout is required");
    }
}
