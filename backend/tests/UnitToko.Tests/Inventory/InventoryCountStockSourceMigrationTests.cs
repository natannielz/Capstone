using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

/// <summary>Genuine retired95 API-produced counts only. Required capsule facts fail when their
/// stopped producer proof is absent; no96 data/history is erased to fabricate a95 predecessor.</summary>
[Collection("Mongo")]
public sealed class InventoryCountStockSourceMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Fresh96_registration_creates_empty_strict_owners_without_stock_money_or_association_seeding()
    {
        var database = fixture.Client.GetDatabase(fixture.DatabaseName + "_" + Guid.NewGuid().ToString("N")[..8]);
        try {
            Assert.Equal(97, CollectionMigrations.CurrentVersion);
            using var migrations = PreservedMigrationRuntime.Create(database, new SystemClock().UtcNow, 96);
            await migrations.ApplyAsync(); Assert.True(await migrations.IsCurrentAsync());
            await RequireEmptyNewOwnersAsync(database); await InventoryCountStockSourceMigration.RequireCountStorageAsync(database, default);
            foreach (var name in InventoryCountStockSourceSchemas.Collections) {
                var definition = await AccountContactStorage.DefinitionAsync(database, name, default); Assert.NotNull(definition);
                Assert.Equal(InventoryCountStockSourceSchemas.Validator(name), definition["options"]["validator"]);
                await Assert.ThrowsAsync<MongoWriteException>(() => Rows(database, name).InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } }));
            }
            foreach (var name in new[] { "inventoryPools", StockCountBson.Collection, "stockBalances", "lots", "movements", "journalEntries", "cashMovements" })
                Assert.Equal(0L, await Rows(database, name).CountDocumentsAsync(new BsonDocument()));
            var before = await SnapshotAsync(database); var history = await HistoryAsync(database);
            await migrations.ApplyAsync(); Same(before, await SnapshotAsync(database), "fresh replay state"); SameHistory(history, await HistoryAsync(database), "fresh replay history");
        } finally { await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName); }
    }

    [Fact]
    public async Task Genuine_stopped95_API_counts_survive_forward_migration_original_key_recovery_and_replay_byte_exact()
    {
        var capsule = await CapsuleAsync("UNITTOKO_INVENTORY_COUNT_PREDECESSOR95");
        var database = capsule.Database; var clock = new MutableAuthorizationClock(capsule.Clock);
        var before = await SnapshotAsync(database); var history = await HistoryAsync(database);
        var rows = new List<BsonDocument>();
        foreach (var id in capsule.Ids) {
            var row = await Rows(database, StockCountBson.Collection).Find(new BsonDocument("_id", id)).SingleAsync();
            Assert.Equal(1, row["schemaVersion"].AsInt32); Assert.False(row.Contains("poolBinding")); Assert.False(row.Contains("poolSourceHash"));
            StockCountBson.Graph(row, await Rows(database, "auditEvents").Find(new BsonDocument("_id", row["auditId"])).SingleAsync(),
                await Rows(database, "idempotencyRecords").Find(new BsonDocument { { "actorId", row["actorId"] },
                    { "operation", StockCountBson.Operation }, { "key", row["requestKey"] } }).SingleAsync());
            rows.Add(row);
        }
        Assert.Contains(rows, row => row["packages"] == 0); Assert.Contains(rows, row => row["packages"] == 2);
        using var migrations = PreservedMigrationRuntime.Create(database, capsule.Clock, 96);
        Assert.False(await migrations.IsCurrentAsync()); await migrations.ApplyAsync(); Assert.True(await migrations.IsCurrentAsync());
        var applied = await HistoryAsync(database); Assert.Equal(96, applied.Count);
        SameHistory(history, new BsonArray(applied.Take(95)), "genuine95 prefix"); Assert.Equal("applied", applied[95]["state"].AsString);
        var after = await SnapshotAsync(database); Assert.Equal(before.ElementCount + InventoryCountStockSourceSchemas.Collections.Length, after.ElementCount);
        foreach (var name in before.Names) {
            var expected = before[name].DeepClone().AsBsonDocument;
            if (name is "stockCountObservations" or "idempotencyRecords") expected["definition"]["options"]["validator"] =
                InventoryCountStockSourceSchemas.ExtendCount(name, expected["definition"]["options"]["validator"].AsBsonDocument);
            Same(expected, after[name].AsBsonDocument, name + " retained metadata/documents");
        }
        await RequireEmptyNewOwnersAsync(database);
        await migrations.ApplyAsync(); Same(after, await SnapshotAsync(database), "forward migration replay state"); SameHistory(applied, await HistoryAsync(database), "forward migration replay history");
        migrations.Dispose();
        var current = new CollectionMigrations(database, clock); Assert.False(await current.IsCurrentAsync());
        await current.ApplyAsync(); Assert.True(await current.IsCurrentAsync());
        var applied97 = await HistoryAsync(database); Assert.Equal(97, applied97.Count);
        SameHistory(applied, new BsonArray(applied97.Take(96)), "genuine96 prefix"); Assert.Equal("applied", applied97[96]["state"].AsString);
        var after97 = await SnapshotAsync(database); Assert.Equal(after.ElementCount + PackConversionRuleSchemas.Collections.Length, after97.ElementCount);
        foreach (var name in after.Names) Same(after[name].AsBsonDocument, after97[name].AsBsonDocument, name + " exact retained97 metadata/documents");
        await RequireEmptyRuleOwnersAsync(database);
        await current.ApplyAsync(); Same(after97, await SnapshotAsync(database), "97 replay state"); SameHistory(applied97, await HistoryAsync(database), "97 replay history");
        var service = new StockCountService(database, new(fixture.Client), new(database), clock);
        foreach (var row in rows) {
            var saved = StockCountBson.Read(row); var key = row["requestKey"].AsString;
            Assert.Equal(capsule.Actor.UserId, saved.CountedBy); Assert.Equal(saved, await service.RecoverAsync(capsule.Actor, key));
            var input = new StockCountInput(saved.Location, saved.Definition.SkuId, saved.Definition.SkuRevision,
                saved.Definition.DefinitionId, saved.Packages, saved.ObservedLotLabel);
            Assert.Equal(saved, await service.RecordCountAsync(capsule.Actor, input,
                new(key, row["reason"].AsString, row["correlationId"].AsString)));
            Same(row, await Rows(database, StockCountBson.Collection).Find(new BsonDocument("_id", row["_id"])).SingleAsync(), "original legacy count");
            // The genuine retired Counter was approved for stock.count only. Its original
            // key recovery remains available; migration must not grant history permission.
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(capsule.Actor, saved.Id));
        }
        var afterReplay = await SnapshotAsync(database);
        foreach (var name in new[] { StockCountBson.Collection, "auditEvents", "idempotencyRecords" })
            Same(new BsonDocument("rows", after[name]["rows"].DeepClone()), new BsonDocument("rows", afterReplay[name]["rows"].DeepClone()), name + " original command history");
        await RequireEmptyNewOwnersAsync(database);
        await RequireEmptyRuleOwnersAsync(database);
        // Existing ordinary replay can allocate gaps and fence identity. No source association,
        // usable stock, financial effect or external provider acceptance is claimed.
        foreach (var name in new[] { "inventoryPools", "stockBalances", "lots", "movements", "reservations", "journalEntries", "cashMovements" }) {
            if (before.TryGetValue(name, out var original)) Same(original.AsBsonDocument, afterReplay[name].AsBsonDocument, name + " physical/financial owner");
            else Assert.False(afterReplay.Contains(name), name + " must not be seeded");
        }
        // The genuine capsule is retained; this fact never disposes its retired fixture/database.
    }

    [Fact]
    public async Task Separate_genuine95_custom_count_audit_orphan_refuses_before_target_DDL_and_preserves_failed_evidence()
    {
        var capsule = await CapsuleAsync("UNITTOKO_INVENTORY_COUNT_PREDECESSOR95_NEGATIVE");
        var positivePath = Environment.GetEnvironmentVariable("UNITTOKO_INVENTORY_COUNT_PREDECESSOR95");
        if (!string.IsNullOrEmpty(positivePath)) {
            using var positive = JsonDocument.Parse(await File.ReadAllTextAsync(positivePath));
            Assert.NotEqual(positive.RootElement.GetProperty("databaseName").GetString(), capsule.Database.DatabaseNamespace.DatabaseName);
        }
        var database = capsule.Database; var clock = new MutableAuthorizationClock(capsule.Clock);
        var row = await Rows(database, StockCountBson.Collection).Find(new BsonDocument("_id", capsule.Ids[0])).SingleAsync();
        var originalAudit = await Rows(database, "auditEvents").Find(new BsonDocument("_id", row["auditId"])).SingleAsync();
        Assert.Equal(1L, (await Rows(database, "auditEvents").DeleteOneAsync(originalAudit)).DeletedCount);
        Assert.True(await Rows(database, StockCountBson.Collection).Find(new BsonDocument("_id", row["_id"])).AnyAsync());
        Assert.True(await Rows(database, "idempotencyRecords").Find(new BsonDocument("stockCount.observationId", row["_id"])).AnyAsync());
        // Only a genuine audit was removed. Both old allocators remain present and above
        // the retained maxima, so an unrelated broken counter cannot mask this graph test.
        foreach (var name in new[] { StockCountBson.Collection, "auditEvents" }) {
            var maximum = await Rows(database, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstAsync();
            var counter = await Rows(database, "idCounters").Find(new BsonDocument("_id", name)).SingleAsync();
            Assert.True(counter["value"].AsInt64 >= maximum["_id"].AsInt64);
        }
        var before = await SnapshotAsync(database); var history = await HistoryAsync(database); var ddl = new ConcurrentQueue<string>();
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "Count96RetainedOrphan-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command => {
            if (command.DatabaseNamespace.DatabaseName == database.DatabaseNamespace.DatabaseName &&
                command.CommandName is "create" or "collMod" or "createIndexes" or "dropIndexes" or "drop") ddl.Enqueue(command.CommandName);
        });
        using var observedClient = new MongoClient(settings); var observed = observedClient.GetDatabase(database.DatabaseNamespace.DatabaseName);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(observed, clock).ApplyAsync());
        Assert.Equal("Stock-count migration found incompatible record graph; review records before retrying", failure.Message);
        Assert.Empty(ddl); Same(before, await SnapshotAsync(database), "refused orphan retained state");
        var failed = await HistoryAsync(database); Assert.Equal(96, failed.Count); Assert.Equal("applying", failed[95]["state"].AsString);
        SameHistory(history, new BsonArray(failed.Take(95)), "refused orphan retained95 prefix");
        foreach (var name in InventoryCountStockSourceSchemas.Collections) Assert.Null(await AccountContactStorage.DefinitionAsync(database, name, default));
        Assert.False(await new CollectionMigrations(database, clock).IsCurrentAsync());
        // Keep the deliberate synthetic orphan, original remaining receipt/count and failed
        // migration marker/database as evidence. No cleanup disguises the refusal as acceptance.
    }

    private sealed record Capsule(IMongoDatabase Database, DateTimeOffset Clock, long[] Ids, ActorContext Actor);
    private async Task<Capsule> CapsuleAsync(string variable)
    {
        Assert.Equal(97, CollectionMigrations.CurrentVersion);
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Verified stopped genuine95 count capsule is required; no capsule acceptance is available");
        var root = FindRoot(); var allowed = Path.GetFullPath(Path.Combine(root, "tmp", "production", "inventory-count-genuine95-predecessor-writer-release1")) + Path.DirectorySeparatorChar;
        path = Path.GetFullPath(path); Assert.True(path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase), "capsule must belong to the isolated predecessor writer");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)); var value = document.RootElement;
        Assert.Equal("GENUINE95_POPULATED_COUNT_READY", value.GetProperty("status").GetString()); Assert.Equal(95, value.GetProperty("schema").GetInt32());
        Assert.Equal("ABSENT_NOT_REMOVED", value.GetProperty("future96OwnersOrCounters").GetString());
        Assert.True(value.GetProperty("syntheticLocalOnly").GetBoolean()); Assert.False(value.GetProperty("productionAcceptance").GetBoolean());
        var name = value.GetProperty("databaseName").GetString() ?? throw new InvalidOperationException(); Assert.Matches(@"\Aunittoko_test_[0-9a-f]{32}_[0-9a-f]{8}\z", name);
        var clock = DateTimeOffset.Parse(value.GetProperty("businessClockUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.None);
        Assert.Equal(TimeSpan.Zero, clock.Offset); Assert.NotEqual(default, clock);
        using var stoppedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "PRODUCER-STOPPED.json")));
        var stopped = stoppedDocument.RootElement; Assert.Equal(95, stopped.GetProperty("schema").GetInt32()); Assert.Equal(name, stopped.GetProperty("databaseName").GetString());
        Assert.Equal(0, stopped.GetProperty("producerExitCode").GetInt32()); Assert.True(stopped.GetProperty("producerWritesStopped").GetBoolean());
        Assert.False(stopped.GetProperty("fixtureDisposed").GetBoolean()); Assert.True(stopped.GetProperty("databaseRetained").GetBoolean());
        Assert.Equal(value.GetProperty("businessClockUtc").GetString(), stopped.GetProperty("businessClockUtc").GetString());
        foreach (var field in new[] { "retainedSnapshotSha256", "executedProgramSha256", "executedHostSha256" })
            Assert.Equal(value.GetProperty(field).GetString(), stopped.GetProperty(field).GetString());
        var writer = Path.GetFullPath(Path.Combine(root, "tmp", "production", "inventory-count-genuine95-predecessor-writer-release1"));
        var program = Path.Combine(writer, "Program.cs"); var host = Path.Combine(writer, "bin", "Release", "net10.0", "Predecessor95.dll");
        VerifyFile(root, program, value.GetProperty("executedProgramSha256").GetString()!);
        VerifyFile(root, host, value.GetProperty("executedHostSha256").GetString()!);
        using var observationDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "PRODUCER-EXTERNAL-OBSERVATION.json")));
        var observed = observationDocument.RootElement;
        Assert.Equal(95, observed.GetProperty("schema").GetInt32()); Assert.Equal(name, observed.GetProperty("databaseName").GetString());
        var pid = stopped.GetProperty("processId").GetInt32(); Assert.True(pid > 0, "original producer PID must be positive");
        Assert.Equal(pid, observed.GetProperty("processId").GetInt32());
        Assert.Equal(0, observed.GetProperty("observedProducerExitCode").GetInt32()); Assert.True(observed.GetProperty("processStopped").GetBoolean());
        Assert.Equal("ABSENT_NOT_REMOVED", observed.GetProperty("future96OwnersOrCounters").GetString());
        Assert.True(observed.GetProperty("syntheticLocalOnly").GetBoolean()); Assert.False(observed.GetProperty("productionAcceptance").GetBoolean());
        Assert.True(string.Equals(program, Path.GetFullPath(observed.GetProperty("executedProgramPath").GetString()!), StringComparison.OrdinalIgnoreCase), "observed program path must be the fixed producer source");
        Assert.True(string.Equals(host, Path.GetFullPath(observed.GetProperty("executedHostPath").GetString()!), StringComparison.OrdinalIgnoreCase), "observed host path must be the fixed producer binary");
        VerifyFile(root, path, observed.GetProperty("handoffSha256").GetString()!);
        foreach (var field in new[] { "executedProgramSha256", "executedHostSha256", "retainedSnapshotSha256" })
            Assert.Equal(value.GetProperty(field).GetString(), observed.GetProperty(field).GetString());
        var observedAt = DateTimeOffset.Parse(observed.GetProperty("observedAtUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.None);
        var stoppedAt = DateTimeOffset.Parse(stopped.GetProperty("stoppedAtUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.None);
        Assert.Equal(TimeSpan.Zero, observedAt.Offset); Assert.True(observedAt >= stoppedAt && observedAt <= DateTimeOffset.UtcNow, "external observation chronology must match the completed producer");
        Assert.True(ProducerPidIsAbsent(pid), "the exact original producer PID must be absent");
        foreach (var file in value.GetProperty("runtimeBinaries").EnumerateArray()) VerifyFile(root, file.GetProperty("path").GetString()!, file.GetProperty("sha256").GetString()!);
        VerifyFile(root, value.GetProperty("preservedBindingPath").GetString()!, value.GetProperty("preservedBindingSha256").GetString()!);
        var snapshotPath = value.GetProperty("retainedSnapshotPath").GetString()!; VerifyFile(root, snapshotPath, value.GetProperty("retainedSnapshotSha256").GetString()!);
        var database = fixture.Client.GetDatabase(name); var history = await HistoryAsync(database);
        Assert.Equal(95, history.Count); Assert.True(MigrationHistory.IsValid(history.Select(row => row.AsBsonDocument).ToArray(), 95));
        Assert.All(history, row => Assert.Equal("applied", row["state"].AsString));
        using var snapshotDocument = JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath));
        var captured = snapshotDocument.RootElement; Assert.Equal(name, captured.GetProperty("databaseName").GetString()); Assert.Equal(95, captured.GetProperty("schema").GetInt32());
        var definitions = await (await database.ListCollectionsAsync()).ToListAsync(); var expected = captured.GetProperty("collections").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, definitions.Count(row => row.GetValue("type", "") == "collection"));
        foreach (var item in expected) {
            var collection = item.GetProperty("name").GetString()!; var definition = definitions.Single(row => row.GetValue("name", "") == collection);
            var indexes = (await (await Rows(database, collection).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal);
            var rows = await Rows(database, collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            Assert.True(Hash(definition.ToBson()) == item.GetProperty("definitionSha256").GetString(), collection + " original definition changed");
            Assert.True(Hash(new BsonDocument("indexes", new BsonArray(indexes)).ToBson()) == item.GetProperty("indexesSha256").GetString(), collection + " original indexes changed");
            Assert.True(Hash(new BsonDocument("rows", new BsonArray(rows)).ToBson()) == item.GetProperty("rowsSha256").GetString(), collection + " original rows changed");
            Assert.Equal(item.GetProperty("rowCount").GetInt64(), (long)rows.Count);
        }
        foreach (var collection in InventoryCountStockSourceSchemas.Collections) {
            Assert.Null(await AccountContactStorage.DefinitionAsync(database, collection, default));
            Assert.False(await Rows(database, "idCounters").Find(new BsonDocument("_id", collection)).AnyAsync());
        }
        var ids = value.GetProperty("observationIds").EnumerateArray().Select(item => long.Parse(item.GetString()!, CultureInfo.InvariantCulture)).ToArray();
        Assert.Collection(ids, id => Assert.True(id > 0), id => Assert.True(id > 0)); Assert.NotEqual(ids[0], ids[1]);
        Assert.Equal(2L, await Rows(database, StockCountBson.Collection).CountDocumentsAsync(new BsonDocument()));
        var keys = value.GetProperty("originalKeys").EnumerateArray().Select(item => item.GetString()).ToArray(); Assert.Equal(ids.Length, keys.Length);
        for (var index = 0; index < ids.Length; index++) Assert.Equal(keys[index], (await Rows(database, StockCountBson.Collection).Find(new BsonDocument("_id", ids[index])).SingleAsync())["requestKey"].AsString);
        var actor = JsonSerializer.Deserialize<ActorContext>(value.GetProperty("actor").GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException(); actor.Validate();
        return new(database, clock, ids, actor);
    }
    private static void VerifyFile(string root, string path, string hash)
    {
        var allowed = Path.GetFullPath(Path.Combine(root, "tmp", "production")) + Path.DirectorySeparatorChar; path = Path.GetFullPath(path);
        Assert.True(path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase), "capsule proof file must stay in isolated production artifacts");
        Assert.True(Hash(File.ReadAllBytes(path)) == hash, "capsule proof file hash changed");
    }
    private static bool ProducerPidIsAbsent(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return false; }
        catch (ArgumentException) { return true; }
    }
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Authoritative checkout is required");
    }
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static IMongoCollection<BsonDocument> Rows(IMongoDatabase database, string name) => database.GetCollection<BsonDocument>(name);
    private static async Task RequireEmptyNewOwnersAsync(IMongoDatabase database)
    {
        foreach (var name in InventoryCountStockSourceSchemas.Collections) {
            Assert.Equal(0L, await Rows(database, name).CountDocumentsAsync(new BsonDocument()));
            Assert.False(await Rows(database, "idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
    }
    private static async Task RequireEmptyRuleOwnersAsync(IMongoDatabase database)
    {
        await PackConversionRuleStorage.RequireAsync(database, default);
        foreach (var name in PackConversionRuleSchemas.Collections) {
            Assert.Equal(0L, await Rows(database, name).CountDocumentsAsync(new BsonDocument()));
            Assert.False(await Rows(database, "idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
    }
    private static void Same(BsonDocument expected, BsonDocument actual, string field) => Assert.True(expected.ToBson().SequenceEqual(actual.ToBson()), field + " changed");
    private static void SameHistory(BsonArray expected, BsonArray actual, string field) => Same(new("history", expected), new("history", actual), field);
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await Rows(database, "schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase database)
    {
        var result = new BsonDocument(); var definitions = await (await database.ListCollectionsAsync()).ToListAsync();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection").OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; var indexes = (await (await Rows(database, name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal);
            var state = new BsonDocument { { "definition", definition.DeepClone() }, { "indexes", new BsonArray(indexes) } };
            if (name != "schemaMigrations") state.Add("rows", new BsonArray(await Rows(database, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            result.Add(name, state);
        }
        return result;
    }
}
