using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Platform;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using UnitToko.Tests.Ordering;
using Xunit.Abstractions;

namespace UnitToko.Tests.Catalog;

// New scan semantics only; genuine procurement/release/order owners establish the existing work.
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OperationalScanFulfillmentBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Browser_explicit_scans_bind_one_picking_and_one_finite_dispatch_and_recover_both_original_keys()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var root = Root(); var frontend = Frontend(root);
        var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node));
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, converted: false, cogs: true,
            createOrder: false, reviewedSafetyBufferBaseUnits: 0, useSystemClock: true);
        var actors = new Dictionary<string, ActorContext> { ["buyer"] = data.Buyer, ["spender"] = data.Spender,
            ["dispatcher"] = data.Dispatcher, ["courier"] = data.Courier };
        foreach (var actor in actors.Values) if (!await data.Rows("accountProfiles").Find(new BsonDocument("_id", actor.UserId)).AnyAsync()) {
            var at = data.Clock.UtcNow;
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic scan participant " + actor.UserId }, { "verifiedEmail", "scan-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", at.UtcDateTime }, { "updatedAt", at.UtcDateTime } });
        }
        async Task<SessionIssue> Issue(string name) {
            actors[name] = await data.IssueExistingSessionAsync(actors[name]);
            return data.Catalog.Issued[actors[name].UserId];
        }
        foreach (var name in actors.Keys.ToArray()) await Issue(name);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Release:Profile"] = "full"; settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<MongoClient>(); services.AddSingleton((MongoClient)data.Database.Client);
            services.RemoveAll<IMongoDatabase>(); services.AddSingleton(data.Database);
            services.RemoveAll<MongoUnitOfWork>(); services.AddSingleton(data.Unit);
            services.RemoveAll<IdAllocator>(); services.AddSingleton(new IdAllocator(data.Database));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Catalog.Sessions);
            services.AddSingleton<IStartupFilter>(new RenewSession(controlKey, async () => (await Issue("dispatcher")).OpaqueToken));
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        // These are the production DI owners, including approved admission readers; no synthetic business adapter is registered.
        var orders = factory.Services.GetRequiredService<OrderSubmissionService>();
        var preview = await orders.PreviewAsync(actors["buyer"], data.Input);
        var ordered = await orders.SubmitAsync(actors["buyer"], data.Input, preview.Review, GenuineFiniteDispatchTestData.Meta("scan-browser-approved-order"));
        var orderId = ordered["orderId"].AsInt64; var suborderId = Assert.Single(ordered["suborderIds"].AsBsonArray).AsInt64;
        var spend = await orders.ReviewOrderAsync(actors["spender"], orderId, null, OrderReviewStage.Spending);
        await orders.DecideOrderAsync(actors["spender"], orderId, null, OrderReviewStage.Spending, spend.Revision, spend.Review, GenuineFiniteDispatchTestData.Meta("scan-browser-independent-spend"));
        var store = await orders.ReviewOrderAsync(actors["dispatcher"], orderId, suborderId, OrderReviewStage.Store);
        await orders.DecideOrderAsync(actors["dispatcher"], orderId, suborderId, OrderReviewStage.Store, store.Revision, store.Review, GenuineFiniteDispatchTestData.Meta("scan-browser-store-accept"));
        var source = await orders.ReadFulfillmentOrderAsync(actors["dispatcher"], suborderId); var line = Assert.Single(source.Lines);
        var identifier = await data.Rows(CatalogIdentifierRecords.Collection).Find(new BsonDocument { { "skuId", line.SkuId.Value }, { "isPrimary", true } }).SingleAsync();
        var before = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        var journalsBefore = await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument());
        var movementsBefore = await data.Rows("movements").CountDocumentsAsync(new BsonDocument());
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=Local operational scan rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { apiAddress, origin, controlKey, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, dispatcherToken = (await Issue("dispatcher")).OpaqueToken,
            dispatcherId = actors["dispatcher"].UserId.ToString(CultureInfo.InvariantCulture), courierId = actors["courier"].UserId.ToString(CultureInfo.InvariantCulture),
            orderId = orderId.ToString(CultureInfo.InvariantCulture), suborderId = suborderId.ToString(CultureInfo.InvariantCulture),
            skuId = line.SkuId.ToString(), skuCode = line.SkuCode, definitionId = line.Package.Id.ToString(), definitionVersion = line.Package.Version.ToString(CultureInfo.InvariantCulture),
            identifierId = identifier["_id"].AsInt64.ToString(CultureInfo.InvariantCulture), identifierRevision = identifier["revision"].AsInt64.ToString(CultureInfo.InvariantCulture),
            lotId = before.Balance.Key.LotId.ToString(), packages = 1, proofPrefix = "operational-scan-fulfillment-" + Guid.NewGuid().ToString("N") };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = frontend, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(frontend, "scripts", "verify-operational-scan-fulfillment-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Scan browser did not start");
        try {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6)); output.WriteLine(await stdout); output.WriteLine(await stderr);
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("REAL_OPERATIONAL_SCAN_FULFILLMENT_BROWSER_PASS", await stdout);
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        foreach (var operation in new[] { ShipmentPreparationBson.Operation, ShipmentDispatchBson.Operation }) {
            var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", operation)).SingleAsync();
            var binding = await data.Rows(OperationalScanCommandBindings.Collection).Find(new BsonDocument("operation", operation)).SingleAsync();
            OperationalScanCommandBindings.RequireRecord(binding);
            Assert.Equal("scanned", binding["mode"].AsString); Assert.Equal(receipt["key"], binding["requestKey"]);
            Assert.Equal(receipt["_id"], binding["receiptId"]); Assert.Equal(OperationalScanCommandBindings.Digest(receipt), binding["receiptHash"].AsString);
            var selection = Assert.Single(binding["selections"].AsBsonArray)["identifier"];
            Assert.Equal(identifier["_id"], selection["identifierId"]); Assert.Equal(identifier["revision"], selection["identifierRevision"]);
            Assert.Equal(line.SkuId.Value, selection["skuId"].AsInt64); Assert.Equal(line.Package.Id.Value, selection["definitionId"].AsInt64);
            Assert.Equal(line.Package.Version, selection["definitionVersion"].AsInt64);
        }
        Assert.Equal(1L, await data.Rows(ShipmentPreparationBson.Heads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(ShipmentPreparationBson.Versions).CountDocumentsAsync(new BsonDocument()));
        var shipment = await data.Rows("shipments").Find(new BsonDocument()).SingleAsync();
        Assert.Equal("committed", shipment["phase"].AsString); Assert.Equal(actors["dispatcher"].UserId, shipment["dispatchedBy"].AsInt64);
        Assert.Equal(actors["courier"].UserId, shipment["courierId"].AsInt64);
        Assert.Equal(before with { Balance = StockBalancePolicy.Dispatch(before.Balance, before.Balance.Revision, 1) },
            InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync()));
        Assert.Equal(journalsBefore + 1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(movementsBefore + 1, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("_id", shipment["journalId"])).SingleAsync();
        Assert.Equal(11250m, journal["lines"].AsBsonArray.Single(row => row["mappingKey"] == "cogs")["debit"].AsDecimal);
        Assert.Equal(11250m, journal["lines"].AsBsonArray.Single(row => row["mappingKey"] == "inventory")["credit"].AsDecimal);
        var stable = await data.BusinessAsync();
        foreach (var operation in new[] { ShipmentPreparationBson.Operation, ShipmentDispatchBson.Operation }) {
            var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", operation)).SingleAsync();
            if (operation == ShipmentPreparationBson.Operation) Assert.NotNull(await factory.Services.GetRequiredService<ShipmentPreparationService>().RecoverAsync(actors["dispatcher"], receipt["key"].AsString));
            else Assert.NotNull(await factory.Services.GetRequiredService<ShipmentDispatchService>().RecoverAsync(actors["dispatcher"], receipt["key"].AsString));
        }
        Assert.Equal(stable, await data.BusinessAsync());
    }
    private sealed class RenewSession(string secret, Func<Task<string>> issue) : IStartupFilter {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use(async (context, invoke) => {
                if (context.Request.Path != "/__fixture/operational-scan/session") { await invoke(); return; }
                if (context.Request.Method != "POST" || context.Request.Headers["X-Fixture-Control"] != secret) { context.Response.StatusCode = 404; return; }
                await context.Response.WriteAsJsonAsync(new { token = await issue() });
            }); next(app);
        };
    }
    private static string Frontend(string root) {
        var path = Environment.GetEnvironmentVariable("UNITTOKO_SCAN_BROWSER_FRONTEND") ?? Path.Combine(root, "frontend");
        Assert.True(Path.IsPathFullyQualified(path)); path = Path.GetFullPath(path);
        Assert.True(path == Path.Combine(root, "frontend") || path.StartsWith(Path.Combine(root, "tmp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(path, ".next-full", "BUILD_ID"))); return path;
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
