using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;
using Xunit.Abstractions;
using UnitToko.Tests.Inventory;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderTimelineBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact(Skip = Pending.TransactionLatency)]
    public async Task Browser_reads_actual_grouped_delivery_invoice_bank_allocation_and_return_outcomes_after_restart_without_business_writes()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native browser rehearsal requires Windows");
        var root = RepositoryRoot(); var frontend = Path.Combine(root, "frontend");
        if (Environment.GetEnvironmentVariable("UNITTOKO_ORDER_TIMELINE_BROWSER_FRONTEND") is { Length: > 0 } candidate) {
            Assert.True(Path.IsPathFullyQualified(candidate)); frontend = Path.GetFullPath(candidate);
            Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "tmp")) + Path.DirectorySeparatorChar, frontend, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(frontend, "scripts", "verify-order-timeline-browser.mjs")));
        }
        var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node));
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        var buildPath = Path.Combine(frontend, ".next-full", "BUILD_ID"); Assert.True(File.Exists(buildPath));
        await using var data = await OrderTimelineFinancialTestData.CreateAsync(fixture, systemClock: true);
        var source = data.Release.Dispatch;
        var buyer = await source.IssueExistingSessionAsync(data.Buyer);
        var denied = await source.Base.AddActorAsync(729101, CatalogAuthoringTestData.Audience, Permissions.CatalogRead);
        denied = await source.IssueExistingSessionAsync(denied);
        foreach (var actor in new[] { buyer, denied }) {
            if (await source.Rows("accountProfiles").Find(new BsonDocument("_id", actor.UserId)).AnyAsync()) continue;
            await source.Rows("accountProfiles").InsertOneAsync(new BsonDocument {
                { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", "Synthetic timeline participant" },
                { "verifiedEmail", "timeline-" + actor.UserId + "@example.invalid" }, { "verifiedAt", source.Clock.UtcNow.UtcDateTime }, { "updatedAt", source.Clock.UtcNow.UtcDateTime } });
        }
        var baseline = await data.BusinessAsync();
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Release:Profile"] = "full"; settings["Mongo:Database"] = source.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<MongoClient>(); services.AddSingleton((MongoClient)source.Database.Client);
            services.RemoveAll<IMongoDatabase>(); services.AddSingleton(source.Database);
            services.RemoveAll<MongoUnitOfWork>(); services.AddSingleton(source.Unit);
            services.RemoveAll<IdAllocator>(); services.AddSingleton(new IdAllocator(source.Database));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(source.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(source.Catalog.Sessions);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=Local order timeline rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); var prefix = "order-timeline-" + Guid.NewGuid().ToString("N");
        static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
        var config = new { apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, buildId = (await File.ReadAllTextAsync(buildPath)).Trim(), proofPrefix = prefix, orderTimeline = true,
            financeToken = source.Catalog.Issued[buyer.UserId].OpaqueToken, buyerId = Id(buyer.UserId),
            deniedToken = source.Catalog.Issued[denied.UserId].OpaqueToken, deniedId = Id(denied.UserId),
            orderA = Id(data.Grouped.OrderA), orderB = Id(data.Grouped.OrderB), declarationId = Id(data.DeclarationId),
            creditCaseId = Id(data.CreditCaseId), refundCaseId = Id(data.RefundCaseId) };
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, prefix + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = frontend, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(frontend, "scripts", "verify-invoice-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Timeline browser child did not start");
        async Task Read(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) lock (lines) { lines.AppendLine(line); File.AppendAllText(log, line + Environment.NewLine); } }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6)); await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_ORDER_TIMELINE_BROWSER_PASS", lines.ToString());
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser child log: " + log);
        }
        Assert.Equal(baseline, await data.BusinessAsync());
    }
    private static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run the browser proof from the authoritative checkout");
    }
}
