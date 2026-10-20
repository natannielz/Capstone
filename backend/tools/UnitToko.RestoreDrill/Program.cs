using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Operations;

// Q09 restore tooling. "capture" and "verify" work against any environment (run capture before the backup, verify after
// the restore). "drill" is the timed end-to-end rehearsal on a synthetic database in a throwaway local mongod.
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
try
{
    var command = args.FirstOrDefault();
    var options = Options(args.Skip(1).ToArray());
    return command switch
    {
        "capture" => await CaptureAsync(options),
        "verify" => await VerifyAsync(options),
        "drill" => await DrillAsync(options),
        _ => Usage(),
    };
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or MongoException or TimeoutException)
{
    Console.Error.WriteLine("restore drill: " + exception.Message);
    return 2;
}

int Usage()
{
    Console.Error.WriteLine("""
        usage:
          capture --mongo-uri <uri> --database <name> [--evidence-root <dir>] --out <manifest.json>
          verify  --mongo-uri <uri> --database <name> [--evidence-root <dir>] --manifest <manifest.json>
          drill   --mongod <mongod.exe> --work <empty dir> [--port 27091] [--documents 20000] [--evidence-objects 200]
        """);
    return 2;
}

async Task<int> CaptureAsync(Dictionary<string, string> options)
{
    var manifest = await RestoreVerification.CaptureAsync(Database(options), Optional(options, "evidence-root"), DateTimeOffset.UtcNow, default);
    await File.WriteAllTextAsync(Required(options, "out"), JsonSerializer.Serialize(manifest, json));
    Console.WriteLine($"captured {manifest.Collections.Count} collections, {manifest.Evidence.Count} evidence objects, schema {manifest.SchemaVersion}");
    return 0;
}

async Task<int> VerifyAsync(Dictionary<string, string> options)
{
    var expected = JsonSerializer.Deserialize<RestoreManifest>(await File.ReadAllTextAsync(Required(options, "manifest")), json)
        ?? throw new InvalidOperationException("The manifest is empty.");
    var actual = await RestoreVerification.CaptureAsync(Database(options), Optional(options, "evidence-root"), DateTimeOffset.UtcNow, default);
    return Report(RestoreVerification.Compare(expected, actual));
}

int Report(IReadOnlyList<string> findings)
{
    foreach (var finding in findings) Console.WriteLine("FAIL " + finding);
    Console.WriteLine(findings.Count == 0 ? "restore verified: every collection and evidence object matches" : $"restore rejected: {findings.Count} difference(s)");
    return findings.Count == 0 ? 0 : 1;
}

