using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests;

[Collection("Mongo")]
public sealed class OutboxProcessTests(MongoFixture fixture)
{
    [Fact]
    public async Task Killed_process_after_downstream_ack_is_replayed_without_a_second_effect()
    {
        var databaseName = "unittoko_test_" + Guid.NewGuid().ToString("N") + "_outboxprocess";
        var database = fixture.Client.GetDatabase(databaseName);
        var now = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var sourceKey = "order.confirmed:" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await new CollectionMigrations(database, new TestClock(now)).ApplyAsync(timeout.Token);
            var outbox = new OutboxStore(database, new TestClock(now));
            await new MongoUnitOfWork(fixture.Client).ExecuteAsync(async (session, token) =>
            {
                await outbox.EnqueueAsync(session, sourceKey, "order.confirmed",
                    new BsonDocument("orderId", new BsonInt64(9007199254740993L)), token);
                return true;
            }, timeout.Token);

            var events = database.GetCollection<BsonDocument>("outboxEvents");
            var receipts = database.GetCollection<BsonDocument>("probeDownstreamReceipts");
            await using (var first = StartProbe(databaseName, now, "crash-window"))
            {
                Assert.Equal("DOWNSTREAM_ACKNOWLEDGED", await first.ReadLineAsync());
                Assert.False(first.HasExited);
                await first.KillAsync();
                Assert.NotEqual(0, first.ExitCode);
            }

            // The OS process died between the downstream acknowledgement and our completion write.
            var interrupted = await events.Find(new BsonDocument("sourceKey", sourceKey))
                .SingleAsync(timeout.Token);
            Assert.Equal("leased", interrupted["status"].AsString);
            Assert.Equal(1, interrupted["attempts"].AsInt32);
            var firstReceipt = await receipts.Find(new BsonDocument("_id", sourceKey))
                .SingleAsync(timeout.Token);
            Assert.Equal(1, firstReceipt["effectCount"].AsInt32);
            Assert.Equal(1, firstReceipt["deliveryAttempts"].AsInt32);

            // A fresh process and clock past the old deadline exercise durable lease recovery.
            await using (var restarted = StartProbe(databaseName, now.AddSeconds(31), "complete"))
            {
                Assert.Equal("DOWNSTREAM_ACKNOWLEDGED", await restarted.ReadLineAsync());
                Assert.Equal("OUTBOX_COMPLETED", await restarted.ReadLineAsync());
                await restarted.WaitForExitAsync();
                Assert.True(restarted.ExitCode == 0, await restarted.ErrorsAsync());
            }

            var completed = await events.Find(new BsonDocument("sourceKey", sourceKey))
                .SingleAsync(timeout.Token);
            Assert.Equal("completed", completed["status"].AsString);
            Assert.Equal(2, completed["attempts"].AsInt32);
            Assert.False(completed.Contains("leaseToken"));
            Assert.Equal(1, await events.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                cancellationToken: timeout.Token));
            Assert.Equal(1, await receipts.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                cancellationToken: timeout.Token));
            var finalReceipt = await receipts.Find(new BsonDocument("_id", sourceKey))
                .SingleAsync(timeout.Token);
            Assert.Equal(1, finalReceipt["effectCount"].AsInt32);
            Assert.Equal(2, finalReceipt["deliveryAttempts"].AsInt32);
            Assert.Equal(9007199254740993L, finalReceipt["payload"]["orderId"].AsInt64);
            Assert.Null(await new OutboxStore(database, new TestClock(now.AddHours(1)))
                .ClaimAsync("after-restart", new OutboxClaimPolicy(["order.confirmed"], 3, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)), timeout.Token));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await fixture.Client.DropDatabaseAsync(databaseName, cleanup.Token);
        }
    }

    private static OwnedProbe StartProbe(string databaseName, DateTimeOffset now, string mode)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "PersistenceProbe", "UnitToko.PersistenceProbe.dll");
        if (!File.Exists(helper)) throw new FileNotFoundException("Build the test project to copy its process probe.", helper);
        var start = new ProcessStartInfo(FindDotnetHost())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(helper)!
        };
        start.ArgumentList.Add(helper);
        start.ArgumentList.Add(databaseName);
        start.ArgumentList.Add(now.ToString("O", CultureInfo.InvariantCulture));
        start.ArgumentList.Add(mode);
        return new OwnedProbe(Process.Start(start) ?? throw new InvalidOperationException("Could not start the test probe."));
    }

    internal static string FindDotnetHost()
    {
        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured) &&
            string.Equals(Path.GetFileName(configured), executableName, StringComparison.OrdinalIgnoreCase))
            return configured;
        var runtimeRoot = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent?.Parent?.Parent;
        var alongsideRuntime = Path.Combine(runtimeRoot?.FullName ?? "", executableName);
        if (File.Exists(alongsideRuntime)) return alongsideRuntime;
        throw new InvalidOperationException("Cannot locate the dotnet host beside the active test runtime.");
    }

    private sealed class OwnedProbe(Process process) : IAsyncDisposable
    {
        private readonly Task<string> errors = process.StandardError.ReadToEndAsync();
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;

        public async Task<string?> ReadLineAsync() =>
            await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));

        public Task WaitForExitAsync() => process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        public Task<string> ErrorsAsync() => errors.WaitAsync(TimeSpan.FromSeconds(5));

        public async Task KillAsync()
        {
            // Only this exact Process handle is ours; never discover or terminate by process name.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await WaitForExitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try { await KillAsync(); }
            finally { process.Dispose(); }
        }
    }

    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
