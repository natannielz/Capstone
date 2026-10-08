using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Procurement;
using Xunit.Abstractions;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class ReplenishmentBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_replenishment_uses_genuine_stock_persists_recovers_refreshes_and_explicitly_creates_procurement_work()
    {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await StockReleaseServiceTests.Data.CreateAsync(fixture, useSystemClock: true);
        var released = await data.ProposeAsync("replenishment-browser-release");
        await data.DecideAsync(released["proposalId"].AsInt64, "replenishment-browser-released");
        var procurement = data.Valuation.Inspection.Receiving.Procurement;
        var actor = await data.Valuation.Base.AddActorAsync(109803, ProcurementRequestTestData.Scope,
            Permissions.StockRead, Permissions.ProcurementRead, Permissions.ProcurementPrepare);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, actor.UserId);
        var physical = await data.BalanceAsync(); var sku = await data.Rows("skus").Find(new BsonDocument("_id", physical["skuId"])).SingleAsync();
        var definition = await data.Rows("variantDefinitions").Find(new BsonDocument("_id", physical["definitionId"])).SingleAsync();
        var pool = await data.Rows("inventoryPools").Find(new BsonDocument("_id", physical["physicalPoolId"])).SingleAsync();
        var requestsBefore = await data.Rows(ProcurementRequestTestData.Requests).CountDocumentsAsync(new BsonDocument());
        var purchaseBefore = await data.Rows("purchaseOrders").CountDocumentsAsync(new BsonDocument());
        var movementsBefore = await data.Rows("movements").CountDocumentsAsync(new BsonDocument());
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock,
            new("https://approved.example/", "fixture-connection", "fixture-policy"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var issued = new Dictionary<long, SessionIssue>();
        foreach (var participant in new[] {actor, procurement.Outsider}) {
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", participant.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            var now = data.Clock.UtcNow;
            var proof = new ProviderIdentityProof(participant.ProviderIssuer, participant.ProviderSubject, "replenishment-" + participant.UserId + "@example.invalid", true,
                "fixture-connection", now, now, "otp", "fixture-policy");
            issued[participant.UserId] = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(participant.UserId, 1, participant.GrantVersion, participant.ProviderIssuer, participant.ProviderSubject), proof, "synthetic-replenishment-provider", ct));
            await data.Rows("accountProfiles").ReplaceOneAsync(new BsonDocument("_id", participant.UserId), new BsonDocument { { "_id", participant.UserId },
                { "schemaVersion", 1 }, { "version", 1L }, { "displayName", "Synthetic replenishment participant " + participant.UserId },
                { "verifiedEmail", "replenishment-" + participant.UserId + "@example.invalid" }, { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } }, new ReplaceOptions { IsUpsert = true });
        }
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<ReplenishmentService>(); services.AddSingleton(new ReplenishmentService(data.Database, data.Unit, new(data.Database), data.Clock));
            services.RemoveAll<ProcurementRequestService>(); services.AddSingleton(procurement.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko replenishment rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { replenishment = true, apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = issued[actor.UserId].OpaqueToken, makerToken = issued[actor.UserId].OpaqueToken, makerId = actor.UserId.ToString(),
            outsiderToken = issued[procurement.Outsider.UserId].OpaqueToken, outsiderId = procurement.Outsider.UserId.ToString(),
            poolId = pool["_id"].ToString(), skuId = sku["_id"].ToString(), skuCode = sku["sku"].AsString, packageLabel = definition["packageLabel"].AsString,
            businessDate = UnitToko.Modules.Accounting.AccountingCalendar.BusinessDate(data.Clock.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) };
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "replenishment-" + Guid.NewGuid().ToString("N") + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Replenishment browser child did not start");
        async Task ReadAsync(StreamReader stream) {while (await stream.ReadLineAsync() is { } line) {lock (lines) {lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine);}}}
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try {await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); await Task.WhenAll(stdout, stderr);
            output.WriteLine(lines.ToString()); Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_REPLENISHMENT_BROWSER_PASS", lines.ToString());
        } finally {if (!process.HasExited) {process.Kill(true); await process.WaitForExitAsync();} await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser log: " + logPath);}
        var review = await data.Rows(ReplenishmentRecords.Collection).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(actor.UserId, review["actorId"].AsInt64); Assert.Equal(10L, review["advice"]["suggestedPurchasePackages"].AsInt64);
        Assert.Equal(requestsBefore + 1, await data.Rows(ProcurementRequestTestData.Requests).CountDocumentsAsync(new BsonDocument()));
        var request = await data.Rows(ProcurementRequestTestData.Requests).Find(new BsonDocument("input.externalReference", "replenishment:" + review["_id"].AsInt64)).SingleAsync();
        Assert.Equal(10, Assert.Single(request["input"]["lines"].AsBsonArray)["packages"].ToInt32());
        Assert.Equal(physical, await data.BalanceAsync()); Assert.Equal(movementsBefore, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(purchaseBefore, await data.Rows("purchaseOrders").CountDocumentsAsync(new BsonDocument()));
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
