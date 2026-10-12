using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Tests.Ordering;

/// <summary>Actual production Next/HTTPS/API/Mongo and approved commercial/finite stock owners.
/// Local synthetic identity proofs and contact display data confer no business or stock authority.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderSubstitutionBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Browser_staff_proposal_buyer_decline_and_exact_consent_recover_original_keys_then_dispatch_real_replacement() {
        var root = Root(); var frontend = Frontend(root); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(File.Exists(playwright));
        var buildPath = Path.Combine(frontend, ".next-full", "BUILD_ID"); Assert.True(File.Exists(buildPath));
        var prepared = await OrderSubstitutionTestData.CreateAsync(fixture, systemClock: true); await using var data = prepared.Data;
        // A real dispatcher completes MFA again before dispatching; the system-clock setup exceeds its freshness window.
        data.Dispatcher = await data.IssueExistingSessionAsync(data.Dispatcher);
        var firstReview = await data.PrepareDispatchAsync(8, "substitution-browser-original-prepared");
        var first = await data.DispatchAsync(firstReview, "substitution-browser-original-dispatched");
        var firstShipment = await data.Rows("shipments").Find(new BsonDocument("_id", first["shipmentId"])).SingleAsync();
        var originalCommercial = await data.Unit.ExecuteAsync((tx, ct) => data.Orders.InvoiceCommercialAsync(tx, data.Buyer, firstShipment, ct));
        var originalOrder = (await data.Rows(B.Orders).Find(new BsonDocument("_id", data.OrderId)).SingleAsync()).DeepClone().AsBsonDocument; originalOrder.Remove("fence");
        var actors = new Dictionary<string, ActorContext> { ["staff"] = data.Dispatcher, ["buyer"] = data.Buyer, ["spender"] = data.Spender, ["courier"] = data.Courier };
        foreach (var name in actors.Keys.ToArray()) {
            actors[name] = await data.IssueExistingSessionAsync(actors[name]); var actor = actors[name];
            if (!await data.Rows("accountProfiles").Find(new BsonDocument("_id", actor.UserId)).AnyAsync())
                await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                    { "displayName", "Synthetic substitution participant " + actor.UserId }, { "verifiedEmail", "substitution-" + actor.UserId + "@example.invalid" },
                    { "verifiedAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime } });
        }
        var physicalBefore = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.Stock.Valuation.Receipt["balanceId"])).SingleAsync());
        var oldBefore = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        var packages = await data.Orders.SubstitutionPackagesAsync(actors["staff"], data.OrderId, data.SuborderId);
        var replacement = Assert.Single(packages.Items, item => item.Definition.SkuId.Value == prepared.ReplacementSku);
        var financialBefore = await Financial(data); var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        UnitToko.Platform.Integrity.ProvenanceSealRing.Configure(UnitToko.Platform.Integrity.SealKeyRing.ForKeys("browser-seal",
            new Dictionary<string, byte[]> { ["browser-seal"] = RandomNumberGenerator.GetBytes(32) }));
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Release:Profile"] = "full"; settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<MongoClient>(); services.AddSingleton((MongoClient)data.Database.Client);
            services.RemoveAll<IMongoDatabase>(); services.AddSingleton(data.Database);
            services.RemoveAll<MongoUnitOfWork>(); services.AddSingleton(data.Unit);
            services.RemoveAll<IdAllocator>(); services.AddSingleton(new IdAllocator(data.Database));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Catalog.Sessions);
            services.AddSingleton<IStartupFilter>(new Control(data, controlKey, actors));
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=Local substitution rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), prefix = "order-substitution-" + Guid.NewGuid().ToString("N");
        static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
        string Token(string name) => data.Catalog.Issued[actors[name].UserId].OpaqueToken;
        var config = new { orderSubstitution = true, apiAddress, origin, controlKey, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, proofPrefix = prefix, buildId = (await File.ReadAllTextAsync(buildPath)).Trim(), financeToken = Token("staff"), makerToken = Token("staff"),
            buyerToken = Token("buyer"), spenderToken = Token("spender"), actorIds = actors.ToDictionary(pair => pair.Key, pair => Id(pair.Value.UserId)),
            orderId = Id(data.OrderId), suborderId = Id(data.SuborderId), originalSkuId = Id(data.SkuId), replacementSkuId = Id(prepared.ReplacementSku),
            replacementCode = replacement.SkuCode, replacementLabel = replacement.Definition.PackageLabel, replacementLotId = physicalBefore.Balance.Key.LotId.ToString(),
            baseline = await State(data), clockFixture = "Actual system UTC from setup; fresh synthetic-provider sessions without clock rewinding" };
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, prefix + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = frontend, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(frontend, "scripts", "verify-invoice-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Substitution browser child did not start");
        async Task Read(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) lock (lines) { lines.AppendLine(line); File.AppendAllText(log, line + Environment.NewLine); } }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(12)); await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_ORDER_SUBSTITUTION_BROWSER_PASS", lines.ToString());
        } finally {
            UnitToko.Platform.Integrity.ProvenanceSealRing.Configure(null);
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser child log: " + log); }
        Assert.Equal(2L, await data.Rows(S.Proposals).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(S.Decisions).CountDocumentsAsync(new BsonDocument("decision", "Decline")));
        var accepted = await data.Rows(S.Decisions).Find(new BsonDocument("decision", "Accept")).SingleAsync();
        Assert.Equal(actors["buyer"].UserId, accepted["actorId"].AsInt64);
        Assert.Equal(2L, await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", "ordering.substitution")));
        var oldAfter = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        Assert.Equal(oldBefore with { Balance = StockBalancePolicy.Release(oldBefore.Balance, oldBefore.Balance.Revision, 4) }, oldAfter);
        var reserve = StockBalancePolicy.Reserve(physicalBefore.Balance, physicalBefore.Balance.Revision, 1);
        Assert.Equal(physicalBefore with { Balance = StockBalancePolicy.Dispatch(reserve, reserve.Revision, 1) },
            InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", physicalBefore.Id)).SingleAsync()));
        var retained = (await data.Rows(B.Orders).Find(new BsonDocument("_id", data.OrderId)).SingleAsync()).DeepClone().AsBsonDocument; retained.Remove("fence");
        Assert.Equal(originalOrder, retained); Assert.Equal(financialBefore, await Financial(data));
        Assert.Equal(originalCommercial, await data.Unit.ExecuteAsync((tx, ct) => data.Orders.InvoiceCommercialAsync(tx, actors["buyer"], firstShipment, ct)));
        var latest = await data.Rows("shipments").Find(new BsonDocument("_id", new BsonDocument("$ne", firstShipment["_id"]))).SingleAsync();
        var commercial = await data.Unit.ExecuteAsync((tx, ct) => data.Orders.InvoiceCommercialAsync(tx, actors["buyer"], latest, ct));
        Assert.Equal(accepted["catalog"], commercial["quote"]);
        foreach (var operation in new[] { S.AcceptOperation, S.DeclineOperation }) Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", operation)));
    }
    private sealed class Control(GenuineFiniteDispatchTestData data, string secret, Dictionary<string, ActorContext> actors) : IStartupFilter {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use(async (context, invoke) => {
                // A real dispatcher completes MFA again before dispatching: re-issue the named synthetic actor's session.
                if (context.Request.Path == "/__fixture/fresh-session") {
                    if (context.Request.Method != "POST" || context.Request.Headers["X-Fixture-Control"] != secret ||
                        context.Request.Query["actor"].ToString() is not "staff") { context.Response.StatusCode = 404; return; }
                    actors["staff"] = await data.IssueExistingSessionAsync(actors["staff"]);
                    await context.Response.WriteAsJsonAsync(new { token = data.Catalog.Issued[actors["staff"].UserId].OpaqueToken }); return;
                }
                if (context.Request.Path != "/__fixture/substitution-state") { await invoke(); return; }
                if (context.Request.Method != "POST" || context.Request.Headers["X-Fixture-Control"] != secret) { context.Response.StatusCode = 404; return; }
                await context.Response.WriteAsJsonAsync(await State(data));
            }); next(app);
        };
    }
    private static async Task<object> State(GenuineFiniteDispatchTestData data) {
        var physical = new BsonDocument();
        foreach (var name in new[] { "stockBalances", "journalEntries", LotValueStore.AvailableHeads, LotValueStore.HeldHeads, LotValueStore.Transitions }) {
            var rows = await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); // Freshness fence increments are not a stock, value or journal effect.
            physical[name] = new BsonArray(rows);
        }
        return new { proposals = await data.Rows(S.Proposals).CountDocumentsAsync(new BsonDocument()),
            decisions = await data.Rows(S.Decisions).CountDocumentsAsync(new BsonDocument()), shipments = await data.Rows("shipments").CountDocumentsAsync(new BsonDocument()),
            movements = await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", "ordering.substitution")),
            physicalHash = Convert.ToHexString(SHA256.HashData(physical.ToBson())), financialHash = Convert.ToHexString(SHA256.HashData((await Financial(data)).ToBson())) };
    }
    private static async Task<BsonDocument> Financial(GenuineFiniteDispatchTestData data) {
        var result = new BsonDocument(); foreach (var name in new[] { "invoices", "paymentAllocations", "payments", "bankReceipts" })
            result[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()); return result;
    }
    private static string Frontend(string root) {
        var supplied = Environment.GetEnvironmentVariable("UNITTOKO_SUBSTITUTION_BROWSER_FRONTEND");
        if (string.IsNullOrWhiteSpace(supplied)) return Path.Combine(root, "frontend");
        if (!Path.IsPathFullyQualified(supplied)) throw new InvalidOperationException("Browser frontend override must be absolute");
        var resolved = Path.GetFullPath(supplied);
        var allowed = Path.GetFullPath(Path.Combine(root, "tmp")) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(resolved) ||
            !File.Exists(Path.Combine(resolved, "package.json")) || !File.Exists(Path.Combine(resolved, "scripts", "verify-order-substitution-browser.mjs")) ||
            !File.Exists(Path.Combine(resolved, "scripts", "verify-invoice-browser.mjs")) || !File.Exists(Path.Combine(resolved, ".next-full", "BUILD_ID")))
            throw new InvalidOperationException("Browser frontend override must be a built source overlay below repository tmp");
        return resolved;
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
