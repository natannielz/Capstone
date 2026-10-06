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

/// <summary>Real local HTTPS/Next/API/Mongo policy workflow using synthetic identities and provider keys.</summary>
[Collection("Mongo")]
public sealed class StockSourcePolicyBrowserTests(MongoFixture fixture, ITestOutputHelper testOutput)
{
    [LocalStockBrowserFact]
    public async Task Browser_policy_scoped_tasks_four_recoveries_frozen_decisions_and_private_history_use_real_API_and_Mongo()
    {
        var root = RepositoryRoot();
        var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node), "Set the local Node 24 executable.");
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright), "Install the pinned browser package.");
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")), "Build the production frontend first.");
        await using var data = await StoreTestData.CreateAsync(fixture);
        data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1";
        settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        var maker = await data.AddActorAsync(49001, CatalogAuthoringTestData.Warehouse, Permissions.StockSourcePropose, Permissions.StockSourcePolicyPropose);
        var checker = await data.AddActorAsync(49002, CatalogAuthoringTestData.Warehouse, Permissions.StockSourceApprove, Permissions.StockSourcePolicyApprove);
        var reader = await data.AddActorAsync(9007199254740993, CatalogAuthoringTestData.Warehouse, Permissions.StockSourceRead);
        var stopper = await data.AddActorAsync(49004, CatalogAuthoringTestData.Warehouse, Permissions.StockSourceSuspend);
        foreach (var (actor, subject) in new[] { (maker, "maker"), (checker, "checker"), (reader, "reader") })
            await Rows(data, "identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
                Builders<BsonDocument>.Update.Set("issuer", configured.Issuer).Set("subject", "auth0|policy-" + subject).Set("securityVersion", 1L));
        maker = maker with { ProviderIssuer = configured.Issuer, ProviderSubject = "auth0|policy-maker" };
        checker = checker with { ProviderIssuer = configured.Issuer, ProviderSubject = "auth0|policy-checker" };
        var location = new StockLocation(new(100), new(101), new(102), new(103));
        var linked = location with { ChannelId = new(104) };
        await Rows(data, "authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)));
        await data.AddGrantAsync(maker, 49101, linked.ToScope(), [Permissions.StockSourcePropose, Permissions.StockSourcePolicyPropose]);
        await data.AddGrantAsync(checker, 49102, linked.ToScope(), [Permissions.StockSourceApprove, Permissions.StockSourcePolicyApprove]);
        await data.AddGrantAsync(reader, 49103, linked.ToScope(), [Permissions.StockSourceRead]);
        await data.AddGrantAsync(stopper, 49104, linked.ToScope(), [Permissions.StockSourceSuspend]);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, checker.UserId, reader.UserId, stopper.UserId);
        foreach (var name in new[] { "inventoryPools", "stockSourcePolicyProposals", "stockSourcePolicyDecisions", "stockSourcePolicies" })
            await Rows(data, "idCounters").UpdateOneAsync(new BsonDocument("_id", name),
                Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        var service = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        var input = new StockSourceSetupInput(location, "browser-policy", "Alokasi kebijakan uji", StockAuthorityMode.WebLedger,
            StockPoolControl.SharedWebLedger, 60001, 9007199254740993, null, null, "Synthetic owner", "Synthetic handoff");
        var created = await service.ProposeAsync(maker, input, new("fixture-create", "Synthetic source", "browser-policy"));
        var approved = await service.DecideAsync(checker, created["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Synthetic source review", new("fixture-approve", "Synthetic source", "browser-policy"));
        var poolId = approved["poolId"].AsInt64;
        var alias = await service.ProposeAliasAsync(maker, poolId, 1, linked, 0, true, new("fixture-alias", "Synthetic channel", "browser-policy"));
        await service.DecideAliasAsync(checker, alias["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Synthetic mapping review", new("fixture-alias-approve", "Synthetic channel", "browser-policy"));
        foreach (var (actor, permission) in new[] { (maker, Permissions.StockSourcePolicyPropose), (checker, Permissions.StockSourcePolicyApprove) })
            await Rows(data, "authorizationGrants").UpdateManyAsync(new BsonDocument("userId", actor.UserId),
                Builders<BsonDocument>.Update.Set("permissions", new BsonArray { permission }));
        await InventoryGraphTests.InstallBalances(data);
        await Rows(data, "lots").InsertOneAsync(InventoryGraphTests.Lot());
        await Rows(data, "stockBalances").InsertOneAsync(InventoryBson.Balance(new InventoryBalanceDocument(7000, poolId,
            new(new(location, new(200), new(201), new(202)), 9007199254740993, 2, 3, 4, 1))));
        var preserved = await InvariantState(data);
        var clock = new BrowserClock(); var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var provider = new ProviderHandlerTests.FakeProvider(configured, new SystemClock());
        var selection = new ProviderSelection();
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(Auth0Options.Scheme, options => options.Backchannel = new HttpClient(provider, disposeHandler: false));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(clock);
            services.AddSingleton<IStartupFilter>(new ObserveProviderChallenge(provider, configured.Issuer, selection));
            services.AddSingleton<IStartupFilter>(new FixtureControl(data, new(fixture.Client), clock, controlKey, service, stopper, poolId, selection));
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        Assert.Equal("Production", factory.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var rsa = RSA.Create(2048);
        var certificate = new CertificateRequest("CN=UnitToko policy browser rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(configured.PublicOrigin).Host); names.AddDnsName(new Uri(configured.Issuer).Host);
        certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var browserInput = new { apiAddress, origin = configured.PublicOrigin, issuer = configured.Issuer,
            pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            makerId = maker.UserId.ToString(), checkerId = checker.UserId.ToString(), readerId = reader.UserId.ToString(), poolId = poolId.ToString(), controlKey };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-stock-source-policy-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start browser rehearsal.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(browserInput)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6)); testOutput.WriteLine(await output); testOutput.WriteLine(await errors);
            Assert.True(process.ExitCode == 0, $"Browser rehearsal failed: {await output}; {await errors}");
            Assert.Contains("REAL_SOURCE_POLICY_BROWSER_PASS", await output);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(provider.SawValidPkce); Assert.Equal(4, provider.TokenRequests);
        Assert.Equal(4, await Rows(data, "stockSourcePolicyProposals").CountDocumentsAsync(new BsonDocument()));
        var decisions = await Rows(data, "stockSourcePolicyDecisions").Find(new BsonDocument()).ToListAsync();
        Assert.Equal(3, decisions.Count);
        Assert.All(decisions, row => Assert.True(row["_id"].AsInt64 > 9007199254740992L));
        Assert.Equal(1, await Rows(data, "stockSourcePolicies").CountDocumentsAsync(new BsonDocument()));
        var pool = await Rows(data, "inventoryPools").Find(new BsonDocument("_id", poolId)).SingleAsync();
        Assert.False(pool["active"].AsBoolean); Assert.Equal(1L, pool["revision"].AsInt64);
        foreach (var name in new[] { "auditEvents", "idempotencyRecords" })
            foreach (var (operation, count) in new[] { ("propose", 4L), ("approve", 1L), ("reject", 1L), ("withdraw", 1L) })
                Assert.Equal(count, await Rows(data, name).CountDocumentsAsync(new BsonDocument("operation", "stock.source.policy." + operation)));
        Assert.Equal(preserved, await InvariantState(data));
        var persistedKeys = Directory.GetFiles(keys.Directory, "key-*.xml"); Assert.NotEmpty(persistedKeys);
        Assert.All(persistedKeys, path => Assert.Contains("encryptedSecret", File.ReadAllText(path)));
    }
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private sealed class BrowserClock : IClock
    {
        private long offsetTicks;
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offsetTicks));
        public void ExpireFactor() => Interlocked.Exchange(ref offsetTicks, TimeSpan.FromMinutes(6).Ticks);
    }
    private sealed class ProviderSelection { internal string Account = "maker"; }
    private sealed class FixtureControl(StoreTestData data, MongoUnitOfWork unit, BrowserClock clock, string key, StockSourceSetupService service,
        ActorContext stopper, long poolId, ProviderSelection selection) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextRequest) =>
            {
                if (!context.Request.Path.StartsWithSegments("/__fixture/policy")) { await nextRequest(); return; }
                if (context.Request.Method != "POST" || context.Request.Headers["X-Fixture-Control"] != key) { context.Response.StatusCode = 404; return; }
                var action = context.Request.Path.Value!["/__fixture/policy/".Length..];
                if (action is "maker" or "checker" or "reader") selection.Account = action;
                else if (action == "state")
                {
                    var rows = await Rows(data, "stockSourcePolicyProposals").Find(new BsonDocument()).Sort("{_id:1}").ToListAsync();
                    await context.Response.WriteAsJsonAsync(new { ids = rows.Select(row => row["_id"].AsInt64.ToString()).ToArray() }); return;
                }
                else if (action == "mfa") clock.ExpireFactor();
                else if (action == "grant")
                    await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 49103L),
                        Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
                else if (action == "account")
                    await Rows(data, "identities").UpdateOneAsync(new BsonDocument("_id", 49001L),
                        Builders<BsonDocument>.Update.Set("status", "suspended").Inc("grantVersion", 1L));
                else if (action is "same-person" or "distinct-people")
                {
                    await PersonTestData.SetEquivalenceAsync(unit, data.Database, 49001, 49002,
                        action == "same-person", context.RequestAborted);
                }
                else if (action == "stop")
                {
                    data.Clock.UtcNow = clock.UtcNow;
                    var preview = await service.PreviewSuspensionAsync(stopper, poolId);
                    await service.SuspendAsync(stopper, poolId, new(preview["poolRevision"].AsInt64, preview["suspensionRevision"].AsInt64, preview["configurationHash"].AsString),
                        new("fixture-stop", "Synthetic stop during policy review", "browser-policy"));
                }
                else { context.Response.StatusCode = 404; return; }
                context.Response.StatusCode = 204;
            }); next(app);
        };
    }
    private sealed class ObserveProviderChallenge(ProviderHandlerTests.FakeProvider provider, string issuer, ProviderSelection selection) : IStartupFilter
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
                        provider.Subject = "auth0|policy-" + selection.Account;
                        provider.Email = "policy-" + selection.Account + "@company.co.id";
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
            result[name] = new BsonArray(await Rows(data, name).Find(new BsonDocument()).ToListAsync());
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
