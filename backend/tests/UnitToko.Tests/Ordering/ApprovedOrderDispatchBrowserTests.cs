using System.Diagnostics;
using System.Collections.Concurrent;
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
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Organizations;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;

namespace UnitToko.Tests.Ordering;

/// <summary>Actual production Next/HTTPS/API/Mongo, approved admission readers,
/// first order and finite dispatch. Identity proofs, contacts and reviewed roster
/// assignments are explicit local synthetic inputs; this is not provider acceptance.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ApprovedOrderDispatchBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Browser_first_order_independent_spending_store_preparation_dispatch_and_buyer_history_use_actual_approved_owners_once()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native browser rehearsal requires Windows");
        var root = RepositoryRoot(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        var buildPath = Path.Combine(root, "frontend", ".next-full", "BUILD_ID"); Assert.True(File.Exists(buildPath));
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, converted: false, cogs: true,
            createOrder: false, reviewedSafetyBufferBaseUnits: 0, useSystemClock: true);
        await data.Base.AddGrantAsync(data.Buyer, 719050, CatalogAuthoringTestData.Audience, [Permissions.ShipmentRead]);
        var actors = new Dictionary<string, ActorContext> {
            ["buyer"] = data.Buyer, ["spender"] = data.Spender, ["dispatcher"] = data.Dispatcher, ["courier"] = data.Courier
        };
        // Contact display rows carry no business approval or stock authority.
        foreach (var actor in actors.Values) if (!await data.Rows("accountProfiles").Find(new BsonDocument("_id", actor.UserId)).AnyAsync()) {
            var at = data.Clock.UtcNow;
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic order participant " + actor.UserId }, { "verifiedEmail", "order-dispatch-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", at.UtcDateTime }, { "updatedAt", at.UtcDateTime } });
        }
        async Task<SessionIssue> Issue(string name) {
            actors[name] = await data.IssueExistingSessionAsync(actors[name]);
            return data.Catalog.Issued[actors[name].UserId];
        }
        var tokens = new Dictionary<string, string>(); foreach (var name in actors.Keys.ToArray()) tokens[name] = (await Issue(name)).OpaqueToken;
        var drafts = new OrderDraftService(data.Database, data.Unit, new(data.Database), data.Clock);
        var draft = await drafts.SaveAsync(actors["buyer"], null, 0, data.Input, GenuineFiniteDispatchTestData.Meta("approved-browser-saved-draft"));
        var before = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        var valueBefore = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        Assert.Equal("11250.00", valueBefore["remainingValue"].AsString);
        var packages = Assert.Single(data.Input.Selections).Packages;
        Assert.Equal((long)packages, valueBefore["remainingPackages"].AsInt64);
        Assert.Equal(0L, await data.Rows(B.Orders).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows(B.Reservations).CountDocumentsAsync(new BsonDocument()));
        var baseline = await State(data); var untouched = await FinancialDocuments(data);
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
            services.AddSingleton<IStartupFilter>(new FixtureControl(data, controlKey, Issue, fixture, output));
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=Local approved order dispatch rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); var prefix = "approved-order-dispatch-" + Guid.NewGuid().ToString("N");
        var config = new { apiAddress, origin, controlKey, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, buildId = (await File.ReadAllTextAsync(buildPath)).Trim(), proofPrefix = prefix, tokens,
            actorIds = actors.ToDictionary(pair => pair.Key, pair => pair.Value.UserId.ToString(CultureInfo.InvariantCulture)),
            draftId = draft["draftId"].AsInt64.ToString(CultureInfo.InvariantCulture), skuId = data.SkuId.ToString(CultureInfo.InvariantCulture),
            lotId = before.Balance.Key.LotId.ToString(), packages, expectedValue = "11250.00", baseline,
            clockFixture = "Actual system UTC from initial stock setup, monotonic fresh synthetic-provider session renewal; no clock rewind" };
        var directory = Path.Combine(root, "tmp", "production", "approved-order-dispatch-browser-results"); Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, prefix + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-approved-order-dispatch-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Approved order browser child did not start");
        async Task Read(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) lock (lines) { lines.AppendLine(line); File.AppendAllText(log, line + Environment.NewLine); } }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(12)); await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("LOCAL_APPROVED_ORDER_DISPATCH_BROWSER_PASS", lines.ToString());
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser child log: " + log);
        }
        var order = await data.Rows(B.Orders).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(actors["buyer"].UserId, order["createdBy"].AsInt64);
        Assert.Equal(1L, await data.Rows(B.Reservations).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, await data.Rows("orderDecisions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(ShipmentPreparationBson.Heads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(ShipmentPreparationBson.Versions).CountDocumentsAsync(new BsonDocument()));
        var shipment = await data.Rows("shipments").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(2, shipment["schemaVersion"].AsInt32); Assert.Equal("committed", shipment["phase"].AsString);
        Assert.Equal(actors["dispatcher"].UserId, shipment["dispatchedBy"].AsInt64); Assert.Equal(actors["courier"].UserId, shipment["courierId"].AsInt64);
        var effect = Assert.Single(shipment["valueEffects"].AsBsonArray); Assert.Equal("11250.00", effect["transferredValue"].AsString);
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("_id", shipment["journalId"])).SingleAsync();
        Assert.Equal(11250m, journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "cogs")["debit"].AsDecimal);
        Assert.Equal(11250m, journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "inventory")["credit"].AsDecimal);
        var reserved = StockBalancePolicy.Reserve(before.Balance, before.Balance.Revision, packages);
        var expected = StockBalancePolicy.Dispatch(reserved, reserved.Revision, packages);
        Assert.Equal(before with { Balance = expected }, InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync()));
        var value = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        Assert.Equal(0L, value["remainingPackages"].AsInt64); Assert.Equal("0.00", value["remainingValue"].AsString);
        foreach (var operation in new[] { B.Operation, ShipmentPreparationBson.Operation, ShipmentDispatchBson.Operation })
            Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", operation)));
        var current = await factory.Services.GetRequiredService<OrderSubmissionService>().ReadOwnAsync(actors["buyer"], order["_id"].AsInt64);
        Assert.Equal(UnitToko.Modules.Ordering.OrderSpendingState.Approved, current.Progress.SpendingState);
        Assert.Single(current.Progress.AcceptedSuborders); Assert.Equal(1L, current.Progress.DispatchSequence); Assert.False(current.Progress.Cancelled);
        Assert.Equal(untouched, await FinancialDocuments(data));
    }
    private sealed class FixtureControl(GenuineFiniteDispatchTestData data, string secret, Func<string, Task<SessionIssue>> issue,
        MongoFixture fixture, ITestOutputHelper output) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use(async (context, invoke) => {
                var requestPath = context.Request.Path.Value;
                var profileName = requestPath == "/api/v1/ordering/orders" ? "ACTUAL_ORDER_SUBMIT_MONGO_PROFILE"
                    : requestPath == "/api/v1/ordering/orders/preview/query" ? "ACTUAL_ORDER_PREVIEW_MONGO_PROFILE"
                    : requestPath is not null && requestPath.StartsWith("/api/v1/fulfillment/dispatches/", StringComparison.Ordinal) &&
                        long.TryParse(requestPath["/api/v1/fulfillment/dispatches/".Length..], out var suborderId) && suborderId > 0
                        ? "ACTUAL_DISPATCH_MONGO_PROFILE" : null;
                if (context.Request.Method == "POST" && profileName is not null) {
                    var commands = new ConcurrentDictionary<string, (long Count, double Milliseconds)>();
                    var previous = fixture.CommandCompleted; var timer = Stopwatch.StartNew();
                    fixture.CommandCompleted = (name, milliseconds) => {
                        commands.AddOrUpdate(name, (1, milliseconds), (_, sum) => (sum.Count + 1, sum.Milliseconds + milliseconds));
                        previous?.Invoke(name, milliseconds);
                    };
                    try { await invoke(); }
                    finally {
                        fixture.CommandCompleted = previous;
                        var completedCommands = commands.ToArray();
                        output.WriteLine(profileName + " " + JsonSerializer.Serialize(new { elapsedMs = timer.ElapsedMilliseconds,
                            status = context.Response.StatusCode,
                            commandCount = completedCommands.Sum(row => row.Value.Count),
                            commandTotalMs = completedCommands.Sum(row => row.Value.Milliseconds),
                            commandKinds = completedCommands.Length,
                            commands = completedCommands.OrderByDescending(row => row.Value.Count).Take(20)
                                .Select(row => new { command = row.Key, count = row.Value.Count, totalMs = row.Value.Milliseconds }).ToArray(),
                            commandsByDuration = completedCommands.OrderByDescending(row => row.Value.Milliseconds).Take(20)
                                .Select(row => new { command = row.Key, count = row.Value.Count, totalMs = row.Value.Milliseconds }).ToArray() }));
                    }
                    return;
                }
                if (!context.Request.Path.StartsWithSegments("/__fixture/approved-order")) { await invoke(); return; }
                if (context.Request.Method != "POST" || context.Request.Headers["X-Fixture-Control"] != secret) { context.Response.StatusCode = 404; return; }
                var action = context.Request.Path.Value!["/__fixture/approved-order/".Length..];
                if (action == "state") { await context.Response.WriteAsJsonAsync(await State(data)); return; }
                if (action is "renew-buyer" or "renew-spender" or "renew-dispatcher") {
                    var session = await issue(action["renew-".Length..]); await context.Response.WriteAsJsonAsync(new { token = session.OpaqueToken }); return;
                }
                context.Response.StatusCode = 404;
            }); next(app);
        };
    }
    private static async Task<object> State(GenuineFiniteDispatchTestData data) {
        var physical = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        var value = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        var count = async (string name) => await data.Rows(name).CountDocumentsAsync(new BsonDocument());
        return new { orders = await count(B.Orders), reservations = await count(B.Reservations), decisions = await count("orderDecisions"),
            preparations = await count(ShipmentPreparationBson.Heads), shipments = await count("shipments"), journals = await count("journalEntries"),
            transitions = await count(LotValueStore.Transitions), movements = await count("movements"),
            onHand = physical.Balance.OnHand.ToString(CultureInfo.InvariantCulture), reserved = physical.Balance.Reserved.ToString(CultureInfo.InvariantCulture),
            valuePackages = value["remainingPackages"].AsInt64.ToString(CultureInfo.InvariantCulture), remainingValue = value["remainingValue"].AsString,
            financialDocumentsSha256 = Convert.ToHexString(SHA256.HashData((await FinancialDocuments(data)).ToBson())).ToLowerInvariant() };
    }
    private static async Task<BsonDocument> FinancialDocuments(GenuineFiniteDispatchTestData data) {
        var result = new BsonDocument(); foreach (var name in new[] { "invoices", "cashMovements", "paymentAllocations", "issuedDocuments" })
            result[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    private static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run the browser proof from the authoritative checkout");
    }
}
