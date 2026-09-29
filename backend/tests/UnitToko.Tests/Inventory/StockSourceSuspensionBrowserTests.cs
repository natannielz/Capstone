using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Xunit.Abstractions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Inventory;

/// <summary>Real HTTPS browser/API/Mongo rehearsal with synthetic accounts and provider keys only.</summary>
[Collection("Mongo")]
public sealed class StockSourceSuspensionBrowserTests(MongoFixture fixture, ITestOutputHelper testOutput)
{
    [LocalStockBrowserFact]
    public async Task Browser_suspension_command_only_confirmation_recovery_history_and_MFA_use_real_API_and_Mongo()
    {
        var root = RepositoryRoot();
        var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node), "Set UNITTOKO_BROWSER_NODE to the local Node 24 executable.");
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright), "Set UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE to the installed Playwright package.json.");
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")), "Build the production frontend first.");
        await using var data = await StoreTestData.CreateAsync(fixture);
        data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1";
        settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        var maker = await data.AddActorAsync(49001, CatalogAuthoringTestData.Warehouse, Permissions.StockSourcePropose);
        var reviewer = await data.AddActorAsync(49002, CatalogAuthoringTestData.Warehouse, Permissions.StockSourceApprove);
        var stopper = await data.AddActorAsync(9007199254740993, CatalogAuthoringTestData.Warehouse, Permissions.StockSourceSuspend);
        var reader = await data.AddActorAsync(49003, CatalogAuthoringTestData.Warehouse, Permissions.StockSourceRead);
        foreach (var (actor, subject) in new[] { (stopper, "auth0|source-stopper"), (reader, "auth0|source-reader") })
            await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
                Builders<BsonDocument>.Update.Set("issuer", configured.Issuer).Set("subject", subject).Set("securityVersion", 1L));
        var requested = new StockLocation(new(100), new(101), new(102), new(104));
        await data.Database.GetCollection<BsonDocument>("authorizationScopes").InsertOneAsync(
            AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)));
        await data.AddGrantAsync(maker, 49101, requested.ToScope(), [Permissions.StockSourcePropose]);
        await data.AddGrantAsync(reviewer, 49102, requested.ToScope(), [Permissions.StockSourceApprove]);
        await data.AddGrantAsync(stopper, 49104, requested.ToScope(), [Permissions.StockSourceSuspend]);
        await data.AddGrantAsync(reader, 49103, requested.ToScope(), [Permissions.StockSourceRead]);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId, stopper.UserId, reader.UserId);
        var service = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        var location = new StockLocation(new(100), new(101), new(102), new(103));
        var input = new StockSourceSetupInput(location, "browser-suspension", "Alokasi penghentian uji", StockAuthorityMode.WebLedger,
            StockPoolControl.SharedWebLedger, 60001, 9007199254740993, null, null, "Synthetic owner", "Synthetic handoff");
        var created = await service.ProposeAsync(maker, input, new("fixture-create", "Synthetic original source", "browser-suspension"));
        var approved = await service.DecideAsync(reviewer, created["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Synthetic original review", new("fixture-approve", "Synthetic original source", "browser-suspension"));
        var poolId = approved["poolId"].AsInt64;
        var alias = await service.ProposeAliasAsync(maker, poolId, 1, requested, 0, true, new("fixture-alias", "Synthetic shared channel", "browser-suspension"));
        await service.DecideAliasAsync(reviewer, alias["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Synthetic mapping review", new("fixture-alias-approve", "Synthetic shared channel", "browser-suspension"));
        await data.Database.GetCollection<BsonDocument>("idCounters").UpdateOneAsync(new BsonDocument("_id", "stockSourceSuspensions"),
            Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        await InventoryGraphTests.InstallBalances(data);
        await data.Database.GetCollection<BsonDocument>("lots").InsertOneAsync(InventoryGraphTests.Lot());
        await data.Database.GetCollection<BsonDocument>("stockBalances").InsertOneAsync(InventoryBson.Balance(new InventoryBalanceDocument(7000, poolId,
            new(new(location, new(200), new(201), new(202)), 9007199254740993, 2, 3, 4, 1))));
        var preserved = await InvariantState(data);
        var clock = new BrowserClock(); var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var provider = new ProviderHandlerTests.FakeProvider(configured, new SystemClock());
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(Auth0Options.Scheme, options => options.Backchannel = new HttpClient(provider, disposeHandler: false));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(clock);
            services.AddSingleton<IStartupFilter>(new ObserveProviderChallenge(provider, configured.Issuer));
            services.AddSingleton<IStartupFilter>(new FixtureControl(data, clock, controlKey, service, maker, reviewer, stopper.UserId, poolId, input));
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        Assert.Equal("Production", factory.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        Assert.Equal("127.0.0.1", new Uri(apiAddress).Host);
        using var rsa = RSA.Create(2048);
        var certificate = new CertificateRequest("CN=UnitToko browser rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(configured.PublicOrigin).Host); names.AddDnsName(new Uri(configured.Issuer).Host);
        certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var browserInput = new { apiAddress, origin = configured.PublicOrigin, issuer = configured.Issuer,
            pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            actorId = stopper.UserId.ToString(), readerId = reader.UserId.ToString(), poolId = poolId.ToString(), controlKey };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-stock-source-suspension-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start browser rehearsal.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(browserInput)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5)); testOutput.WriteLine(await output); testOutput.WriteLine(await errors);
            Assert.True(process.ExitCode == 0, $"Browser rehearsal failed: {await output}; {await errors}");
            Assert.Contains("REAL_SOURCE_SUSPENSION_BROWSER_PASS", await output);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(provider.SawValidPkce); Assert.Equal(3, provider.TokenRequests);
        var stops = await data.Database.GetCollection<BsonDocument>("stockSourceSuspensions").Find(new BsonDocument()).Sort("{suspensionRevision:1}").ToListAsync();
        Assert.Equal(2, stops.Count); Assert.Equal(new long[] { 1, 2 }, stops.Select(row => row["suspensionRevision"].AsInt64));
        Assert.Equal(new long[] { 1, 2 }, stops.Select(row => row["poolRevision"].AsInt64));
        Assert.All(stops, row => { Assert.Equal(stopper.UserId, row["suspendedBy"].AsInt64); Assert.True(row["_id"].AsInt64 > 9007199254740992L); });
        var pool = Assert.Single(await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument()).ToListAsync());
        Assert.False(pool["active"].AsBoolean); Assert.Equal(3L, pool["revision"].AsInt64);
        foreach (var collection in new[] { "auditEvents", "idempotencyRecords" })
            Assert.Equal(2, await data.Database.GetCollection<BsonDocument>(collection).CountDocumentsAsync(new BsonDocument("operation", "stock.source.suspend")));
        Assert.Equal(preserved, await InvariantState(data));
        // This fixture changes identity authority directly. Reads reject its old grant version without
        // rewriting session rows or deleting a cookie that may have rotated in another tab.
        var retained = Assert.Single(await data.Database.GetCollection<BsonDocument>("identitySessions")
            .Find(new BsonDocument("status", "active")).ToListAsync());
        Assert.Equal(stopper.UserId, retained["userId"].AsInt64);
        var disabledIdentity = await data.Database.GetCollection<BsonDocument>("identities")
            .Find(new BsonDocument("_id", stopper.UserId)).SingleAsync();
        Assert.Equal("suspended", disabledIdentity["status"].AsString);
        Assert.True(retained["grantVersion"].AsInt64 < disabledIdentity["grantVersion"].AsInt64);
        var persistedKeys = Directory.GetFiles(keys.Directory, "key-*.xml"); Assert.NotEmpty(persistedKeys);
        Assert.All(persistedKeys, path => Assert.Contains("encryptedSecret", File.ReadAllText(path)));
    }
    private sealed class BrowserClock : IClock
    {
        private long offsetTicks;
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offsetTicks));
        public void ExpireFactor() => Interlocked.Exchange(ref offsetTicks, TimeSpan.FromMinutes(6).Ticks);
    }
    private sealed class FixtureControl(StoreTestData data, BrowserClock clock, string key, StockSourceSetupService service,
        ActorContext maker, ActorContext reviewer, long stopperId, long poolId, StockSourceSetupInput initial) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextRequest) =>
            {
                if (!context.Request.Path.StartsWithSegments("/__fixture/suspension")) { await nextRequest(); return; }
                if (context.Request.Method != "POST" || context.Request.Headers["X-Fixture-Control"] != key) { context.Response.StatusCode = 404; return; }
                if (context.Request.Path == "/__fixture/suspension/mfa") clock.ExpireFactor();
                else if (context.Request.Path == "/__fixture/suspension/grant")
                    await data.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 49103L),
                        Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
                else if (context.Request.Path == "/__fixture/suspension/account")
                    await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", stopperId),
                        Builders<BsonDocument>.Update.Set("status", "suspended").Inc("grantVersion", 1L));
                else if (context.Request.Path == "/__fixture/suspension/settings")
                {
                    data.Clock.UtcNow = clock.UtcNow;
                    var pool = await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument("_id", poolId)).SingleAsync();
                    var revision = pool["revision"].AsInt64;
                    var proposal = await service.ProposeRevisionAsync(maker, poolId, revision, initial with { DiscrepancyOwnerReference = "Synthetic revised owner " + revision },
                        new("fixture-settings-" + revision, "Synthetic settings change", "browser-suspension"));
                    await service.DecideRevisionAsync(reviewer, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
                        "Synthetic settings review", new("fixture-settings-approve-" + revision, "Synthetic settings change", "browser-suspension"));
                }
                else { context.Response.StatusCode = 404; return; }
                context.Response.StatusCode = 204;
            }); next(app);
        };
    }
    private sealed class ObserveProviderChallenge(ProviderHandlerTests.FakeProvider provider, string issuer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextRequest) =>
            {
                context.Response.OnStarting(() =>
                {
                    var location = context.Response.Headers.Location.ToString();
                    if (location.StartsWith(issuer + "authorize?", StringComparison.Ordinal))
                    {
                        provider.Challenge = QueryHelpers.ParseQuery(new Uri(location).Query).ToDictionary(item => item.Key, item => item.Value.ToString());
                        provider.Subject = provider.TokenRequests < 2 ? "auth0|source-stopper" : "auth0|source-reader";
                        provider.Email = provider.TokenRequests < 2 ? "source-stopper@company.co.id" : "source-reader@company.co.id";
                    }
                    return Task.CompletedTask;
                }); return nextRequest();
            }); next(app);
        };
    }
    private static async Task<BsonDocument> InvariantState(StoreTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "stockSourceProposals", "stockSourceDecisions", "inventoryPoolRegistrations", "stockSourceAliases", "stockSourceAliasProposals",
            "stockSourceAliasDecisions", "lots", "stockBalances", "movements", "reservations", "journalEntries" })
            result[name] = new BsonArray(await data.Database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).ToListAsync());
        return result;
    }
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) &&
                File.Exists(Path.Combine(directory.FullName, "backend", "tests", "UnitToko.Tests", "UnitToko.Tests.csproj"))) return directory.FullName;
        throw new InvalidOperationException("Browser rehearsal must run from the repository checkout.");
    }
}