async Task<int> DrillAsync(Dictionary<string, string> options)
{
    var mongod = Path.GetFullPath(Required(options, "mongod"));
    if (!File.Exists(mongod)) throw new ArgumentException("Supply the existing mongod executable; the drill never downloads software.");
    var work = Path.GetFullPath(Required(options, "work"));
    if (Directory.Exists(work) && Directory.EnumerateFileSystemEntries(work).Any()) throw new ArgumentException("The work directory must be new or empty.");
    var port = int.Parse(Optional(options, "port") ?? "27091", System.Globalization.CultureInfo.InvariantCulture);
    var documents = int.Parse(Optional(options, "documents") ?? "20000", System.Globalization.CultureInfo.InvariantCulture);
    var objects = int.Parse(Optional(options, "evidence-objects") ?? "200", System.Globalization.CultureInfo.InvariantCulture);
    if (port is < 1024 or > 65535 || port == 27028) throw new ArgumentException("Use a free local port other than the development database's 27028.");
    if (documents is < 1 or > 1_000_000 || objects is < 1 or > 10_000) throw new ArgumentException("Synthetic volume is out of range.");
    EnsurePortFree(port);
    const string databaseName = "unittoko_restore_drill", replicaSet = "restoredrill";
    string source = Path.Combine(work, "source"), backup = Path.Combine(work, "backup"), restore = Path.Combine(work, "restore");
    var timings = new Dictionary<string, long>();
    var clock = Stopwatch.StartNew();
    long Lap(string name) { var elapsed = clock.ElapsedMilliseconds; timings[name] = elapsed; clock.Restart(); return elapsed; }

    // 1. Source: a throwaway replica set holding the full current schema plus synthetic rows and evidence objects.
    Directory.CreateDirectory(Path.Combine(source, "db")); Directory.CreateDirectory(Path.Combine(source, "evidence"));
    RestoreManifest expected;
    using (var node = await LocalNode.StartAsync(mongod, Path.Combine(source, "db"), Path.Combine(work, "source-mongod.log"), port, replicaSet, initiate: true))
    {
        var database = node.Client.GetDatabase(databaseName);
        await new CollectionMigrations(database, new SystemClock()).ApplyAsync();
        Lap("schemaMigrationMs");
        await SeedAsync(database, Path.Combine(source, "evidence"), documents, objects);
        Lap("syntheticSeedMs");
        expected = await RestoreVerification.CaptureAsync(database, Path.Combine(source, "evidence"), DateTimeOffset.UtcNow, default);
        await File.WriteAllTextAsync(Path.Combine(work, "manifest.json"), JsonSerializer.Serialize(expected, json));
        Lap("captureMs");
        await node.StopAsync();
    }
    // 2. Backup: a clean shutdown, then a copy of the data files and the evidence root.
    clock.Restart();
    var bytes = CopyTree(source, backup);
    Lap("backupMs");
    // 3. Restore into a fresh location and start a node on it.
    CopyTree(backup, restore);
    Lap("restoreCopyMs");
    IReadOnlyList<string> findings, control;
    using (var node = await LocalNode.StartAsync(mongod, Path.Combine(restore, "db"), Path.Combine(work, "restore-mongod.log"), port, replicaSet, initiate: false))
    {
        Lap("restoreStartupMs");
        var database = node.Client.GetDatabase(databaseName);
        findings = RestoreVerification.Compare(expected, await RestoreVerification.CaptureAsync(database, Path.Combine(restore, "evidence"), DateTimeOffset.UtcNow, default));
        Lap("verifyMs");
        // Negative control: remove one evidence object from the restored copy; verification must now refuse it.
        var victim = Directory.EnumerateFiles(Path.Combine(restore, "evidence"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
        File.Delete(victim);
        control = RestoreVerification.Compare(expected, await RestoreVerification.CaptureAsync(database, Path.Combine(restore, "evidence"), DateTimeOffset.UtcNow, default));
        await node.StopAsync();
    }
    var passed = findings.Count == 0 && control.Count == 1 && control[0].StartsWith("evidence object missing: ", StringComparison.Ordinal);
    var report = new {
        result = passed ? "passed" : "failed", ranAt = DateTimeOffset.UtcNow, mongod = Path.GetFileName(mongod), schemaVersion = expected.SchemaVersion,
        collections = expected.Collections.Count, syntheticDocuments = documents, evidenceObjects = expected.Evidence.Count, backupBytes = bytes,
        timingsMs = timings, recoveryMs = timings["restoreCopyMs"] + timings["restoreStartupMs"] + timings["verifyMs"],
        findings, negativeControl = control,
    };
    await File.WriteAllTextAsync(Path.Combine(work, "drill-report.json"), JsonSerializer.Serialize(report, json));
    Console.WriteLine(JsonSerializer.Serialize(report, json));
    return passed ? 0 : 1;
}

static async Task SeedAsync(IMongoDatabase database, string evidenceRoot, int documents, int objects)
{
    // Synthetic only: random bytes and generated numbers, never real names, amounts or files.
    var rows = database.GetCollection<BsonDocument>("restoreDrillSynthetic");
    for (var start = 0; start < documents; start += 1000)
        await rows.InsertManyAsync(Enumerable.Range(start, Math.Min(1000, documents - start)).Select(index => new BsonDocument {
            { "_id", (long)index + 1 }, { "reference", "DRILL-" + (index + 1).ToString("D7", System.Globalization.CultureInfo.InvariantCulture) },
            { "quantity", index % 97 }, { "amount", new BsonDecimal128(index * 1.25m) }, { "payload", new BsonBinaryData(RandomNumberGenerator.GetBytes(768)) },
        }));
    for (var index = 0; index < objects; index++)
    {
        var folder = Path.Combine(evidenceRoot, "objects", (index % 16).ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, $"drill-{index:D5}.bin"), RandomNumberGenerator.GetBytes(16_384 + index * 397 % 81_920));
    }
}

static long CopyTree(string from, string to)
{
    long bytes = 0;
    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(to, Path.GetRelativePath(from, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: false); bytes += new FileInfo(file).Length;
    }
    return bytes;
}

static void EnsurePortFree(int port)
{
    try { var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); listener.Stop(); }
    catch (SocketException) { throw new InvalidOperationException($"Port {port} is in use; choose another with --port."); }
}

