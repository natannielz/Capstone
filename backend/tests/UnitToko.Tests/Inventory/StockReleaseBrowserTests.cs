using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Authorization;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;
using Xunit.Abstractions;
using ReleaseData = UnitToko.Tests.Inventory.StockReleaseServiceTests.Data;
using B = UnitToko.Platform.Inventory.StockReleaseBson;

namespace UnitToko.Tests.Inventory;

/// <summary>Opt-in local synthetic signed OIDC/cookie/browser/API/Mongo proof.
/// Actual business owners activate before private receipt and post quality/value; no raw active flag or release proof is seeded.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class StockReleaseBrowserTests(MongoFixture fixture, ITestOutputHelper testOutput)
{
    [LocalStockBrowserFact]
    public async Task Browser_explicit_selection_independent_release_lost_reply_original_key_recovery_and_exact_stock_value_are_durable()
    {
        using var timing = new StockReleaseTestDiagnostics(testOutput);
        var root = RepositoryRoot(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node), "Set the local Node24 executable.");
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright), "Set the pinned local Playwright package.json.");
        var buildIdPath = Path.Combine(root, "frontend", ".next-full", "BUILD_ID"); Assert.True(File.Exists(buildIdPath), "Build the actual full production frontend first.");
        await using var data = await ReleaseData.CreateAsync(fixture, stage => testOutput.WriteLine("FIXTURE " + stage), useSystemClock: true);
        var original = await Preserved(data); var beforeBalance = await data.BalanceAsync();
        Assert.Equal(3L, beforeBalance["held"].AsInt64); Assert.Equal(3L, beforeBalance["onHand"].AsInt64);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1";
        settings["Logging:LogLevel:Default"] = "Warning";
        var options = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        foreach (var (actor, account) in new[] { (data.Maker, "maker"), (data.Checker, "reviewer"), (data.Outsider, "foreign") })
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update
                .Set("issuer", options.Issuer).Set("subject", "auth0|stock-release-" + account).Set("securityVersion", 1L));
        var selection = new ProviderSelection(); var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var provider = new ProviderHandlerTests.FakeProvider(options, new SystemClock());
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.Configure<OpenIdConnectOptions>(Auth0Options.Scheme, value => value.Backchannel = new HttpClient(provider, disposeHandler: false));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<StockReleaseService>(); services.AddSingleton(data.NewService());
            services.AddSingleton<IStartupFilter>(new ObserveProviderChallenge(provider, options.Issuer, selection));
            services.AddSingleton<IStartupFilter>(new FixtureControl(data, controlKey, selection));
        }));
        factory.UseKestrel(value => value.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        Assert.Equal("Production", factory.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        Assert.Equal("127.0.0.1", new Uri(apiAddress).Host);
        using var rsa = RSA.Create(2048);
        var certificate = new CertificateRequest("CN=UnitToko stock release browser rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(options.PublicOrigin).Host); names.AddDnsName(new Uri(options.Issuer).Host);
        certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); var prefix = "stock-release-" + Guid.NewGuid().ToString("N");
        var input = new { apiAddress, origin = options.PublicOrigin, issuer = options.Issuer, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, makerId = data.Maker.UserId.ToString(CultureInfo.InvariantCulture), reviewerId = data.Checker.UserId.ToString(CultureInfo.InvariantCulture),
            foreignId = data.Outsider.UserId.ToString(CultureInfo.InvariantCulture), receiptId = data.Valuation.ReceiptId.ToString(CultureInfo.InvariantCulture),
            qualityAllocationId = data.QualityAllocationId.ToString(CultureInfo.InvariantCulture), valuationAllocationId = data.ValueAllocationId.ToString(CultureInfo.InvariantCulture),
            policy = data.Input().Policy, buildId = (await File.ReadAllTextAsync(buildIdPath)).Trim(), controlKey, proofPrefix = prefix };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-stock-release-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start local synthetic release browser rehearsal.");
        try {
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6)); testOutput.WriteLine(await output); testOutput.WriteLine(await errors);
            Assert.True(process.ExitCode == 0, $"Local synthetic release browser failed: {await output}; {await errors}");
            Assert.Contains("LOCAL_SYNTHETIC_STOCK_RELEASE_BROWSER_PASS", await output);
        } finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(provider.SawValidPkce); Assert.Equal(3, provider.TokenRequests);
        var persistedKeys = Directory.GetFiles(keys.Directory, "key-*.xml"); Assert.NotEmpty(persistedKeys);
        Assert.All(persistedKeys, path => Assert.Contains("encryptedSecret", File.ReadAllText(path)));
        var makerActor = selection.MakerActor ?? throw new InvalidOperationException("Actual authenticated maker was not observed");
        var checkerActor = selection.CheckerActor ?? throw new InvalidOperationException("Actual authenticated reviewer was not observed");
        Assert.NotNull(makerActor.Session); Assert.NotNull(checkerActor.Session);
        var proposal = Assert.Single(await data.Rows(B.Proposals).Find(new BsonDocument()).ToListAsync());
        var decision = Assert.Single(await data.Rows(B.Decisions).Find(new BsonDocument()).ToListAsync());
        Assert.Equal(data.Maker.UserId, proposal["actorId"].AsInt64); Assert.Equal(data.Checker.UserId, decision["actorId"].AsInt64);
        Assert.Equal(1L, proposal["input"]["packages"].AsInt64); Assert.Equal("approve", decision["decision"].AsString);
        Assert.Equal("11250.00", decision["transferredValue"].AsString);
        var balance = await data.BalanceAsync(); Assert.Equal(3L, balance["onHand"].AsInt64); Assert.Equal(2L, balance["held"].AsInt64); Assert.Equal(0L, balance["reserved"].AsInt64);
        Assert.Equal(1L, await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", B.Namespace)));
        Assert.Equal(1L, await data.Rows(LotValueStore.Transitions).CountDocumentsAsync(new BsonDocument()));
        var held = await data.Rows(LotValueStore.HeldHeads).Find(new BsonDocument("_id", data.ValueAllocationId)).SingleAsync();
        var available = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", balance["_id"])).SingleAsync();
        Assert.Equal(1L, held["remainingPackages"].AsInt64); Assert.Equal("11250.00", held["remainingValue"].AsString);
        Assert.Equal(1L, available["remainingPackages"].AsInt64); Assert.Equal("11250.00", available["remainingValue"].AsString);
        foreach (var operation in new[] { B.Propose, B.Decide }) {
            Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", operation)));
            foreach (var collection in new[] { "auditEvents", "outboxEvents" }) Assert.Equal(1L, await data.Rows(collection).CountDocumentsAsync(new BsonDocument("kind", operation)));
        }
        // StartupFilter captures the actual authenticated actor after the OIDC identity
        // update; the original pre-login fixture context is never reused as authority.
        Assert.Equal("approved", (await data.Service.RecoverAsync(checkerActor, B.Decide, decision["requestKey"].AsString))!["status"].AsString);
        var after = await Preserved(data); Assert.True(original.ToBson().SequenceEqual(after.ToBson()), "Original acquisition/journal/payable state changed");
        var directory = Path.Combine(root, "tmp", "production", "stock-release-browser-results"); Directory.CreateDirectory(directory);
        var proof = new { recordedUtc = DateTimeOffset.UtcNow, input.buildId, proposalCount = 1, decisionCount = 1, movementCount = 1, valueTransitionCount = 1,
            heldBefore = 3, heldAfter = 2, availableAfter = 1, transferredValue = "11250.00", durableSha256 = Hash(await Durable(data)),
            originalBeforeSha256 = Hash(original), originalAfterSha256 = Hash(after), originalAcquisitionJournalPayableUnchanged = true,
            actualAuthenticatedActorReused = true, localSyntheticOnly = true, externalProviderAccepted = false, stagingValidated = false, productionAccepted = false };
        var proofPath = Path.Combine(directory, prefix + "-mongo.json"); await File.WriteAllTextAsync(proofPath, JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
        testOutput.WriteLine("LOCAL_SYNTHETIC_STOCK_RELEASE_MONGO_PASS " + proofPath);
    }
    private sealed class ProviderSelection { internal string Account = "maker"; internal ActorContext? MakerActor, CheckerActor; }
    private sealed class ObserveProviderChallenge(ProviderHandlerTests.FakeProvider provider, string issuer, ProviderSelection selection) : IStartupFilter {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use((context, nextRequest) => { context.Response.OnStarting(() => {
                var location = context.Response.Headers.Location.ToString();
                if (location.StartsWith(issuer + "authorize?", StringComparison.Ordinal)) {
                    provider.Challenge = QueryHelpers.ParseQuery(new Uri(location).Query).ToDictionary(item => item.Key, item => item.Value.ToString());
                    provider.Subject = "auth0|stock-release-" + selection.Account; provider.Email = "stock-release-" + selection.Account + "@company.co.id";
                }
                return Task.CompletedTask;
            }); return nextRequest(); }); next(app);
        };
    }
    private sealed class FixtureControl(ReleaseData data, string key, ProviderSelection selection) : IStartupFilter {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use(async (context, nextRequest) => {
                if (!context.Request.Path.StartsWithSegments("/__fixture/stock-release")) {
                    await nextRequest();
                    if (context.Response.StatusCode == 200 && context.Request.Path.StartsWithSegments("/api/v1/inventory/stock-releases") && context.Features.Get<ResolvedActor>()?.Actor is { } actor) {
                        if (actor.UserId == data.Maker.UserId) selection.MakerActor = actor;
                        if (actor.UserId == data.Checker.UserId) selection.CheckerActor = actor;
                    }
                    return;
                }
                if (context.Request.Method != "POST" || context.Request.Headers["X-Fixture-Control"] != key) { context.Response.StatusCode = 404; return; }
                var action = context.Request.Path.Value!["/__fixture/stock-release/".Length..];
                if (action is "maker" or "reviewer" or "foreign") selection.Account = action;
                else if (action is "same-person" or "distinct-people") await PersonTestData.SetEquivalenceAsync(data.Unit, data.Database,
                    data.Maker.UserId, data.Checker.UserId, action == "same-person", context.RequestAborted);
                else if (action == "state") { await context.Response.WriteAsJsonAsync(await State(data)); return; }
                else { context.Response.StatusCode = 404; return; }
                context.Response.StatusCode = 204;
            }); next(app);
        };
    }
    private static async Task<object> State(ReleaseData data) {
        var proposals = await data.Rows(B.Proposals).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var decisions = await data.Rows(B.Decisions).Find(new BsonDocument()).ToListAsync(); var balance = await data.BalanceAsync();
        var available = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", balance["_id"])).SingleOrDefaultAsync();
        return new { proposals = proposals.Select(proposal => new { id = proposal["_id"].AsInt64.ToString(CultureInfo.InvariantCulture),
                status = decisions.SingleOrDefault(row => row["proposalId"] == proposal["_id"]) is { } decision ? decision["decision"] == "approve" ? "approved" : "rejected" : "proposed" }).ToArray(),
            proposalCount = proposals.Count, decisionCount = decisions.Count, movementCount = await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", B.Namespace)),
            valueTransitionCount = await data.Rows(LotValueStore.Transitions).CountDocumentsAsync(new BsonDocument()), onHand = balance["onHand"].AsInt64.ToString(CultureInfo.InvariantCulture),
            held = balance["held"].AsInt64.ToString(CultureInfo.InvariantCulture), availablePackages = available is null ? "0" : available["remainingPackages"].AsInt64.ToString(CultureInfo.InvariantCulture),
            availableValue = available is null ? "0.00" : available["remainingValue"].AsString, durableSha256 = Hash(await Durable(data)), originalSha256 = Hash(await Preserved(data)) };
    }
    private static async Task<BsonDocument> Durable(ReleaseData data) {
        var result = new BsonDocument();
        foreach (var name in new[] { B.Proposals, B.Decisions, "stockBalances", "movements", LotValueStore.HeldHeads, LotValueStore.AvailableHeads, LotValueStore.Transitions })
            result[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        foreach (var (name, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            result[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument(field, new BsonDocument("$in", new BsonArray { B.Propose, B.Decide })))
                .Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    private static async Task<BsonDocument> Preserved(ReleaseData data) {
        var result = new BsonDocument { { "valuation", await data.Valuation.StateAsync() }, { "documents", await data.Valuation.Documents.StateAsync() },
            { "inspection", await data.Valuation.Inspection.StateAsync() }, { "receiving", await data.Valuation.Inspection.Receiving.StateAsync() },
            { "orders", await data.Valuation.Inspection.Receiving.Orders.StateAsync() } };
        foreach (var name in new[] { "procurementStockReceipts", "lots", "procurementSupplierPayableProposals", "procurementSupplierPayableDecisions", "procurementSupplierPayableAllocations",
            "cashMovements", "paymentAllocations", "procurementFundingAllocations", "procurementDisbursements", "procurementSettlements", "reservations" })
            result[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    private static string Hash(BsonDocument row) => Convert.ToHexString(SHA256.HashData(row.ToBson())).ToLowerInvariant();
    private static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Browser rehearsal must run from the authoritative checkout.");
    }
}
