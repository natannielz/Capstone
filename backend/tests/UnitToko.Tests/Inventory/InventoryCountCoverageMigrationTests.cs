using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

/// <summary>Required stopped genuine97 predecessor proof. Empty suffix rehearsals
/// cannot satisfy this test and no future owner is deleted to manufacture a predecessor.</summary>
[Collection("Mongo")]
public sealed class InventoryCountCoverageMigrationTests(MongoFixture fixture)
{
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Genuine_stopped97_original_counts_upgrade_to101_with_exact_predecessor_storage_history_and_custom_recovery()
    {
        var path = Environment.GetEnvironmentVariable("UNITTOKO_INVENTORY_COUNT_COVERAGE_PREDECESSOR97");
        if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Stopped genuine97 populated count capsule is required; no genuine97 migration acceptance is available");
        path = Path.GetFullPath(path); var allowed = Path.GetFullPath(Path.Combine(FindRoot(), "tmp", "production")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(allowed, path, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)); var capsule = document.RootElement;
        Assert.Equal("GENUINE97_POPULATED_COUNT_READY", capsule.GetProperty("status").GetString()); Assert.Equal(97, capsule.GetProperty("schema").GetInt32());
        Assert.Equal("ABSENT_NOT_REMOVED", capsule.GetProperty("futureOwnersOrCounters").GetString());
        Assert.True(capsule.GetProperty("syntheticLocalOnly").GetBoolean()); Assert.False(capsule.GetProperty("productionAcceptance").GetBoolean());
        var name = capsule.GetProperty("databaseName").GetString()!; Assert.Matches(@"\Aunittoko_test_[0-9a-f]{32}_[0-9a-f]{8}\z", name);
        var clock = new MutableAuthorizationClock(DateTimeOffset.Parse(capsule.GetProperty("businessClockUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.None));
        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
        VerifyFile(allowed, capsule.GetProperty("executedProgramPath").GetString()!, capsule.GetProperty("executedProgramSha256").GetString()!);
        VerifyFile(allowed, capsule.GetProperty("executedHostPath").GetString()!, capsule.GetProperty("executedHostSha256").GetString()!);
        VerifyFile(allowed, capsule.GetProperty("retainedSnapshotPath").GetString()!, capsule.GetProperty("retainedSnapshotSha256").GetString()!);
        using var stoppedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "PRODUCER-STOPPED.json")));
        var stopped = stoppedDocument.RootElement;
        Assert.Equal(97, stopped.GetProperty("schema").GetInt32()); Assert.Equal(name, stopped.GetProperty("databaseName").GetString());
        Assert.Equal(0, stopped.GetProperty("producerExitCode").GetInt32()); Assert.True(stopped.GetProperty("producerWritesStopped").GetBoolean());
        Assert.True(stopped.GetProperty("databaseRetained").GetBoolean()); Assert.False(stopped.GetProperty("fixtureDisposed").GetBoolean());
        var processId = stopped.GetProperty("processId").GetInt32(); Assert.True(processId > 0);
        try { using var process = Process.GetProcessById(processId); Assert.Fail("The exact predecessor producer is still running"); }
        catch (ArgumentException) { }
        using var observedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "PRODUCER-EXTERNAL-OBSERVATION.json")));
        var observed = observedDocument.RootElement;
        Assert.Equal(97, observed.GetProperty("schema").GetInt32()); Assert.Equal(name, observed.GetProperty("databaseName").GetString());
        Assert.Equal(processId, observed.GetProperty("processId").GetInt32()); Assert.True(observed.GetProperty("processStopped").GetBoolean());
        Assert.Equal(0, observed.GetProperty("observedProducerExitCode").GetInt32());
        Assert.Equal("ABSENT_NOT_REMOVED", observed.GetProperty("futureOwnersOrCounters").GetString());
        VerifyFile(allowed, path, observed.GetProperty("handoffSha256").GetString()!);
        foreach (var field in new[] { "executedProgramSha256", "executedHostSha256", "retainedSnapshotSha256" }) {
            Assert.Equal(capsule.GetProperty(field).GetString(), stopped.GetProperty(field).GetString());
            Assert.Equal(capsule.GetProperty(field).GetString(), observed.GetProperty(field).GetString());
        }
        var database = fixture.Client.GetDatabase(name); var before = await SnapshotAsync(database); var originalHistory = await HistoryAsync(database);
        Assert.Equal(97, originalHistory.Length); Assert.True(MigrationHistory.IsValid(originalHistory, 97));
        Assert.All(originalHistory, row => Assert.Equal("applied", row["state"].AsString));
        using var snapshotDocument = JsonDocument.Parse(await File.ReadAllTextAsync(capsule.GetProperty("retainedSnapshotPath").GetString()!));
        var captured = snapshotDocument.RootElement; Assert.Equal(97, captured.GetProperty("schema").GetInt32()); Assert.Equal(name, captured.GetProperty("databaseName").GetString());
        var capturedCollections = captured.GetProperty("collections").EnumerateArray().Where(value => value.GetProperty("name").GetString() != "schemaMigrations").ToArray();
        Assert.Equal(before.ElementCount, capturedCollections.Length);
        foreach (var collection in capturedCollections) {
            var item = before[collection.GetProperty("name").GetString()!].AsBsonDocument;
            foreach (var (part, hashField) in new[] { ("definition", "definitionSha256"), ("indexes", "indexesSha256"), ("rows", "rowsSha256") }) {
                var content = part == "definition" ? item[part].AsBsonDocument : new BsonDocument(part, item[part]);
                Assert.Equal(collection.GetProperty(hashField).GetString()!.ToLowerInvariant(), Convert.ToHexString(SHA256.HashData(content.ToBson())).ToLowerInvariant());
            }
        }
        foreach (var target in new[] { StockSourceTopologyFence.Collection }.Concat(AfterSalesCausalCoverageSchemas.Collections)
            .Concat(ProcurementStockSourceCoverageSchemas.Collections).Concat(InventoryCountCoverageSchemas.Collections)) {
            Assert.False(before.Contains(target)); Assert.False(await Rows(database, "idCounters").Find(new BsonDocument("_id", target)).AnyAsync());
        }
        var originalCounts = await Rows(database, StockCountBson.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.NotEmpty(originalCounts); Assert.Contains(originalCounts, row => row["schemaVersion"] == 1);
        var actor = JsonSerializer.Deserialize<ActorContext>(capsule.GetProperty("actor").GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Original count actor proof is required"); actor.Validate();
        var migrations = new CollectionMigrations(database, clock); await migrations.ApplyAsync(); Assert.True(await migrations.IsCurrentAsync());
        var after = await SnapshotAsync(database); var applied = await HistoryAsync(database);
        Assert.True(applied.Length >= 101); Assert.True(originalHistory.SequenceEqual(applied.Take(97)));
        foreach (var collection in before.Names.Where(name => name != "idCounters"))
            Same(before[collection].AsBsonDocument, after[collection].AsBsonDocument, collection + " genuine97 bytes");
        var oldCounters = before["idCounters"].AsBsonDocument; var newCounters = after["idCounters"].AsBsonDocument;
        Assert.Equal(oldCounters["definition"], newCounters["definition"]); Assert.Equal(oldCounters["indexes"], newCounters["indexes"]);
        var retainedCounters = oldCounters["rows"].AsBsonArray.Select(row => row.AsBsonDocument).ToArray();
        var currentCounters = newCounters["rows"].AsBsonArray.Select(row => row.AsBsonDocument).ToArray();
        foreach (var counter in retainedCounters) Same(counter, Assert.Single(currentCounters, row => row["_id"] == counter["_id"]), "original counter bytes");
        // M99 adds genuine setup transitions for retained pools using its NEW allocator.
        // Every old counter remains byte-exact; no other new counter is permitted here.
        var addedCounter = Assert.Single(currentCounters, row => retainedCounters.All(old => old["_id"] != row["_id"]));
        Assert.Equal(AfterSalesStockSourceRecords.CausalTransitions, addedCounter["_id"].AsString);
        var latestCausal = await Rows(database, AfterSalesStockSourceRecords.CausalTransitions).Find(new BsonDocument())
            .Sort(new BsonDocument("_id", -1)).FirstAsync();
        Assert.Equal(latestCausal["_id"], addedCounter["value"]);
        Assert.Equal(originalCounts.Count, await Rows(database, InventoryCountCoverage.Members).CountDocumentsAsync(new BsonDocument()));
        var service = new StockCountService(database, new(fixture.Client), new(database), clock);
        foreach (var row in originalCounts) {
            Assert.Equal(InventoryCountCoverage.Member(row, "retained"), await Rows(database, InventoryCountCoverage.Members).Find(new BsonDocument("_id", row["_id"])).SingleAsync());
            if (row["actorId"] != actor.UserId) continue;
            var original = StockCountBson.Read(row); var key = row["requestKey"].AsString;
            Assert.Equal(original, await service.RecoverAsync(actor, key));
            if (original.PoolBinding is null) Assert.Equal(original, await service.RecordCountAsync(actor,
                new(original.Location, original.Definition.SkuId, original.Definition.SkuRevision, original.Definition.DefinitionId, original.Packages, original.ObservedLotLabel),
                new(key, row["reason"].AsString, row["correlationId"].AsString)));
            Same(row, await Rows(database, StockCountBson.Collection).Find(new BsonDocument("_id", row["_id"])).SingleAsync(), "original custom count");
        }
        var beforeReplay = await SnapshotAsync(database); var replayHistory = await HistoryAsync(database);
        await migrations.ApplyAsync(); var replay = await SnapshotAsync(database);
        foreach (var collection in beforeReplay.Names) Same(beforeReplay[collection].AsBsonDocument, replay[collection].AsBsonDocument, collection + " replay bytes");
        var replayedHistory = await HistoryAsync(database);
        Assert.True(replayHistory.SequenceEqual(replayedHistory));
    }
    private static void VerifyFile(string allowed, string path, string expected)
    {
        path = Path.GetFullPath(path); Assert.StartsWith(allowed, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected.ToLowerInvariant(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
    }
    private static IMongoCollection<BsonDocument> Rows(IMongoDatabase database, string name) => database.GetCollection<BsonDocument>(name);
    private static async Task<BsonDocument[]> HistoryAsync(IMongoDatabase database) => (await Rows(database, "schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).ToArray();
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase database)
    {
        var result = new BsonDocument();
        foreach (var definition in await (await database.ListCollectionsAsync()).ToListAsync()) {
            var name = definition["name"].AsString; if (definition.GetValue("type", "") != "collection" || name == "schemaMigrations") continue;
            result[name] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray((await (await Rows(database, name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) },
                { "rows", new BsonArray(await Rows(database, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
    private static void Same(BsonDocument expected, BsonDocument actual, string label) => Assert.True(expected.ToBson().SequenceEqual(actual.ToBson()), label + " changed");
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Authoritative checkout is required");
    }
}
