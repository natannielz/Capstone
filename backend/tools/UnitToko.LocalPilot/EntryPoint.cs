using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.LocalPilot;

internal static class EntryPoint
{
    private const string Usage = "Explicit consent required: --synthetic-local-pilot --repository <root> --mongo <mongod.exe> --node <node.exe> " +
        "[--profile release1|full|review] [--clamav-signatures <folder>]";

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("This isolated local pilot launcher supports Windows only."); return 2; }
        if (args.Length < 7 || args.Length % 2 == 0 || args[0] != "--synthetic-local-pilot" || args[1] != "--repository" || args[3] != "--mongo" || args[5] != "--node")
        { Console.Error.WriteLine(Usage); return 2; }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 7; index < args.Length; index += 2)
            if (args[index] is not ("--profile" or "--clamav-signatures") || !options.TryAdd(args[index], args[index + 1])) { Console.Error.WriteLine(Usage); return 2; }
        try { PilotBoundary.UseProfile(options.GetValueOrDefault("--profile", "release1")); }
        catch (InvalidOperationException exception) { Console.Error.WriteLine(exception.Message); return 2; }
        try { return await RunAsync(args[2], args[4], args[6], options.GetValueOrDefault("--clamav-signatures")); }
        catch (Exception exception) { Console.Error.WriteLine("Local pilot stopped: " + exception.Message); return 1; }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunAsync(string repository, string mongo, string node, string? signatures)
    {
        repository = Path.GetFullPath(repository);
        var review = PilotBoundary.Profile == "review";
        if (review) PilotSessions.RequireLoopback(PilotBoundary.Edge);
        var directory = PilotBoundary.StateDirectory(repository);
        PilotBoundary.ProtectDirectory(directory);
        using var singleInstance = new FileStream(Path.Combine(directory, "launcher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var stopFile = Path.Combine(directory, "stop-requested");
        File.Delete(stopFile);
        using var state = new PilotState(directory);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        // start-local-pilot.ps1 -Stop asks a running instance to stop the same way Ctrl+C does.
        var stopWatch = WatchStopFileAsync(stopFile, stop);
        try
        {
            var client = await PilotRuntime.MongoAsync(mongo, directory, stop.Token);
            try { return await ServeAsync(repository, node, signatures, directory, state, stop, client); }
            finally
            {
                // The review stack stops its own MongoDB however serving ended (MongoAsync verified the process on its port).
                if (review)
                    try { await PilotRuntime.ShutdownMongoAsync(client); }
                    catch (Exception exception) { Console.WriteLine("MongoDB ulasan belum berhenti (" + exception.GetType().Name + ")."); }
            }
        }
        finally
        {
            if (!stop.IsCancellationRequested) stop.Cancel();
            await stopWatch;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> ServeAsync(string repository, string node, string? signatures, string directory, PilotState state,
        CancellationTokenSource stop, MongoClient client)
    {
        var review = PilotBoundary.Profile == "review";
        var database = client.GetDatabase(PilotBoundary.DatabaseName);
        await PilotBootstrap.InitializeAsync(client, state.Secrets, stop.Token);
        using var signing = state.Certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("Missing pilot signing key.");
        using var provider = new SyntheticOidcProvider(state.Secrets, signing);
        await using var photos = PilotBoundary.IsFullRelease
            ? await PilotPhotos.StartAsync(repository, directory, database, client, state.Secrets, signatures, stop.Token)
            : null;
        await using var api = new PilotApiFactory(repository, state, provider, photos?.ApiSettings);
        api.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        api.StartServer();
        var sessions = review ? ReviewSessions(repository, api, database, state) : null;
        var next = await PilotRuntime.NextAsync(repository, node, directory, stop.Token);
        using var nextProcess = next.Process;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production", ContentRootPath = repository });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = 1_048_576;
                foreach (var bind in PilotBoundary.Edge) options.Listen(bind, endpoint => endpoint.UseHttps(state.Certificate));
            });
            await using var edge = builder.Build();
            using var forward = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) };
            var browserHost = new Uri(PilotBoundary.BrowserOrigin).Authority;
            edge.Run(async context =>
            {
                if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) || !context.Request.IsHttps)
                { context.Response.StatusCode = 403; return; }
                if (review)
                {
                    // Review browsers open https://localhost:8443. A same-origin browser request is translated to the API's canonical
                    // origin; anything claiming another origin is refused here, so the API's origin checks keep their meaning.
                    if (context.Request.Host.Value != browserHost) { context.Response.StatusCode = 404; return; }
                    var origin = context.Request.Headers.Origin;
                    if (origin.Count > 1 || origin.Count == 1 && origin[0] != PilotBoundary.BrowserOrigin) { context.Response.StatusCode = 403; return; }
                }
                else
                {
                    if (!context.Request.Host.Port.GetValueOrDefault(443).Equals(443)) { context.Response.StatusCode = 403; return; }
                    if (context.Request.Host.Host == PilotBoundary.ProviderHost) { await provider.HandleAsync(context); return; }
                    if (context.Request.Host.Host != PilotBoundary.PortalHost) { context.Response.StatusCode = 404; return; }
                }
                var path = context.Request.Path;
                if (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/v1")) { context.Response.StatusCode = 404; return; }
                var address = path.StartsWithSegments("/api/v1") || path.StartsWithSegments("/auth") ? api.Address : next.Address;
                await ForwardAsync(context, forward, address, review);
            });
            await edge.StartAsync(stop.Token);
            Console.WriteLine("SYNTHETIC LOCAL PILOT (" + PilotBoundary.Profile + ") — no real identity, MFA, approved catalog, purchases or stock.");
            Console.WriteLine("Private account/password/simulated factor file: " + Path.Combine(directory, "ACCOUNTS-PRIVATE.txt"));
            if (review)
            {
                await sessions!.MintAllAsync(stop.Token);
                Console.WriteLine("Review stack (loopback only): " + PilotBoundary.BrowserOrigin + "/");
                Console.WriteLine("Persona sessions (Playwright storage state, refreshed every " + (int)PilotSessions.RefreshEvery.TotalMinutes + " minutes): " +
                    PilotSessions.SessionDirectory(repository));
            }
            else
            {
                Console.WriteLine("Open with the isolated browser shortcut below. Ordinary browser profiles cannot resolve these private names.");
                Console.WriteLine(PilotBoundary.Origin + "/");
                SaveBrowserLauncher(repository, state);
                Console.WriteLine("Browser launcher: " + Path.Combine(directory, "open-browser.ps1"));
            }
            Console.WriteLine("Ctrl+C (or start-local-pilot.ps1 -Profile " + PilotBoundary.Profile + " -Stop) stops API and Next. " +
                (review ? "The review MongoDB stops too; its data persists." : "MongoDB remains loopback-only; its data, keys and accounts persist."));
            var photoWork = photos?.RunAsync(stop.Token) ?? Task.CompletedTask;
            var refresh = sessions is null ? Task.CompletedTask : RefreshAsync(sessions, stop.Token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            await edge.StopAsync(CancellationToken.None);
            await refresh;
            await photoWork;
        }
        finally
        {
            if (!stop.IsCancellationRequested) stop.Cancel();
            // However serving ended, minted sessions are revoked and their files removed while the API still runs.
            if (sessions is not null)
                try
                {
                    var (revoked, failed) = await sessions.StopAsync(CancellationToken.None);
                    Console.WriteLine("Sesi persona dicabut: " + revoked + (failed > 0 ? ", gagal dicabut: " + failed + " (berakhir sendiri setelah 30 menit tanpa pemakaian)" : "") + ".");
                }
                catch (Exception exception) { Console.WriteLine("Berkas sesi persona belum terhapus (" + exception.GetType().Name + ")."); }
            if (!nextProcess.HasExited) { nextProcess.Kill(true); await nextProcess.WaitForExitAsync(); }
        }
        return 0;
    }

    /// <summary>Review persona sessions from the API's own session store, admitted like a provider sign-in with a fresh factor.</summary>
    [SupportedOSPlatform("windows")]
    private static PilotSessions ReviewSessions(string repository, PilotApiFactory api, IMongoDatabase database, PilotState state)
    {
        var store = api.Services.GetRequiredService<MongoIdentitySessionStore>();
        var unit = api.Services.GetRequiredService<MongoUnitOfWork>();
        var configured = api.Services.GetRequiredService<IOptions<Auth0Options>>().Value;
        var identities = database.GetCollection<BsonDocument>("identities");
        async Task<MintedSession> Issue(PilotPersona persona, CancellationToken ct)
        {
            var account = state.Secrets.Accounts.Single(item => item.Id == persona.AccountId);
            var identity = await identities.Find(new BsonDocument("_id", persona.AccountId)).SingleAsync(ct);
            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var proof = new ProviderIdentityProof(configured.Issuer, account.Subject, account.Email, true, configured.ConnectionId, now, now, "otp",
                configured.AssurancePolicyVersion);
            var issued = await unit.ExecuteAsync((tx, token) => store.CreateAsync(tx, new IdentityAdmission(persona.AccountId,
                identity["securityVersion"].AsInt64, identity["grantVersion"].AsInt64, configured.Issuer, account.Subject), proof, "synthetic-review-persona-session", token), ct);
            return new(issued.OpaqueToken, issued.Session.AbsoluteExpiresAt);
        }
        Task Revoke(string token, CancellationToken ct) => store.RevokeAsync(token, "SYNTHETIC review persona session rotated or stack stopped", ct);
        return PilotSessions.Create(repository, PilotBoundary.Edge, Issue, Revoke, (persona, ct) => PilotPersonas.DescribeAsync(database, persona, ct));
    }

    private static async Task RefreshAsync(PilotSessions sessions, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PilotSessions.RefreshEvery, ct); await sessions.MintAllAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception exception) { Console.WriteLine("Sesi persona belum diperbarui (" + exception.GetType().Name + ")."); }
        }
    }

    private static async Task WatchStopFileAsync(string path, CancellationTokenSource stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (File.Exists(path)) { File.Delete(path); stop.Cancel(); return; }
            try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { return; }
        }
    }

    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host" };
    private static async Task ForwardAsync(HttpContext context, HttpClient client, string upstream, bool review)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstream + context.Request.PathBase + context.Request.Path + context.Request.QueryString);
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")) request.Content = new StreamContent(context.Request.Body);
        foreach (var header in context.Request.Headers)
        {
            if (HopHeaders.Contains(header.Key) || header.Key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
                header.Key.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)) continue;
            var values = header.Value.ToArray();
            if (review && (header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)))
                values = values.Select(value => value is null ? null : ToCanonical(value)).ToArray();
            if (!request.Headers.TryAddWithoutValidation(header.Key, values)) request.Content?.Headers.TryAddWithoutValidation(header.Key, values);
        }
        request.Headers.Host = PilotBoundary.PortalHost;
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", PilotBoundary.PortalHost);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (HopHeaders.Contains(header.Key)) continue;
                // The review stack shares the browser's localhost name with other local work: never pin HTTPS on it.
                if (review && header.Key.Equals("Strict-Transport-Security", StringComparison.OrdinalIgnoreCase)) continue;
                var values = header.Value.ToArray();
                if (review && header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase)) values = values.Select(ToBrowser).ToArray();
                context.Response.Headers[header.Key] = values;
            }
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (HttpRequestException) { if (!context.Response.HasStarted) { context.Response.StatusCode = 502; await context.Response.WriteAsync("Layanan pilot lokal belum tersedia."); } }
    }
    private static string ToCanonical(string value) => value == PilotBoundary.BrowserOrigin ? PilotBoundary.Origin
        : value.StartsWith(PilotBoundary.BrowserOrigin + "/", StringComparison.Ordinal) ? PilotBoundary.Origin + value[PilotBoundary.BrowserOrigin.Length..] : value;
    private static string ToBrowser(string value) => value == PilotBoundary.Origin ? PilotBoundary.BrowserOrigin
        : value.StartsWith(PilotBoundary.Origin + "/", StringComparison.Ordinal) ? PilotBoundary.BrowserOrigin + value[PilotBoundary.Origin.Length..] : value;

    [SupportedOSPlatform("windows")]
    private static void SaveBrowserLauncher(string repository, PilotState state)
    {
        // Generated only in private ignored state. No global hosts entry, certificate store, or default profile is changed.
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = "$ErrorActionPreference = 'Stop'\n$edgePath = @((${env:ProgramFiles(x86)} + '\\Microsoft\\Edge\\Application\\msedge.exe'), (${env:ProgramFiles} + '\\Microsoft\\Edge\\Application\\msedge.exe')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1\n" +
            "if (-not $edgePath) { throw 'Microsoft Edge is required; no browser is downloaded automatically.' }\n" +
            "$pilotArguments = @(" + string.Join(",", new[] { "--user-data-dir=" + Path.Combine(state.Directory, "browser-profile"),
                "--host-resolver-rules=MAP " + PilotBoundary.PortalHost + " 127.0.0.1, MAP " + PilotBoundary.ProviderHost + " 127.0.0.1",
                "--ignore-certificate-errors-spki-list=" + state.SpkiPin, "--no-proxy-server", "--no-first-run", "--disable-sync", PilotBoundary.Origin + "/" }.Select(Quote)) + ")\n" +
            "$start = [System.Diagnostics.ProcessStartInfo]::new($edgePath)\n$start.UseShellExecute = $false\nforeach ($item in $pilotArguments) { $start.ArgumentList.Add($item) }\n[void][System.Diagnostics.Process]::Start($start)\n";
        _ = repository;
        File.WriteAllText(Path.Combine(state.Directory, "open-browser.ps1"), script);
    }
}
