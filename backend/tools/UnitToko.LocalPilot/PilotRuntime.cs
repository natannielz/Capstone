using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;

namespace UnitToko.LocalPilot;

[SupportedOSPlatform("windows")]
internal sealed class PilotApiFactory(string repository, PilotState state, SyntheticOidcProvider provider,
    IReadOnlyDictionary<string, string?>? evidence = null) : WebApplicationFactory<global::Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        // The private photo store and image rescan settings, when the pilot started its local scanner (full profile).
        if (evidence is not null) builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(evidence));
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:Uri"] = PilotBoundary.MongoUri, ["Mongo:Database"] = PilotBoundary.DatabaseName,
            ["Release:Profile"] = PilotBoundary.Profile, ["Identity:Auth0:Enabled"] = "true", ["Identity:Auth0:Issuer"] = PilotBoundary.Issuer,
            ["Identity:Auth0:PublicOrigin"] = PilotBoundary.Origin, ["Identity:Auth0:ClientId"] = SyntheticOidcProvider.ClientId,
            ["Identity:Auth0:ClientSecret"] = state.Secrets.ClientSecret, ["Identity:Auth0:ConnectionId"] = SyntheticOidcProvider.ConnectionId,
            ["Identity:Auth0:ConnectionName"] = SyntheticOidcProvider.ConnectionName, ["Identity:Auth0:ClaimsNamespace"] = SyntheticOidcProvider.ClaimsNamespace,
            ["Identity:Keys:Directory"] = state.KeyDirectory, ["Identity:Keys:ApplicationName"] = PilotBoundary.Profile == "full" ? "UnitToko.SYNTHETIC.LocalPilot.Full" : "UnitToko.SYNTHETIC.LocalPilot.Release1",
            ["Identity:Keys:CertificatePath"] = state.CertificatePath, ["Identity:Keys:CertificatePassword"] = state.Secrets.CertificatePassword,
            ["Identity:TrustedProxies:0"] = "127.0.0.1", ["Logging:LogLevel:Default"] = "Warning"
        }));
        return base.CreateHost(builder);
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production").UseContentRoot(Path.Combine(repository, "backend", "src", "UnitToko.Api"));
        builder.ConfigureTestServices(services => services.Configure<OpenIdConnectOptions>(Auth0Options.Scheme,
            options => options.Backchannel = new HttpClient(provider, disposeHandler: false)));
    }
    public string Address => Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
}

[SupportedOSPlatform("windows")]
internal static class PilotRuntime
{
    public static async Task<MongoClient> MongoAsync(string executable, string stateDirectory, CancellationToken ct)
    {
        var dataPath = Path.Combine(stateDirectory, "mongo-data");
        PilotBoundary.ProtectDirectory(dataPath);
        var direct = new MongoClient($"mongodb://127.0.0.1:{PilotBoundary.MongoPort}/?directConnection=true&serverSelectionTimeoutMS=1000&connectTimeoutMS=1000");
        var admin = direct.GetDatabase("admin");
        try { await admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct); }
        catch (TimeoutException)
        {
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new InvalidOperationException("Supply the existing inspected mongod.exe path; this launcher never downloads software.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "--bind_ip", "127.0.0.1", "--port", PilotBoundary.MongoPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "--replSet", PilotBoundary.ReplicaSet,
                "--dbpath", dataPath, "--logpath", Path.Combine(stateDirectory, "mongo.log"), "--logappend", "--wiredTigerCacheSizeGB", "0.25" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start local MongoDB.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException("Local MongoDB stopped during startup; inspect the private mongo.log.");
                try { await admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: deadline.Token); break; }
                catch (TimeoutException) { await Task.Delay(250, deadline.Token); }
            }
        }
        var options = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("getCmdLineOpts", 1), cancellationToken: ct);
        var parsed = options["parsed"].AsBsonDocument;
        if (parsed.GetValue("net", new BsonDocument()).AsBsonDocument.GetValue("bindIp", "") != "127.0.0.1" ||
            parsed.GetValue("replication", new BsonDocument()).AsBsonDocument.GetValue("replSet", "") != PilotBoundary.ReplicaSet ||
            !string.Equals(Path.GetFullPath(parsed["storage"]["dbPath"].AsString), Path.GetFullPath(dataPath), StringComparison.OrdinalIgnoreCase) ||
            parsed.GetValue("setParameter", new BsonDocument()).AsBsonDocument.Contains("enableTestCommands"))
            throw new InvalidOperationException("Port " + PilotBoundary.MongoPort + " is not the isolated pilot Mongo process. No bootstrap was attempted.");
        try
        {
            await admin.RunCommandAsync<BsonDocument>(new BsonDocument("replSetInitiate", new BsonDocument { { "_id", PilotBoundary.ReplicaSet },
                { "members", new BsonArray { new BsonDocument { { "_id", 0 }, { "host", "127.0.0.1:" + PilotBoundary.MongoPort } } } } }), cancellationToken: ct);
        }
        catch (MongoCommandException exception) when (exception.Code == 23) { }
        var client = new MongoClient(PilotBoundary.MongoUri + "&serverSelectionTimeoutMS=1000");
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct); ready.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                var hello = await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: ready.Token);
                if (hello.GetValue("setName", "") == PilotBoundary.ReplicaSet && hello.GetValue("isWritablePrimary", false).ToBoolean()) return client;
            }
            catch (TimeoutException) { }
            await Task.Delay(250, ready.Token);
        }
    }

    public static async Task<(Process Process, string Address)> NextAsync(string repository, string node, string stateDirectory, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(node) || !File.Exists(node)) throw new InvalidOperationException("Supply the existing Node executable path.");
        if (!File.Exists(Path.Combine(repository, "frontend", PilotBoundary.NextBuildFolder, "BUILD_ID"))) throw new InvalidOperationException("Build the " + PilotBoundary.Profile + " frontend before starting the pilot.");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo(node) { WorkingDirectory = Path.Combine(repository, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { Path.Combine(repository, "frontend", "node_modules", "next", "dist", "bin", "next"), "start", "--hostname", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = PilotBoundary.Profile;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Next.js.");
        // Drain bounded process output; authentication request data is never logged by this launcher.
        process.OutputDataReceived += (_, value) => { if (value.Data is { } line) AppendLog(stateDirectory, line); };
        process.ErrorDataReceived += (_, value) => { if (value.Data is { } line) AppendLog(stateDirectory, line); };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var address = "http://127.0.0.1:" + port;
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException("Next.js stopped; inspect the private next.log.");
                try { using var response = await client.GetAsync(address + "/identity/login", deadline.Token); if ((int)response.StatusCode < 500) return (process, address); }
                catch (HttpRequestException) { }
                await Task.Delay(250, deadline.Token);
            }
        }
        catch { if (!process.HasExited) process.Kill(true); process.Dispose(); throw; }
    }
    private static readonly object LogGate = new();
    private static void AppendLog(string directory, string line)
    {
        lock (LogGate) File.AppendAllText(Path.Combine(directory, "next.log"), line[..Math.Min(line.Length, 2000)] + Environment.NewLine);
    }
}