IMongoDatabase Database(Dictionary<string, string> options)
{
    var name = Required(options, "database");
    if (name is "admin" or "local" or "config") throw new ArgumentException("Verify an application database, not a system database.");
    return new MongoClient(Required(options, "mongo-uri")).GetDatabase(name);
}

static string Required(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ArgumentException("--" + name + " is required.");
static string? Optional(Dictionary<string, string> options, string name) => options.GetValueOrDefault(name);
static Dictionary<string, string> Options(string[] values)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= values.Length) throw new ArgumentException("Options are --name value pairs.");
        options[values[index][2..]] = values[index + 1];
    }
    return options;
}

/// <summary>A throwaway single-node replica set on 127.0.0.1, stopped cleanly so its data files can be copied.</summary>
sealed class LocalNode : IDisposable
{
    private readonly Process process;
    public MongoClient Client { get; }
    private LocalNode(Process process, MongoClient client) { this.process = process; Client = client; }

    public static async Task<LocalNode> StartAsync(string mongod, string dbPath, string logPath, int port, string replicaSet, bool initiate)
    {
        var start = new ProcessStartInfo(mongod) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--bind_ip", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--replSet", replicaSet,
                     "--dbpath", dbPath, "--logpath", logPath, "--logappend", "--wiredTigerCacheSizeGB", "0.25" }) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start mongod.");
        try
        {
            var direct = new MongoClient($"mongodb://127.0.0.1:{port}/?directConnection=true&serverSelectionTimeoutMS=1000");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException("mongod stopped during startup; see " + logPath);
                try { await direct.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: deadline.Token); break; }
                catch (TimeoutException) { await Task.Delay(250, deadline.Token); }
            }
            if (initiate)
                await direct.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("replSetInitiate", new BsonDocument { { "_id", replicaSet },
                    { "members", new BsonArray { new BsonDocument { { "_id", 0 }, { "host", "127.0.0.1:" + port } } } } }), cancellationToken: deadline.Token);
            var client = new MongoClient($"mongodb://127.0.0.1:{port}/?replicaSet={replicaSet}&serverSelectionTimeoutMS=2000");
            while (true)
            {
                try
                {
                    var hello = await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: deadline.Token);
                    if (hello.GetValue("isWritablePrimary", false).ToBoolean()) return new LocalNode(process, client);
                }
                catch (TimeoutException) { }
                await Task.Delay(250, deadline.Token);
            }
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); throw; }
    }

    public async Task StopAsync()
    {
        try { await Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument { { "shutdown", 1 }, { "timeoutSecs", 10 } }); }
        catch (Exception exception) when (exception is MongoException or IOException) { } // the node closes the connection as it stops
        if (!process.WaitForExit(TimeSpan.FromSeconds(60))) throw new InvalidOperationException("mongod did not stop cleanly; its files are not a valid backup.");
        if (process.ExitCode != 0) throw new InvalidOperationException("mongod stopped with exit code " + process.ExitCode + "; its files are not a valid backup.");
    }

    public void Dispose()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        process.Dispose();
    }
}
