using System.Diagnostics;
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
using UnitToko.Api.Identity;
using UnitToko.Platform;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;
using Xunit.Abstractions;
using RuleHttpData = UnitToko.Tests.Inventory.PackConversionRuleHttpTests.HttpData;

namespace UnitToko.Tests.Inventory;

/// <summary>Opt-in local HTTPS/browser/API/Mongo proof with synthetic signed OIDC responses and people only.</summary>
[Collection("Mongo")]
public sealed class PackConversionRuleBrowserTests(MongoFixture fixture, ITestOutputHelper testOutput)
{
    [LocalStockBrowserFact]
    public async Task Browser_rule_preparation_independent_approval_original_key_recovery_and_unchanged_stock_money_use_real_https_and_Mongo()
    {
        var root = RepositoryRoot();
        var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node), "Set the local Node 24 executable.");
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright), "Set the pinned local Playwright package.json.");
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")), "Build the production frontend first.");
        await using var data = await RuleHttpData.CreateAsync(fixture);
        data.Base.Clock.UseSystemClock = true;
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Base.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1";
        settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        foreach (var (id, account) in new[] { (PackConversionRuleHttpTests.Maker, "maker"), (PackConversionRuleHttpTests.Reviewer, "reviewer"), (PackConversionRuleHttpTests.Foreign, "foreign") })
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update
                .Set("issuer", configured.Issuer).Set("subject", "auth0|pack-conversion-" + account).Set("securityVersion", 1L));
        await AddNames(data);
        var before = await data.PhysicalAsync();
        var selection = new ProviderSelection();
        var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var provider = new ProviderHandlerTests.FakeProvider(configured, new SystemClock());
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.Configure<OpenIdConnectOptions>(Auth0Options.Scheme, options => options.Backchannel = new HttpClient(provider, disposeHandler: false));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Base.Clock);
            services.AddSingleton<IStartupFilter>(new ObserveProviderChallenge(provider, configured.Issuer, selection));
            services.AddSingleton<IStartupFilter>(new FixtureControl(data, controlKey, selection));
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        Assert.Equal("Production", factory.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        Assert.Equal("127.0.0.1", new Uri(apiAddress).Host);
        using var rsa = RSA.Create(2048);
        var certificate = new CertificateRequest("CN=UnitToko pack conversion browser rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(configured.PublicOrigin).Host); names.AddDnsName(new Uri(configured.Issuer).Host);
        certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var proofPrefix = "pack-conversion-rule-" + Guid.NewGuid().ToString("N");
        var input = new {
            apiAddress, origin = configured.PublicOrigin, issuer = configured.Issuer,
            pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            makerId = PackConversionRuleHttpTests.Maker.ToString(), reviewerId = PackConversionRuleHttpTests.Reviewer.ToString(), foreignId = PackConversionRuleHttpTests.Foreign.ToString(),
            sourceSkuId = data.Sealed.Id.ToString(), destinationSkuId = data.Loose.Id.ToString(),
            sourceDefinitionId = data.Sealed.CurrentDefinitionId!.Value.ToString(), destinationDefinitionId = data.Loose.CurrentDefinitionId!.Value.ToString(),
            warehouseLabel = "Koperasi uji / Toko Tebet uji / Gudang utama uji / OMI uji", controlKey, proofPrefix
        };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-pack-conversion-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start local synthetic browser rehearsal.");
        try {
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            // Temporary synthetic credentials use the private child-process pipe and never appear in an artifact.
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6));
            testOutput.WriteLine(await output); testOutput.WriteLine(await errors);
            Assert.True(process.ExitCode == 0, $"Local synthetic browser rehearsal failed: {await output}; {await errors}");
            Assert.Contains("LOCAL_SYNTHETIC_PACK_CONVERSION_BROWSER_PASS", await output);
        } finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(provider.SawValidPkce); Assert.Equal(3, provider.TokenRequests);
        var proposals = await data.Rows("packConversionRuleProposals").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var decisions = await data.Rows("packConversionRuleDecisions").Find(new BsonDocument()).ToListAsync();
        var rules = await data.Rows("packConversionRules").Find(new BsonDocument()).ToListAsync();
        Assert.Equal(2, proposals.Count); var decision = Assert.Single(decisions); var rule = Assert.Single(rules);
        Assert.All(proposals, row => {
            Assert.Equal(PackConversionRuleHttpTests.Maker, row["actorId"].AsInt64);
            Assert.Equal(data.Sealed.Id.Value, row["input"]["sourceSkuId"].AsInt64); Assert.Equal(data.Loose.Id.Value, row["input"]["destinationSkuId"].AsInt64);
            Assert.Equal(1, row["input"]["sourcePackagesPerStep"].AsInt32); Assert.Equal(12, row["input"]["destinationPackagesPerStep"].AsInt32);
        });
        Assert.Equal(PackConversionRuleHttpTests.Reviewer, decision["actorId"].AsInt64); Assert.Equal("approve", decision["decision"].AsString);
        Assert.Equal(decision["proposalId"], rule["_id"]); Assert.Equal(1L, rule["version"].AsInt64);
        Assert.Equal(2, proposals.Select(row => row["requestKey"].AsString).Distinct(StringComparer.Ordinal).Count());
        foreach (var (operation, count) in new[] { ("inventory.pack-conversion.rule.propose", 2L), ("inventory.pack-conversion.rule.decide", 1L) }) {
            Assert.Equal(count, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", operation)));
            foreach (var collection in new[] { "auditEvents", "outboxEvents" })
                Assert.Equal(count, await data.Rows(collection).CountDocumentsAsync(new BsonDocument("kind", operation)));
        }
        var after = await data.PhysicalAsync(); Assert.True(before.ToBson().SequenceEqual(after.ToBson()), "Stock or money changed during rule preparation");
        var persistedKeys = Directory.GetFiles(keys.Directory, "key-*.xml"); Assert.NotEmpty(persistedKeys);
        Assert.All(persistedKeys, path => Assert.Contains("encryptedSecret", File.ReadAllText(path)));
        var proofDirectory = Path.Combine(root, "tmp", "production", "pack-conversion-browser-results"); Directory.CreateDirectory(proofDirectory);
        var proof = new { recordedUtc = DateTimeOffset.UtcNow, proposalCount = 2, decisionCount = 1, ruleCount = 1,
            durableSha256 = Hash(await Durable(data)), physicalBeforeSha256 = Hash(before), physicalAfterSha256 = Hash(after),
            actualMongoConfirmed = true, unchangedStockAndMoney = true, localSyntheticOnly = true, externalProviderAccepted = false,
            physicalConversionAvailable = false, stagingValidated = false, productionAccepted = false };
        var proofPath = Path.Combine(proofDirectory, proofPrefix + "-mongo.json");
        await File.WriteAllTextAsync(proofPath, JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
        testOutput.WriteLine("LOCAL_SYNTHETIC_PACK_CONVERSION_MONGO_PASS " + proofPath);
    }

    private sealed class ProviderSelection { internal string Account = "maker"; }
    private sealed class ObserveProviderChallenge(ProviderHandlerTests.FakeProvider provider, string issuer, ProviderSelection selection) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use((context, nextRequest) => {
                context.Response.OnStarting(() => {
                    var location = context.Response.Headers.Location.ToString();
                    if (location.StartsWith(issuer + "authorize?", StringComparison.Ordinal)) {
                        provider.Challenge = QueryHelpers.ParseQuery(new Uri(location).Query).ToDictionary(item => item.Key, item => item.Value.ToString());
                        provider.Subject = "auth0|pack-conversion-" + selection.Account;
                        provider.Email = "pack-conversion-" + selection.Account + "@company.co.id";
                    }
                    return Task.CompletedTask;
                }); return nextRequest();
            }); next(app);
        };
    }
    private sealed class FixtureControl(RuleHttpData data, string key, ProviderSelection selection) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use(async (context, nextRequest) => {
                if (!context.Request.Path.StartsWithSegments("/__fixture/pack-conversion")) { await nextRequest(); return; }
                if (context.Request.Method != "POST" || context.Request.Headers["X-Fixture-Control"] != key) { context.Response.StatusCode = 404; return; }
                var action = context.Request.Path.Value!["/__fixture/pack-conversion/".Length..];
                if (action is "maker" or "reviewer" or "foreign") selection.Account = action;
                else if (action is "same-person" or "distinct-people") {
                    await PersonTestData.SetEquivalenceAsync(data.Unit, data.Base.Database, PackConversionRuleHttpTests.Maker,
                        PackConversionRuleHttpTests.Reviewer, action == "same-person", context.RequestAborted);
                } else if (action == "state") { await context.Response.WriteAsJsonAsync(await State(data)); return; }
                else { context.Response.StatusCode = 404; return; }
                context.Response.StatusCode = 204;
            }); next(app);
        };
    }
    private static async Task<object> State(RuleHttpData data)
    {
        var proposals = await data.Rows("packConversionRuleProposals").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var decisions = await data.Rows("packConversionRuleDecisions").Find(new BsonDocument()).ToListAsync();
        return new { proposals = proposals.Select(proposal => {
            var decision = decisions.SingleOrDefault(row => row["proposalId"] == proposal["_id"]);
            return new { id = proposal["_id"].AsInt64.ToString(), status = decision is null ? "pending" : decision["decision"] == "approve" ? "approved" : "rejected" };
        }).ToArray(), proposalCount = proposals.Count, decisionCount = decisions.Count, ruleCount = await data.Count("packConversionRules"),
            durableSha256 = Hash(await Durable(data)), physicalSha256 = Hash(await data.PhysicalAsync()) };
    }
    private static async Task<BsonDocument> Durable(RuleHttpData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "packConversionRuleProposals", "packConversionRuleDecisions", "packConversionRules", "idempotencyRecords", "auditEvents", "outboxEvents" }) {
            var filter = name == "idempotencyRecords" ? new BsonDocument("operation", new BsonDocument("$regex", "^inventory[.]pack-conversion[.]rule[.]"))
                : name is "auditEvents" or "outboxEvents" ? new BsonDocument("kind", new BsonDocument("$regex", "^inventory[.]pack-conversion[.]rule[.]")) : new BsonDocument();
            result[name] = new BsonArray(await data.Rows(name).Find(filter).Sort(new BsonDocument("_id", 1)).ToListAsync());
        }
        return result;
    }
    private static string Hash(BsonDocument document) => Convert.ToHexString(SHA256.HashData(document.ToBson())).ToLowerInvariant();
    private static async Task AddNames(RuleHttpData data)
    {
        foreach (var node in await data.Rows("authorizationScopes").Find(new BsonDocument("_id", new BsonDocument("$gte", 100L))).ToListAsync()) {
            var id = node["_id"].AsInt64;
            var name = id switch { 100 => "Koperasi uji", 101 => "Toko Tebet uji", 102 => "Gudang utama uji", 103 => "OMI uji", 104 => "Gudang lain uji", _ => throw new InvalidOperationException("Unexpected fixture scope") };
            await data.Rows("organizationProfiles").InsertOneAsync(new BsonDocument {
                { "_id", id }, { "schemaVersion", 1 }, { "kind", node["kind"] }, { "parentId", node.GetValue("parentId", BsonNull.Value) },
                { "code", "NODE-" + id }, { "normalizedCode", "NODE-" + id }, { "name", name }, { "version", 1L },
                { "createdBy", 998L }, { "updatedBy", 999L }, { "createdAt", data.Base.Clock.UtcNow.UtcDateTime },
                { "updatedAt", data.Base.Clock.UtcNow.UtcDateTime }, { "approvedProposalId", id + 10000 }
            });
        }
    }
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "tests", "UnitToko.Tests", "UnitToko.Tests.csproj"))) return directory.FullName;
        throw new InvalidOperationException("Browser rehearsal must run from the repository checkout.");
    }
}
