using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace UnitToko.LocalPilot;

internal static class EntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("This isolated local pilot launcher supports Windows only."); return 2; }
        if (args.Length is not (7 or 9) || args[0] != "--synthetic-local-pilot" || args[1] != "--repository" || args[3] != "--mongo" || args[5] != "--node" ||
            args.Length == 9 && args[7] != "--profile")
        { Console.Error.WriteLine("Explicit consent required: --synthetic-local-pilot --repository <root> --mongo <mongod.exe> --node <node.exe> [--profile release1|full]"); return 2; }
        try { PilotBoundary.UseProfile(args.Length == 9 ? args[8] : "release1"); }
        catch (InvalidOperationException exception) { Console.Error.WriteLine(exception.Message); return 2; }
        try { return await RunAsync(args[2], args[4], args[6]); }
        catch (Exception exception) { Console.Error.WriteLine("Local pilot stopped: " + exception.Message); return 1; }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunAsync(string repository, string mongo, string node)
    {
        repository = Path.GetFullPath(repository);
        var directory = PilotBoundary.StateDirectory(repository);
        PilotBoundary.ProtectDirectory(directory);
        using var singleInstance = new FileStream(Path.Combine(directory, "launcher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var state = new PilotState(directory);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var client = await PilotRuntime.MongoAsync(mongo, directory, stop.Token);
        await PilotBootstrap.InitializeAsync(client, state.Secrets, stop.Token);
        using var signing = state.Certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("Missing pilot signing key.");
        using var provider = new SyntheticOidcProvider(state.Secrets, signing);
        await using var api = new PilotApiFactory(repository, state, provider);
        api.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        api.StartServer();
        var next = await PilotRuntime.NextAsync(repository, node, directory, stop.Token);
        using var nextProcess = next.Process;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production", ContentRootPath = repository });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => { options.Limits.MaxRequestBodySize = 1_048_576; options.Listen(IPAddress.Loopback, 443, endpoint => endpoint.UseHttps(state.Certificate)); });
            await using var edge = builder.Build();
            using var forward = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) };
            edge.Run(async context =>
            {
                if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                    !context.Request.IsHttps || !context.Request.Host.Port.GetValueOrDefault(443).Equals(443))
                { context.Response.StatusCode = 403; return; }
                if (context.Request.Host.Host == PilotBoundary.ProviderHost) { await provider.HandleAsync(context); return; }
                if (context.Request.Host.Host != PilotBoundary.PortalHost) { context.Response.StatusCode = 404; return; }
                var path = context.Request.Path;
                if (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/v1")) { context.Response.StatusCode = 404; return; }
                var address = path.StartsWithSegments("/api/v1") || path.StartsWithSegments("/auth") ? api.Address : next.Address;
                await ForwardAsync(context, forward, address);
            });
            await edge.StartAsync(stop.Token);
            Console.WriteLine("SYNTHETIC LOCAL PILOT (" + PilotBoundary.Profile + ") — no real identity, MFA, approved catalog, purchases or stock.");
            Console.WriteLine("Private account/password/simulated factor file: " + Path.Combine(directory, "ACCOUNTS-PRIVATE.txt"));
            Console.WriteLine("Open with the isolated browser shortcut below. Ordinary browser profiles cannot resolve these private names.");
            Console.WriteLine(PilotBoundary.Origin + "/identity/account");
            SaveBrowserLauncher(repository, state);
            Console.WriteLine("Browser launcher: " + Path.Combine(directory, "open-browser.ps1"));
            Console.WriteLine("Ctrl+C stops API and Next. MongoDB remains loopback-only; its data, keys and accounts persist.");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            await edge.StopAsync(CancellationToken.None);
        }
        finally { if (!nextProcess.HasExited) { nextProcess.Kill(true); await nextProcess.WaitForExitAsync(); } }
        return 0;
    }

    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host" };
    private static async Task ForwardAsync(HttpContext context, HttpClient client, string upstream)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstream + context.Request.PathBase + context.Request.Path + context.Request.QueryString);
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")) request.Content = new StreamContent(context.Request.Body);
        foreach (var header in context.Request.Headers)
        {
            if (HopHeaders.Contains(header.Key) || header.Key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
                header.Key.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
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
                if (!HopHeaders.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (HttpRequestException) { if (!context.Response.HasStarted) { context.Response.StatusCode = 502; await context.Response.WriteAsync("Layanan pilot lokal belum tersedia."); } }
    }

    [SupportedOSPlatform("windows")]
    private static void SaveBrowserLauncher(string repository, PilotState state)
    {
        // Generated only in private ignored state. No global hosts entry, certificate store, or default profile is changed.
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = "$ErrorActionPreference = 'Stop'\n$edgePath = @((${env:ProgramFiles(x86)} + '\\Microsoft\\Edge\\Application\\msedge.exe'), (${env:ProgramFiles} + '\\Microsoft\\Edge\\Application\\msedge.exe')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1\n" +
            "if (-not $edgePath) { throw 'Microsoft Edge is required; no browser is downloaded automatically.' }\n" +
            "$pilotArguments = @(" + string.Join(",", new[] { "--user-data-dir=" + Path.Combine(state.Directory, "browser-profile"),
                "--host-resolver-rules=MAP " + PilotBoundary.PortalHost + " 127.0.0.1, MAP " + PilotBoundary.ProviderHost + " 127.0.0.1",
                "--ignore-certificate-errors-spki-list=" + state.SpkiPin, "--no-proxy-server", "--no-first-run", "--disable-sync", PilotBoundary.Origin + "/identity/account" }.Select(Quote)) + ")\n" +
            "$start = [System.Diagnostics.ProcessStartInfo]::new($edgePath)\n$start.UseShellExecute = $false\nforeach ($item in $pilotArguments) { $start.ArgumentList.Add($item) }\n[void][System.Diagnostics.Process]::Start($start)\n";
        _ = repository;
        File.WriteAllText(Path.Combine(state.Directory, "open-browser.ps1"), script);
    }
}
