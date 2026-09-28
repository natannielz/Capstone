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
public sealed class StockSourceBrowserTests(MongoFixture fixture, ITestOutputHelper testOutput)
{
    [LocalStockBrowserFact]
    public async Task Browser_source_proposal_independent_review_recovery_and_logout_use_real_API_and_Mongo()
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
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId);
        await data.Database.GetCollection<BsonDocument>("idCounters").UpdateOneAsync(new BsonDocument("_id", "stockSourceProposals"),
            Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        await AddNames(data);
        var physical = await Physical(data);
        using var provider = new ProviderHandlerTests.FakeProvider(configured, new SystemClock());
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(Auth0Options.Scheme, options => options.Backchannel = new HttpClient(provider, disposeHandler: false));
            services.AddSingleton<IStartupFilter>(new ObserveProviderChallenge(provider, configured.Issuer));
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
            playwrightPackage = playwright, actorId = maker.UserId.ToString(), reviewerId = reviewer.UserId.ToString()
        };
        var start = new ProcessStartInfo(node!)
        {
            WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-stock-source-browser.mjs"));
        start.Environment["NODE_ENV"] = "production";
        start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start browser rehearsal.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            // Temporary fixture credentials travel over the private child-process pipe, never command arguments or an artifact.
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4));
            testOutput.WriteLine(await output);
            testOutput.WriteLine(await errors);
            Assert.True(process.ExitCode == 0, $"Browser rehearsal failed: {await output}; {await errors}");
            Assert.Contains("REAL_SOURCE_BROWSER_PASS", await output);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        Assert.True(provider.SawValidPkce);
        Assert.Equal(2, provider.TokenRequests);
        var proposals = await data.Database.GetCollection<BsonDocument>("stockSourceProposals").Find(new BsonDocument()).ToListAsync();
        Assert.Equal(3, proposals.Count);
        Assert.All(proposals, row => Assert.Equal(maker.UserId, row["proposedBy"].AsInt64));
        Assert.All(proposals, row => Assert.True(row["_id"].AsInt64 > 9007199254740992L));
        var decisions = await data.Database.GetCollection<BsonDocument>("stockSourceDecisions").Find(new BsonDocument()).ToListAsync();
        Assert.Equal(new[] { "approve", "reject", "withdraw" }, decisions.Select(row => row["decision"].AsString).Order());
        Assert.All(decisions, row => Assert.Equal(row["decision"] == "withdraw" ? maker.UserId : reviewer.UserId, row["decidedBy"].AsInt64));
        var pool = Assert.Single(await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument()).ToListAsync());
        Assert.False(pool["active"].AsBoolean);
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("inventoryPoolRegistrations").CountDocumentsAsync(new BsonDocument()));
        var operations = new BsonDocument("operation", new BsonDocument("$in", new BsonArray { "stock.source.propose", "stock.source.approve", "stock.source.reject", "stock.source.withdraw" }));
        Assert.Equal(6, await data.Database.GetCollection<BsonDocument>("auditEvents").CountDocumentsAsync(operations));
        Assert.Equal(6, await data.Database.GetCollection<BsonDocument>("idempotencyRecords").CountDocumentsAsync(operations));
        Assert.Equal(physical, await Physical(data));
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("identitySessions").CountDocumentsAsync(new BsonDocument("status", "active")));
        var persistedKeys = Directory.GetFiles(keys.Directory, "key-*.xml");
        Assert.NotEmpty(persistedKeys);
        Assert.All(persistedKeys, path => Assert.Contains("encryptedSecret", File.ReadAllText(path)));
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
            var name = id switch { 100 => "Koperasi uji", 101 => "Toko Tebet uji", 102 => "Gudang utama uji", 103 => "OMI uji", _ => throw new InvalidOperationException("Unexpected fixture scope") };
            await data.Database.GetCollection<BsonDocument>("organizationProfiles").InsertOneAsync(new BsonDocument
            {
                { "_id", id }, { "schemaVersion", 1 }, { "kind", node["kind"] }, { "parentId", node.GetValue("parentId", BsonNull.Value) },
                { "code", "NODE-" + id }, { "normalizedCode", "NODE-" + id }, { "name", name }, { "version", 1L },
                { "createdBy", 998L }, { "updatedBy", 999L }, { "createdAt", data.Clock.UtcNow.UtcDateTime },
                { "updatedAt", data.Clock.UtcNow.UtcDateTime }, { "approvedProposalId", id + 10000 }
            });
        }
    }
    private static async Task<BsonDocument> Physical(StoreTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "lots", "stockBalances", "movements", "reservations", "journalEntries" })
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
