using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

/// <summary>Explicit native opt-in. The supplied database was produced by exact preserved94
/// factories and writers. This test never removes a migration suffix, repairs history or drops data.</summary>
[Collection("Mongo")]
public sealed class AfterSalesPopulatedPredecessorMigrationTests(MongoFixture fixture)
{
    internal const string HandoffEnvironment = "UNITTOKO_M95_PREDECESSOR_HANDOFF";
    [Populated94PredecessorFact]
    public async Task Genuine_stopped94_case_intake_inspection_survive95_apply_replay_and_original_inspection_recovery()
    {
        Assert.Equal(95, CollectionMigrations.CurrentVersion);
        var handoffPath = Environment.GetEnvironmentVariable(HandoffEnvironment)!;
        Assert.True(Path.IsPathFullyQualified(handoffPath));
        Assert.Equal("HANDOFF.json", Path.GetFileName(handoffPath));
        var directory = Path.GetDirectoryName(Path.GetFullPath(handoffPath))!;
        Assert.Equal("allocator-safe-bootstrap-release2", Path.GetFileName(directory));
        Assert.Equal("aftersales-genuine94-predecessor-writer-release1", Path.GetFileName(Path.GetDirectoryName(directory)));
        using var handoff = JsonDocument.Parse(await File.ReadAllTextAsync(handoffPath));
        using var stopped = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "PRODUCER-STOPPED.json")));
        var ready = handoff.RootElement; var terminal = stopped.RootElement;
        Assert.Equal("GENUINE94_POPULATED_RETURN_READY", Text(ready, "status"));
        Assert.Equal(94, ready.GetProperty("schema").GetInt32()); Assert.Equal(94, terminal.GetProperty("schema").GetInt32());
        Assert.True(ready.GetProperty("syntheticLocalOnly").GetBoolean());
        // READY is historical. The terminal observation is mandatory; an active READY process is insufficient.
        Assert.True(terminal.GetProperty("producerWritesStopped").GetBoolean());
        Assert.False(terminal.GetProperty("producerProcessRunning").GetBoolean());
        Assert.False(terminal.GetProperty("fixtureDisposed").GetBoolean());
        Assert.True(terminal.GetProperty("databaseRetained").GetBoolean());
        Assert.Equal(0, terminal.GetProperty("producerExitCode").GetInt32());
        var name = Text(ready, "databaseName"); Assert.Equal(name, Text(terminal, "databaseName"));
        Assert.Matches(@"\Aunittoko_test_[0-9a-f]{32}_[0-9a-f]{8}\z", name);
        var captured = DateTimeOffset.Parse(Text(ready, "businessClockUtc"), CultureInfo.InvariantCulture);
        var clockValue = DateTimeOffset.Parse(Text(terminal, "forwardMigrationClockUtc"), CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, clockValue.Offset); Assert.Equal(captured.ToUniversalTime(), clockValue);

        var snapshotPath = Path.GetFullPath(Text(ready, "retainedSnapshotPath"));
        Assert.Equal(Path.Combine(directory, "retained94-snapshot.json"), snapshotPath);
        var snapshotBytes = await File.ReadAllBytesAsync(snapshotPath); var snapshotHash = Hash(snapshotBytes);
        Assert.Equal(Text(ready, "retainedSnapshotSha256"), snapshotHash); Assert.Equal(Text(terminal, "retainedSnapshotSha256"), snapshotHash);
        Assert.Equal(Text(ready, "executedProgramSha256"), Text(terminal, "executedProgramSha256"));
        Assert.Equal(Text(ready, "executedHostSha256"), Text(terminal, "executedHostSha256"));
        Assert.Equal("SYNTHETIC_NOT_REAL_RECEIVING_OR_RELEASE", Text(ready, "initialStockAdmission"));
        var fixtureSource = Path.GetFullPath(Text(ready, "fixtureSource"));
        Assert.Equal(Path.Combine(directory, "source", "backend", "tests", "UnitToko.Tests", "Ordering", "OrderSubmissionTestData.cs"), fixtureSource);
        Assert.Equal("8BB4F425B9EE9F81E44BE5BFE7C07B8773F20F8B84B88DF2BFC71DAB1423F782", Text(ready, "fixtureSha256"));
        Assert.Equal(Text(ready, "fixtureSha256"), Text(terminal, "fixtureSha256"));
        Assert.Equal(Text(ready, "fixtureSha256"), Hash(await File.ReadAllBytesAsync(fixtureSource)));
        using var retained = JsonDocument.Parse(snapshotBytes);
        Assert.Equal(name, Text(retained.RootElement, "databaseName"));
        Assert.Equal(94, retained.RootElement.GetProperty("schema").GetInt32());
        // Binding and executed artifact hashes attest which writer produced this exact namespace, not current95 binaries.
        var exact94Product = new Dictionary<string, string> {
            ["UnitToko.Api"] = "F6D84C9AF91E1E17F6FCFBE1425CB1BF0563BDB92AF2518AB77C7DE323834D68",
            ["UnitToko.Platform"] = "66F9B8D4241F4BD3F8651430EA887416A9593B0E2E3697888E33140AD5AB85E2",
            ["UnitToko.Modules"] = "3DEC30E6288D64D7DFD28276472FD4D6F60E6745325764098B8AEC728343A011" };
        foreach (var (product, sha256) in exact94Product)
            Assert.Equal(sha256, ready.GetProperty("runtimeBinaries").EnumerateArray().Single(row => Text(row, "name") == product).GetProperty("sha256").GetString());
        Assert.Equal(Text(ready, "preservedBindingSha256"), Hash(await File.ReadAllBytesAsync(Path.Combine(directory, "compiled94-binding.json"))));
        foreach (var binary in ready.GetProperty("runtimeBinaries").EnumerateArray())
            Assert.Equal(Text(binary, "sha256"), Hash(await File.ReadAllBytesAsync(Text(binary, "path"))));
        Assert.Equal(Text(ready, "executedProgramSha256"), Hash(await File.ReadAllBytesAsync(Path.Combine(directory, "Program.cs"))));
        Assert.Equal(Text(ready, "executedHostSha256"), Hash(await File.ReadAllBytesAsync(Path.Combine(directory, "bin/Release/net10.0", "Predecessor94.dll"))));
        var database = fixture.Client.GetDatabase(name); var clock = new MutableAuthorizationClock(clockValue);
        var before = await SnapshotAsync(database);
        Assert.True(retained.RootElement.GetProperty("collections").GetArrayLength() == before.Count, "Retained collection membership changed before95");
        foreach (var expected in retained.RootElement.GetProperty("collections").EnumerateArray()) {
            var table = Text(expected, "name"); Assert.True(before.TryGetValue(table, out var actual), "Missing retained collection " + table);
            AssertState(expected, actual!, includeRows: true);
        }
        await RequireFutureAbsentAsync(database);
        var predecessor = await HistoryAsync(database); Assert.Equal(94, predecessor.Count);
        Assert.True(MigrationHistory.IsValid(predecessor, 94)); Assert.All(predecessor, row => Assert.Equal("applied", row["state"].AsString));
        var caseId = long.Parse(Text(ready, "caseId"), CultureInfo.InvariantCulture);
        var returnId = long.Parse(Text(ready, "returnId"), CultureInfo.InvariantCulture);
        var originalKey = Text(ready, "inspectionOriginalKey");
        var inspection = await Rows(database, ReturnInspectionRecords.Collection).Find(new BsonDocument("_id", returnId)).SingleAsync();
        var receipt = await Rows(database, ReturnQuarantineLedger.Collection).Find(new BsonDocument("_id", returnId)).SingleAsync();
        Assert.Equal(originalKey, inspection["requestKey"].AsString);
        Assert.Equal("unverified", inspection["lotIdentity"].AsString); Assert.Equal("unverified", receipt["lotIdentity"].AsString);
        var actor = await OriginalActorAsync(database, inspection, clockValue);
        var migrations = new CollectionMigrations(database, clock); Assert.False(await migrations.IsCurrentAsync());
        await migrations.ApplyAsync(); Assert.True(await migrations.IsCurrentAsync());
        var applied = await SnapshotAsync(database);
        foreach (var (table, expected) in before) {
            Assert.True(applied.TryGetValue(table, out var actual), "Retained collection disappeared " + table);
            Assert.Equal(expected.Definition, actual!.Definition); Assert.Equal(expected.Indexes, actual.Indexes);
            if (table != "schemaMigrations") Assert.Equal(expected.Rows, actual.Rows);
        }
        Assert.Equal(before.Count + FutureNames.Length, applied.Count);
        await RequireFutureEmptyAsync(database);
        var history = await HistoryAsync(database); Assert.Equal(95, history.Count);
        Assert.Equal(HistoryHash(predecessor), HistoryHash(history.Take(94)));
        Assert.True(MigrationHistory.IsValid(history, 95)); Assert.All(history, row => Assert.Equal("applied", row["state"].AsString));
        await migrations.ApplyAsync(); Assert.Equal(HistoryHash(history), HistoryHash(await HistoryAsync(database)));
        var replayed = await SnapshotAsync(database); Assert.Equal(applied.Count, replayed.Count);
        foreach (var (table, expected) in applied) Assert.Equal(expected, replayed[table]);
        // Read/recovery legitimately append read audits and authority fences AFTER byte-exact migration/replay checks.
        var protectedTables = new[] { AfterSalesCaseRecords.Collection, AfterSalesDecisionRecords.Collection, ReturnQuarantineLedger.Collection,
            ReturnInspectionRecords.Collection, ReturnResolutionRecords.Collection, "stockBalances", "lots", "movements", "reservations", "journalEntries", InvoiceBson.Invoices, InvoiceBson.Allocations };
        var immutable = new Dictionary<string, string>(); foreach (var table in protectedTables) immutable[table] = await RowsHashAsync(database, table, excludeReadFences: true);
        var unit = new MongoUnitOfWork(fixture.Client); var ids = new IdAllocator(database);
        var orders = new OrderSubmissionService(database, unit, ids, clock);
        var preparations = new ShipmentPreparationService(database, unit, ids, clock, orders);
        var shipments = new ShipmentQueryService(database, unit, clock, orders, preparations);
        var documents = new ShipmentDocumentService(database, unit, ids, clock, shipments);
        var receipts = new ShipmentReceiptService(database, unit, ids, clock, shipments, documents);
        var invoices = new InvoiceService(database, unit, ids, clock, receipts);
        var cases = new AfterSalesCaseService(database, unit, ids, clock, invoices);
        var recovered = await cases.RecoverInspectionAsync(actor, caseId, returnId, originalKey);
        Assert.NotNull(recovered); Assert.Equal(Hash(ReturnInspectionRecords.Result(inspection).ToBson()), Hash(recovered.ToBson()));
        var view = await cases.ReadInspectionAsync(actor, caseId, returnId);
        Assert.Equal(returnId, view.Receipt.Id.Value); Assert.Equal(1L, view.Revision); Assert.NotNull(view.Inspection);
        Assert.Equal(inspection["input"]["publicNote"].AsString, view.Inspection!.PublicNote);
        foreach (var (table, expected) in immutable) Assert.Equal(expected, await RowsHashAsync(database, table, excludeReadFences: true));
        Assert.Equal(HistoryHash(history), HistoryHash(await HistoryAsync(database)));
        await RequireFutureEmptyAsync(database); // No95 attribution, new finding, lot verification or backfill from replay/read.
    }

    private static string[] FutureNames => [.. AfterSalesStockSourceSchemas.Collections, ReturnedLotVerificationRecords.Collection];
    private static async Task RequireFutureAbsentAsync(IMongoDatabase database) {
        var definitions = await (await database.ListCollectionsAsync()).ToListAsync();
        Assert.DoesNotContain(definitions, row => FutureNames.Contains(row["name"].AsString));
        Assert.False(await Rows(database, "idCounters").Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(FutureNames)))).AnyAsync());
    }
    private static async Task RequireFutureEmptyAsync(IMongoDatabase database) {
        await AfterSalesStockSourceStorage.RequireAsync(database, default);
        foreach (var table in FutureNames) {
            Assert.Equal(0L, await Rows(database, table).CountDocumentsAsync(new BsonDocument()));
            var counter = await Rows(database, "idCounters").Find(new BsonDocument("_id", table)).SingleOrDefaultAsync();
            if (counter is not null) Assert.True(counter == new BsonDocument { { "_id", table }, { "value", 0L } }, "Prospective95 counter was used");
        }
    }
    private static async Task<ActorContext> OriginalActorAsync(IMongoDatabase database, BsonDocument inspection, DateTimeOffset frozenClock) {
        var user = inspection["actorId"].AsInt64; var grant = inspection["actorGrantVersion"].AsInt64;
        var identity = await Rows(database, "identities").Find(new BsonDocument("_id", user)).SingleAsync();
        var session = await Rows(database, "identitySessions").Find(new BsonDocument { { "userId", user }, { "status", "active" }, { "grantVersion", grant } }).SingleAsync();
        Assert.Equal(identity["issuer"], session["issuer"]); Assert.Equal(identity["subject"], session["subject"]);
        Assert.Equal(identity["securityVersion"], session["securityVersion"]);
        Assert.Equal("otp", session["factorType"].AsString); Assert.Equal("fixture-policy", session["assurancePolicyVersion"].AsString);
        var completed = new DateTimeOffset(session["factorCompletedAt"].ToUniversalTime(), TimeSpan.Zero);
        Assert.True(completed <= frozenClock && frozenClock - completed < TimeSpan.FromMinutes(5));
        Assert.True(session["idleExpiresAt"].ToUniversalTime() > frozenClock.UtcDateTime && session["absoluteExpiresAt"].ToUniversalTime() > frozenClock.UtcDateTime);
        // Native producer-hashed descriptor only: retain the real recorded factor time/reference;
        // never mint fresh assurance, decode provider payloads or claim a browser/provider sign-in.
        return new(user, identity["issuer"].AsString, identity["subject"].AsString, grant, completed, FactorAssurance.MultiFactor,
            new(session["_id"].AsString, session["version"].AsInt64, session["securityVersion"].AsInt64));
    }
    private sealed record State(string Definition, string Indexes, string Rows, int Count);
    private static async Task<Dictionary<string, State>> SnapshotAsync(IMongoDatabase database) {
        var result = new Dictionary<string, State>(StringComparer.Ordinal); var definitions = await (await database.ListCollectionsAsync()).ToListAsync();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection").OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; var rows = Rows(database, name);
            var indexes = (await (await rows.Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal);
            var documents = await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            result[name] = new(Hash(definition.ToBson()), Hash(new BsonDocument("indexes", new BsonArray(indexes)).ToBson()),
                Hash(new BsonDocument("rows", new BsonArray(documents)).ToBson()), documents.Count);
        }
        return result;
    }
    private static void AssertState(JsonElement expected, State actual, bool includeRows) {
        Assert.Equal(Text(expected, "definitionSha256"), actual.Definition); Assert.Equal(Text(expected, "indexesSha256"), actual.Indexes);
        if (includeRows) { Assert.Equal(Text(expected, "rowsSha256"), actual.Rows); Assert.Equal(expected.GetProperty("rowCount").GetInt32(), actual.Count); }
    }
    private static IMongoCollection<BsonDocument> Rows(IMongoDatabase database, string name) => database.GetCollection<BsonDocument>(name);
    private static async Task<List<BsonDocument>> HistoryAsync(IMongoDatabase database) => await Rows(database, "schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
    private static async Task<string> RowsHashAsync(IMongoDatabase database, string table, bool excludeReadFences) {
        var rows = await Rows(database, table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        // These existing read paths touch authority/ownership fences. Migration/replay comparisons
        // above include EVERY byte; only this later business-field read comparison excludes fences.
        if (excludeReadFences) foreach (var row in rows) { row.Remove("fence"); row.Remove("postingFence"); }
        return Hash(new BsonDocument("rows", new BsonArray(rows)).ToBson());
    }
    private static string HistoryHash(IEnumerable<BsonDocument> rows) => Hash(new BsonDocument("rows", new BsonArray(rows)).ToBson());
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidOperationException("Missing handoff field");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

public sealed class Populated94PredecessorFactAttribute : FactAttribute
{
    public Populated94PredecessorFactAttribute() {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AfterSalesPopulatedPredecessorMigrationTests.HandoffEnvironment)))
            Skip = "Requires explicit genuine stopped94 predecessor handoff; populated94 migration coverage has not executed.";
    }
}
