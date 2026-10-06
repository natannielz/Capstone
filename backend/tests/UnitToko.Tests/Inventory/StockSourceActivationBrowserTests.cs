using System.Diagnostics;
using System.Globalization;
using System.Net;
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
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;
using Xunit.Abstractions;
using ActivationData = UnitToko.Tests.Inventory.StockSourceActivationTests.Data;
using B = UnitToko.Platform.Inventory.StockSourceActivationBson;

namespace UnitToko.Tests.Inventory;

/// <summary>Opt-in real HTTPS/browser/API/Mongo journey. Only provider/personnel/physical
/// attestations are synthetic; every source, policy and activation is its actual command.</summary>
[Collection("Mongo")]
public sealed class StockSourceActivationBrowserTests(MongoFixture fixture, ITestOutputHelper testOutput)
{
    [LocalStockBrowserFact]
    public async Task Browser_empty_handoff_independent_activation_history_original_key_recovery_and_unchanged_stock_money_are_durable()
    {
        var root = RepositoryRoot(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node), "Set the local Node24 executable.");
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright), "Set the pinned local Playwright package.json.");
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")), "Build the actual full production frontend first.");
        await using var data = await ActivationData.CreateAsync(fixture);
        // Preserve the original fixture source. A reviewed ten-minute observation
        // policy permits a multi-account browser rehearsal; the five-minute MFA gate stays unchanged.
        var input = new StockSourceSetupInput(StockSourceSetupService.StockCountLocation(CatalogAuthoringTestData.Warehouse),
            "synthetic-browser-empty-activation", "Synthetic browser empty WebLedger", StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger,
            600000, 0, null, null, "Synthetic discrepancy owner", data.Handoff().Reference);
        var setup = await data.Sources.ProposeAsync(data.Maker, input, ActivationData.Meta("browser-source-setup"));
        var accepted = await data.Sources.DecideAsync(data.Checker, setup["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Synthetic independent browser source review", ActivationData.Meta("browser-source-setup-approved"));
        data.PoolId = accepted["poolId"].AsInt64; await data.ApprovePolicyAsync("browser-source-policy");
        var foreign = await data.Catalog.ActorAsync(102400, new(UnitToko.Modules.Organizations.GrantScopeKind.FulfillmentWarehouse,
            100, ShopId: 201, WarehouseId: 202, ChannelId: 203), Permissions.StockSourceRead, Permissions.StockSourcePropose, Permissions.StockSourceApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Catalog.Database, data.Catalog.Clock, foreign.UserId);
        data.Catalog.Clock.UseSystemClock = true;
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Catalog.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var options = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        foreach (var (id, account) in new[] { (data.Maker.UserId, "maker"), (data.Checker.UserId, "reviewer"), (foreign.UserId, "foreign") })
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update
                .Set("issuer", options.Issuer).Set("subject", "auth0|source-activation-" + account).Set("securityVersion", 1L));
        var before = await data.PhysicalAsync(); var selection = new ProviderSelection(); var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var provider = new ProviderHandlerTests.FakeProvider(options, new SystemClock());
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.Configure<OpenIdConnectOptions>(Auth0Options.Scheme, value => value.Backchannel = new HttpClient(provider, disposeHandler: false));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Catalog.Clock);
            services.AddSingleton<IStartupFilter>(new ObserveProviderChallenge(provider, options.Issuer, selection));
            services.AddSingleton<IStartupFilter>(new FixtureControl(data, controlKey, selection));
        }));
        factory.UseKestrel(value => value.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        Assert.Equal("Production", factory.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        var address = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        Assert.Equal("127.0.0.1", new Uri(address).Host);
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko source activation rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(options.PublicOrigin).Host); names.AddDnsName(new Uri(options.Issuer).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); var prefix = "source-activation-" + Guid.NewGuid().ToString("N");
        var configuration = new { apiAddress = address, origin = options.PublicOrigin, issuer = options.Issuer, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)),
            password, playwrightPackage = playwright, makerId = data.Maker.UserId.ToString(CultureInfo.InvariantCulture), reviewerId = data.Checker.UserId.ToString(CultureInfo.InvariantCulture),
            foreignId = foreign.UserId.ToString(CultureInfo.InvariantCulture), poolId = data.PoolId.ToString(CultureInfo.InvariantCulture), handoffReference = input.HandoffReference, controlKey, proofPrefix = prefix };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-stock-source-activation-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start synthetic local browser rehearsal.");
        try {
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(configuration)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6)); testOutput.WriteLine(await output); testOutput.WriteLine(await errors);
            Assert.True(process.ExitCode == 0, $"Synthetic browser rehearsal failed: {await output}; {await errors}");
            Assert.Contains("LOCAL_SYNTHETIC_SOURCE_ACTIVATION_BROWSER_PASS", await output);
        } finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(provider.SawValidPkce); Assert.Equal(3, provider.TokenRequests);
        var proposal = Assert.Single(await data.Rows(B.Proposals).Find(new BsonDocument()).ToListAsync());
        var decision = Assert.Single(await data.Rows(B.Decisions).Find(new BsonDocument()).ToListAsync());
        var activation = Assert.Single(await data.Rows(B.Activations).Find(new BsonDocument()).ToListAsync());
        Assert.Equal(data.PoolId, proposal["poolId"].AsInt64); Assert.Equal(data.Maker.UserId, proposal["actorId"].AsInt64);
        Assert.Equal(data.Checker.UserId, decision["actorId"].AsInt64); Assert.Equal("approve", decision["decision"].AsString);
        Assert.Equal(proposal["_id"], activation["_id"]); Assert.Equal(decision["_id"], activation["decisionId"]); Assert.True((await data.PoolAsync())["active"].AsBoolean);
        foreach (var operation in new[] { B.Propose, B.Decide }) {
            Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", operation)));
            Assert.Equal(1L, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("operation", operation)));
            Assert.Equal(1L, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", operation)));
        }
        var after = await data.PhysicalAsync(); Assert.Equal(before, after);
        var service = new StockSourceSetupService(data.Catalog.Database, data.Unit, new(data.Catalog.Database), data.Catalog.Clock);
        // Reuse the actor/session actually authenticated by the browser API, never the stale native fixture session.
        var currentChecker = Assert.IsType<ActorContext>(selection.CheckerActor);
        Assert.Equal("activated", (await service.ReadActivationAsync(currentChecker, data.PoolId, proposal["_id"].AsInt64))!["status"].AsString);
        using (var tx = await fixture.Client.StartSessionAsync()) { tx.StartTransaction(); await service.RequireActivatedPoolAsync(tx, await data.PoolAsync(), default); await tx.AbortTransactionAsync(); }
        var encryptedKeys = Directory.GetFiles(keys.Directory, "key-*.xml"); Assert.NotEmpty(encryptedKeys);
        Assert.All(encryptedKeys, path => Assert.Contains("encryptedSecret", File.ReadAllText(path)));
        var directory = Path.Combine(root, "tmp", "production", "source-activation-browser-results"); Directory.CreateDirectory(directory);
        var proof = new { recordedUtc = DateTimeOffset.UtcNow, proposalCount = 1, decisionCount = 1, activationCount = 1, actualMongoConfirmed = true,
            actualPoolActive = true, physicalBeforeSha256 = Hash(before), physicalAfterSha256 = Hash(after), durableSha256 = Hash(await Durable(data)),
            unchangedStockAndMoney = true, localSyntheticOnly = true, externalProviderAccepted = false, stagingValidated = false, productionAccepted = false };
        var proofPath = Path.Combine(directory, prefix + "-mongo.json"); await File.WriteAllTextAsync(proofPath, JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
        testOutput.WriteLine("LOCAL_SYNTHETIC_SOURCE_ACTIVATION_MONGO_PASS " + proofPath);
    }
    private sealed class ProviderSelection { internal string Account = "maker"; internal ActorContext? CheckerActor; }
    private sealed class ObserveProviderChallenge(ProviderHandlerTests.FakeProvider provider, string issuer, ProviderSelection selection) : IStartupFilter {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use((context, nextRequest) => { context.Response.OnStarting(() => {
                var location = context.Response.Headers.Location.ToString();
                if (location.StartsWith(issuer + "authorize?", StringComparison.Ordinal)) {
                    provider.Challenge = QueryHelpers.ParseQuery(new Uri(location).Query).ToDictionary(item => item.Key, item => item.Value.ToString());
                    provider.Subject = "auth0|source-activation-" + selection.Account; provider.Email = "source-activation-" + selection.Account + "@company.co.id";
                } return Task.CompletedTask;
            }); return nextRequest(); }); next(app);
        };
    }
    private sealed class FixtureControl(ActivationData data, string key, ProviderSelection selection) : IStartupFilter {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use(async (context, nextRequest) => {
                if (!context.Request.Path.StartsWithSegments("/__fixture/source-activation")) {
                    await nextRequest();
                    if (context.Response.StatusCode == 200 && context.Request.Path.StartsWithSegments("/api/v1/inventory/sources") &&
                        context.Features.Get<ResolvedActor>()?.Actor is { } actor && actor.UserId == data.Checker.UserId) selection.CheckerActor = actor;
                    return;
                }
                if (context.Request.Method != "POST" || context.Request.Headers["X-Fixture-Control"] != key) { context.Response.StatusCode = 404; return; }
                var action = context.Request.Path.Value!["/__fixture/source-activation/".Length..];
                if (action is "maker" or "reviewer" or "foreign") selection.Account = action;
                else if (action is "same-person" or "distinct-people") await PersonTestData.SetEquivalenceAsync(data.Unit, data.Catalog.Database,
                    data.Maker.UserId, data.Checker.UserId, action == "same-person", context.RequestAborted);
                else if (action == "state") { await context.Response.WriteAsJsonAsync(await State(data)); return; }
                else { context.Response.StatusCode = 404; return; } context.Response.StatusCode = 204;
            }); next(app);
        };
    }
    private static async Task<object> State(ActivationData data) {
        var proposals = await data.Rows(B.Proposals).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var decisions = await data.Rows(B.Decisions).Find(new BsonDocument()).ToListAsync();
        return new { proposals = proposals.Select(proposal => new { id = proposal["_id"].AsInt64.ToString(CultureInfo.InvariantCulture),
            status = decisions.SingleOrDefault(row => row["proposalId"] == proposal["_id"]) is { } decision ? decision["decision"] == "approve" ? "activated" : "rejected" : "proposed" }).ToArray(),
            proposalCount = proposals.Count, decisionCount = decisions.Count, activationCount = await data.CountAsync(B.Activations), poolActive = (await data.PoolAsync())["active"].AsBoolean,
            durableSha256 = Hash(await Durable(data)), physicalSha256 = Hash(await data.PhysicalAsync()) };
    }
    private static async Task<BsonDocument> Durable(ActivationData data) {
        var result = new BsonDocument();
        foreach (var name in new[] { B.Proposals, B.Decisions, B.Activations, "idempotencyRecords", "auditEvents", "outboxEvents" }) {
            var field = name == "idempotencyRecords" || name == "auditEvents" ? "operation" : "kind";
            var filter = name is "idempotencyRecords" or "auditEvents" or "outboxEvents" ? new BsonDocument(field, new BsonDocument("$in", new BsonArray { B.Propose, B.Decide })) : new BsonDocument();
            result[name] = new BsonArray(await data.Rows(name).Find(filter).Sort(new BsonDocument("_id", 1)).ToListAsync());
        } return result;
    }
    private static string Hash(BsonDocument document) => Convert.ToHexString(SHA256.HashData(document.ToBson())).ToLowerInvariant();
    private static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "tests", "UnitToko.Tests", "UnitToko.Tests.csproj"))) return directory.FullName;
        throw new InvalidOperationException("Run the local browser rehearsal from the authoritative repository.");
    }
}
