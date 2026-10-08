using System.Diagnostics;
using System.Net;
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
using UnitToko.Modules.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Operations;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using UnitToko.Tests.Ordering;
using Xunit.Abstractions;

namespace UnitToko.Tests.Operations;

[Collection("Mongo")]
public sealed class OperationalAnomalyBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Browser_real_source_policy_evaluation_review_and_lost_response_recover_without_business_effect()
    {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        var catalog = data.Prices.Catalog;
        var reviewer = await catalog.ActorAsync(7901, CatalogAuthoringTestData.Shop,
            Permissions.AnomalyRead, Permissions.AnomalyReview, Permissions.AnomalyPolicyConfigure, Permissions.OrderRead);
        var denied = catalog.Maker;
        foreach (var actor in new[] { reviewer, denied }) await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument {
            { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", "Petugas tinjauan sintetis" },
            { "verifiedEmail", "anomaly-" + actor.UserId + "@example.invalid" }, { "verifiedAt", catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", catalog.Clock.UtcNow.UtcDateTime } });
        var preview = await data.Service.PreviewAsync(data.Buyer, data.Input);
        var result = await data.Service.SubmitAsync(data.Buyer, data.Input, preview.Review, CatalogPriceTestData.Meta("browser-anomaly-order"));
        var sourceId = Assert.Single(result["suborderIds"].AsBsonArray).AsInt64;
        var original = await BusinessAsync(data);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1";
        settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(catalog.Sessions);
            services.RemoveAll<OrderSubmissionService>(); services.AddSingleton(data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko anomaly rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { operationalAnomalies = true, apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, financeToken = catalog.Issued[reviewer.UserId].OpaqueToken,
            deniedToken = catalog.Issued[denied.UserId].OpaqueToken, makerId = reviewer.UserId.ToString(), sourceId = sourceId.ToString() };
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "operational-anomalies-" + Guid.NewGuid().ToString("N") + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!, "scripts/verify-invoice-browser.mjs") { WorkingDirectory = Path.Combine(root, "frontend"),
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Anomaly browser child did not start");
        async Task ReadAsync(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) { lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); } } }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6)); await Task.WhenAll(stdout, stderr);
            output.WriteLine(lines.ToString()); Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_OPERATIONAL_ANOMALIES_BROWSER_PASS", lines.ToString());
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); output.WriteLine("Browser log: " + logPath); }
        // Two simultaneous-tab evaluations precede policy setup; a later evaluation uses that policy.
        Assert.Equal(3, await data.Rows(OperationalAnomalyRecords.Evaluations).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows(OperationalAnomalyRecords.PolicyVersions).CountDocumentsAsync(new BsonDocument()));
        var disposition = await data.Rows(OperationalAnomalyRecords.Dispositions).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(reviewer.UserId, disposition["actorId"].AsInt64); Assert.Equal("requires_action", disposition["outcome"].AsString);
        Assert.Equal(original, await BusinessAsync(data));
    }
    private static async Task<BsonDocument> BusinessAsync(OrderSubmissionTestData data) {
        var result = new BsonDocument();
        foreach (var name in new[] { "orders", "orderSuborders", "reservations", "stockBalances", "movements", "journalEntries", "authorizationGrants", "memberships" }) {
            var rows = await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); result[name] = new BsonArray(rows);
        }
        return result;
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
