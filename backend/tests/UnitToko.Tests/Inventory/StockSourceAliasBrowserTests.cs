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

/// <summary>Opt-in real browser/proxy/API/Mongo rehearsal. All accounts, keys and provider responses are synthetic.</summary>
[Collection("Mongo")]
public sealed class StockSourceAliasBrowserTests(MongoFixture fixture, ITestOutputHelper testOutput)
{
    [LocalStockBrowserFact]
    public async Task Browser_alias_channels_stale_review_recovery_MFA_and_revocation_use_real_API_and_Mongo()
    {
        var root = RepositoryRoot();
        var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node), "Set UNITTOKO_BROWSER_NODE to the local Node 24 executable.");
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright), "Set UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE to the local installed Playwright package.json.");
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")), "Build the production frontend before this opt-in rehearsal.");

        await using var data = await StoreTestData.CreateAsync(fixture);
        data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1";
        settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        var maker = await data.AddActorAsync(9007199254740993, CatalogAuthoringTestData.Warehouse,
            Permissions.StockSourcePropose, Permissions.StockSourceRead, Permissions.StockSourceApprove);
        var reviewer = await data.AddActorAsync(48002, CatalogAuthoringTestData.Warehouse, Permissions.StockSourceApprove, Permissions.StockSourceRead);
        foreach (var (actor, subject) in new[] { (maker, "auth0|source-maker"), (reviewer, "auth0|source-reviewer") })
            await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
                Builders<BsonDocument>.Update.Set("issuer", configured.Issuer).Set("subject", subject).Set("securityVersion", 1L));
        var requested = new StockLocation(new(100), new(101), new(102), new(104));
        await data.Database.GetCollection<BsonDocument>("authorizationScopes").InsertOneAsync(
            AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)));
        await data.AddGrantAsync(maker, 48001, requested.ToScope(), [Permissions.StockSourcePropose, Permissions.StockSourceRead, Permissions.StockSourceApprove]);
        await data.AddGrantAsync(reviewer, 48003, requested.ToScope(), [Permissions.StockSourceApprove, Permissions.StockSourceRead]);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId);
        await data.Database.GetCollection<BsonDocument>("idCounters").UpdateOneAsync(new BsonDocument("_id", "stockSourceProposals"),
            Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        await AddNames(data);
        var service = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        var location = new StockLocation(new(100), new(101), new(102), new(103));
        var setupInput = new StockSourceSetupInput(location, "browser-alias", "Alokasi browser uji", StockAuthorityMode.WebLedger,
            StockPoolControl.SharedWebLedger, 60001, 9007199254740993, null, null, "Synthetic owner", "Synthetic handoff");
        var sourceMaker = maker with { ProviderIssuer = configured.Issuer, ProviderSubject = "auth0|source-maker" };
        var sourceReviewer = reviewer with { ProviderIssuer = configured.Issuer, ProviderSubject = "auth0|source-reviewer" };
        var created = await service.ProposeAsync(sourceMaker, setupInput, new("fixture-create", "Synthetic original source", "browser-alias"));
        var approved = await service.DecideAsync(sourceReviewer, created["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Synthetic original review", new("fixture-approve", "Synthetic original source", "browser-alias"));
        var poolId = approved["poolId"].AsInt64;
        await data.Database.GetCollection<BsonDocument>("idCounters").UpdateOneAsync(new BsonDocument("_id", "stockSourceAliasProposals"),
            Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        await InventoryGraphTests.InstallBalances(data);
        await data.Database.GetCollection<BsonDocument>("lots").InsertOneAsync(InventoryGraphTests.Lot());
        await data.Database.GetCollection<BsonDocument>("stockBalances").InsertOneAsync(InventoryBson.Balance(new InventoryBalanceDocument(7000, poolId,
            new(new(location, new(200), new(201), new(202)), 9007199254740993, 2, 3, 4, 1))));
        var preserved = await InvariantState(data);
        var clock = new BrowserClock();
        var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var provider = new ProviderHandlerTests.FakeProvider(configured, new SystemClock());
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(Auth0Options.Scheme, options => options.Backchannel = new HttpClient(provider, disposeHandler: false));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(clock);
            services.AddSingleton<IStartupFilter>(new ObserveProviderChallenge(provider, configured.Issuer));
            services.AddSingleton<IStartupFilter>(new FixtureControl(data, clock, controlKey, service, sourceMaker, sourceReviewer, poolId, setupInput));
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        factory.StartServer();
        Assert.Equal("Production", factory.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        Assert.Equal("127.0.0.1", new Uri(apiAddress).Host);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=UnitToko browser rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(new Uri(configured.PublicOrigin).Host); names.AddDnsName(new Uri(configured.Issuer).Host);
        request.CertificateExtensions.Add(names.Build());
        using var tls = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new
        {
            apiAddress, origin = configured.PublicOrigin, issuer = configured.Issuer,
            pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, actorId = maker.UserId.ToString(), reviewerId = reviewer.UserId.ToString(), poolId = poolId.ToString(), controlKey
        };
        var start = new ProcessStartInfo(node!)
        {
            WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-stock-source-alias-browser.mjs"));
        start.Environment["NODE_ENV"] = "production";
        start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start browser rehearsal.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            // Temporary fixture credentials travel over the private child-process pipe, never command arguments or an artifact.
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
            testOutput.WriteLine(await output);
            testOutput.WriteLine(await errors);
            Assert.True(process.ExitCode == 0, $"Browser rehearsal failed: {await output}; {await errors}");
            Assert.Contains("REAL_SOURCE_ALIAS_BROWSER_PASS", await output);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        Assert.True(provider.SawValidPkce);
        Assert.Equal(3, provider.TokenRequests);
        var proposals = await data.Database.GetCollection<BsonDocument>("stockSourceAliasProposals").Find(new BsonDocument()).ToListAsync();
        Assert.Equal(6, proposals.Count);
        Assert.All(proposals, row => { Assert.Equal(maker.UserId, row["proposedBy"].AsInt64); Assert.True(row["_id"].AsInt64 > 9007199254740992L); });
        var decisions = await data.Database.GetCollection<BsonDocument>("stockSourceAliasDecisions").Find(new BsonDocument()).ToListAsync();
        Assert.Equal(new[] { "approve", "approve", "approve", "reject", "withdraw" }, decisions.Select(row => row["decision"].AsString).Order());
        Assert.All(decisions, row => Assert.Equal(row["decision"] == "withdraw" ? maker.UserId : reviewer.UserId, row["decidedBy"].AsInt64));
        var pool = Assert.Single(await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument()).ToListAsync());
        Assert.False(pool["active"].AsBoolean); Assert.Equal(2L, pool["revision"].AsInt64);
        var versions = await data.Database.GetCollection<BsonDocument>("stockSourceAliases").Find(new BsonDocument()).Sort("{aliasRevision:1}").ToListAsync();
        Assert.Equal(new long[] { 1, 2, 3 }, versions.Select(row => row["aliasRevision"].AsInt64));
        Assert.Equal(new long[] { 1, 2, 2 }, versions.Select(row => row["poolRevision"].AsInt64));
        Assert.Equal(new[] { true, true, false }, versions.Select(row => row["enabled"].AsBoolean));
        var revised = Assert.Single(await data.Database.GetCollection<BsonDocument>("stockSourceRevisions").Find(new BsonDocument()).ToListAsync());
        Assert.Equal(2L, revised["poolRevision"].AsInt64);
        Assert.Equal(60001L, revised["input"]["maximumAgeMilliseconds"].AsInt64);
        Assert.Equal(9007199254740993L, revised["input"]["safetyBufferBaseUnits"].AsInt64);
        var operations = new BsonDocument("operation", new BsonDocument("$in", new BsonArray
            { "stock.source.alias.propose", "stock.source.alias.approve", "stock.source.alias.reject", "stock.source.alias.withdraw" }));
        Assert.Equal(11, await data.Database.GetCollection<BsonDocument>("auditEvents").CountDocumentsAsync(operations));
        Assert.Equal(11, await data.Database.GetCollection<BsonDocument>("idempotencyRecords").CountDocumentsAsync(operations));
        Assert.Equal(preserved, await InvariantState(data));
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("identitySessions").CountDocumentsAsync(new BsonDocument("status", "active")));
        var persistedKeys = Directory.GetFiles(keys.Directory, "key-*.xml");
        Assert.NotEmpty(persistedKeys);
        Assert.All(persistedKeys, path => Assert.Contains("encryptedSecret", File.ReadAllText(path)));
    }

    private sealed class BrowserClock : IClock
    {
        private long offsetTicks;
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offsetTicks));
        public void ExpireFactor() => Interlocked.Exchange(ref offsetTicks, TimeSpan.FromMinutes(6).Ticks);
    }
    // Test assembly only. Private pipe supplies this nonce to the Node fixture, never to a browser or artifact.
    private sealed class FixtureControl(StoreTestData data, BrowserClock clock, string key, StockSourceSetupService service,
        ActorContext maker, ActorContext reviewer, long poolId, StockSourceSetupInput initial) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextRequest) =>
            {
                if (!context.Request.Path.StartsWithSegments("/__fixture/alias")) { await nextRequest(); return; }
                if (context.Request.Method != "POST" || context.Request.Headers["X-Fixture-Control"] != key)
                { context.Response.StatusCode = 404; return; }
                if (context.Request.Path == "/__fixture/alias/mfa") clock.ExpireFactor();
                else if (context.Request.Path == "/__fixture/alias/grant")
                    await data.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 48003L),
                        Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
                else if (context.Request.Path == "/__fixture/alias/command-only" || context.Request.Path == "/__fixture/alias/restore-read")
                {
                    var permissions = context.Request.Path == "/__fixture/alias/command-only" ? new[] { Permissions.StockSourcePropose }
                        : new[] { Permissions.StockSourcePropose, Permissions.StockSourceRead, Permissions.StockSourceApprove };
                    await data.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", maker.UserId),
                        Builders<BsonDocument>.Update.Set("permissions", new BsonArray(permissions)).Inc("version", 1L));
                }
                else if (context.Request.Path == "/__fixture/alias/settings")
                {
                    data.Clock.UtcNow = clock.UtcNow;
                    var proposal = await service.ProposeRevisionAsync(maker, poolId, 1, initial with { DiscrepancyOwnerReference = "Synthetic revised owner" },
                        new("fixture-settings", "Synthetic settings change", "browser-alias"));
                    await service.DecideRevisionAsync(reviewer, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
                        "Synthetic settings review", new("fixture-settings-approve", "Synthetic settings change", "browser-alias"));
                }
                else { context.Response.StatusCode = 404; return; }
                context.Response.StatusCode = 204;
            });
            next(app);
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
                        provider.Subject = provider.TokenRequests == 0 ? "auth0|source-maker" : "auth0|source-reviewer";
                        provider.Email = provider.TokenRequests == 0 ? "source-maker@company.co.id" : "source-reviewer@company.co.id";
                    }
                    return Task.CompletedTask;
                });
                return nextRequest();
            });
            next(app);
        };
    }
    private static async Task AddNames(StoreTestData data)
    {
        foreach (var node in await data.Database.GetCollection<BsonDocument>("authorizationScopes").Find(new BsonDocument("_id", new BsonDocument("$gte", 100L))).ToListAsync())
        {
            var id = node["_id"].AsInt64;
            var name = id switch { 100 => "Koperasi uji", 101 => "Toko Tebet uji", 102 => "Gudang utama uji", 103 => "OMI uji", 104 => "S-Mart uji", _ => throw new InvalidOperationException("Unexpected fixture scope") };
            await data.Database.GetCollection<BsonDocument>("organizationProfiles").InsertOneAsync(new BsonDocument
            {
                { "_id", id }, { "schemaVersion", 1 }, { "kind", node["kind"] }, { "parentId", node.GetValue("parentId", BsonNull.Value) },
                { "code", "NODE-" + id }, { "normalizedCode", "NODE-" + id }, { "name", name }, { "version", 1L },
                { "createdBy", 998L }, { "updatedBy", 999L }, { "createdAt", data.Clock.UtcNow.UtcDateTime },
                { "updatedAt", data.Clock.UtcNow.UtcDateTime }, { "approvedProposalId", id + 10000 }
            });
        }
    }
    private static async Task<BsonDocument> InvariantState(StoreTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "stockSourceProposals", "stockSourceDecisions", "inventoryPoolRegistrations", "lots", "stockBalances", "movements", "reservations", "journalEntries" })
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
