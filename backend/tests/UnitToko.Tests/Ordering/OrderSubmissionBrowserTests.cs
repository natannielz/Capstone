using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderSubmissionBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Browser_saved_draft_checkout_reserves_Mongo_stock_and_recovers_a_lost_submit_without_repeating_it()
    {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture, 2);
        var draftService = new OrderDraftService(data.Database, new(fixture.Client), new(data.Database), data.Prices.Catalog.Clock);
        var draft = await draftService.SaveAsync(data.Buyer, null, 0, data.Input, CatalogPriceTestData.Meta("browser-draft"));
        foreach (var actor in new[] { data.Buyer, data.Prices.Maker })
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Akun fixture lokal" }, { "verifiedEmail", "order-" + actor.UserId + "@example.invalid" }, { "verifiedAt", data.Prices.Catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Prices.Catalog.Clock.UtcNow.UtcDateTime } });
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Prices.Catalog.Sessions);
            services.RemoveAll<OrderSubmissionService>(); services.AddSingleton(data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko order rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            buyerToken = data.Prices.Catalog.Issued[data.Buyer.UserId].OpaqueToken, deniedToken = data.Prices.Catalog.Issued[data.Prices.Maker.UserId].OpaqueToken,
            buyerId = data.Buyer.UserId.ToString(), deniedId = data.Prices.Maker.UserId.ToString(), draftId = draft["draftId"].AsInt64.ToString() };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-order-submission-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser child did not start");
        try {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); output.WriteLine(await stdout); output.WriteLine(await stderr);
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("REAL_ORDER_BROWSER_PASS", await stdout);
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        var order = Assert.Single(await data.Rows("orders").Find(new BsonDocument()).ToListAsync());
        Assert.Equal(data.Buyer.UserId, order["createdBy"].AsInt64); Assert.Equal("Pending", order["initialSpendingState"].AsString);
        Assert.Equal(25500.50m, Decimal128.ToDecimal(order["terms"]["total"].AsDecimal128));
        foreach (var name in new[] { "reservations", "movements", "orderSuborders" }) Assert.Equal(1, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, (await data.Rows("stockBalances").Find(new BsonDocument()).SingleAsync())["reserved"].AsInt64);
        Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", OrderSubmissionBson.Operation)));
        Assert.Equal(0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
