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
using UnitToko.Modules.Inventory;
using UnitToko.Platform;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Inventory;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Catalog;

/// <summary>Opt-in real browser/proxy/API/Mongo rehearsal. All accounts, keys and provider responses are synthetic.</summary>
[Collection("Mongo")]
public sealed class CatalogIdentifierBrowserTests(MongoFixture fixture, ITestOutputHelper testOutput)
{
    [LocalStockBrowserFact]
    public async Task Browser_authors_identifier_then_scans_exact_package_and_recovers_one_count_after_restart()
    {
        var root = RepositoryRoot();
        var frontend = Path.Combine(root, "frontend");
        if (Environment.GetEnvironmentVariable("UNITTOKO_IDENTIFIER_BROWSER_FRONTEND") is { Length: > 0 } candidateFrontend)
        {
            Assert.True(Path.IsPathFullyQualified(candidateFrontend), "The isolated frontend path must be absolute.");
            frontend = Path.GetFullPath(candidateFrontend);
            Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "tmp")) + Path.DirectorySeparatorChar, frontend, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(frontend, "package.json")) && File.Exists(Path.Combine(frontend, "scripts", "verify-catalog-identifier-browser.mjs")),
                "The isolated frontend must contain the actual candidate application and browser driver.");
        }
        var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node), "Set UNITTOKO_BROWSER_NODE to the local Node 24 executable.");
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright), "Set UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE to the local installed Playwright package.json.");
        Assert.True(File.Exists(Path.Combine(frontend, ".next-full", "BUILD_ID")), "Build the production frontend before this opt-in rehearsal.");

        await using var data = await StoreTestData.CreateAsync(fixture);
        data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        var settings = IdentityRuntimeTests.Settings(keys);
        settings["Release:Profile"] = "full";
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1";
        settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        var counter = await data.AddActorAsync(9007199254740993, CatalogAuthoringTestData.Warehouse, Permissions.StockCount, Permissions.StockRead);
        await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", counter.UserId),
            Builders<BsonDocument>.Update.Set("issuer", configured.Issuer).Set("subject", "auth0|fixture-user").Set("securityVersion", 1L));
        await data.AddGrantAsync(counter, 48004, CatalogAuthoringTestData.Shop, [Permissions.CatalogManage]);
        var maker = await data.AddActorAsync(48001, CatalogAuthoringTestData.Shop, Permissions.CatalogManage);
        var catalog = new CatalogAuthoringService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        var category = await catalog.SaveCategoryAsync(maker, new(CatalogAuthoringTestData.Shop, null, 0, "BROWSER", "Browser fixtures"), CatalogAuthoringTestData.Metadata("browser-category"));
        var family = await catalog.CreateFamilyAsync(maker, new(CatalogAuthoringTestData.Shop, category.Id, "Pensil browser", "Synthetic integration package", []), CatalogAuthoringTestData.Metadata("browser-family"));
        var sku = await catalog.CreateSkuAsync(maker, new(family.Id, family.Version, "BROWSER-12", "private-browser-physical", CatalogAuthoringTestData.Definition()), CatalogAuthoringTestData.Metadata("browser-sku"));
        var sourceMaker = await data.AddActorAsync(48002, CatalogAuthoringTestData.Warehouse, Permissions.StockSourcePropose);
        var sourceReviewer = await data.AddActorAsync(48003, CatalogAuthoringTestData.Warehouse, Permissions.StockSourceApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, sourceMaker.UserId, sourceReviewer.UserId, counter.UserId);
        var sourceService = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        var proposed = await sourceService.ProposeAsync(sourceMaker,
            new(new(new(100), new(101), new(102), new(103)), "browser-count-reviewed-pool", "Sumber hitungan uji",
                StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60000, 0, null, null,
                "synthetic-discrepancy-owner", "synthetic-reviewed-handoff"),
            new("browser-count-source-propose", "Synthetic physical-count source", "stock-count-browser"));
        var approved = await sourceService.DecideAsync(sourceReviewer, proposed["proposalId"].AsInt64, 1,
            StockSourceSetupDecision.Approve, "synthetic-independent-review",
            new("browser-count-source-approve", "Independent synthetic source review", "stock-count-browser"));
        var poolId = approved["poolId"].AsInt64;
        await data.Database.GetCollection<BsonDocument>("idCounters").UpdateOneAsync(new BsonDocument("_id", "stockCountObservations"),
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
            playwrightPackage = playwright, actorId = counter.UserId.ToString(),
            skuId = sku.Id.ToString(), definitionId = sku.CurrentDefinitionId!.Value.ToString(),
            poolId = poolId.ToString(System.Globalization.CultureInfo.InvariantCulture), sourceLabel = "Sumber hitungan uji",
            originalSkuCode = "BROWSER-12", correctedSkuCode = "BROWSER-CORRECTED-12", rawGtin = "4006381333931", canonicalGtin = "04006381333931",
            proofPrefix = "catalog-identifier-count-" + Guid.NewGuid().ToString("N")
        };
        var start = new ProcessStartInfo(node!)
        {
            WorkingDirectory = frontend, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(frontend, "scripts", "verify-catalog-identifier-browser.mjs"));
        start.Environment["NODE_ENV"] = "production";
        start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
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
            Assert.Contains("REAL_IDENTIFIER_COUNT_BROWSER_PASS", await output);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        Assert.True(provider.SawValidPkce);
        Assert.Equal(1, provider.TokenRequests);
        var observations = await data.Database.GetCollection<BsonDocument>("stockCountObservations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var observation = Assert.Single(observations);
        Assert.Equal(2, observation["packages"].AsInt32); Assert.Equal(24L, observation["baseUnits"].AsInt64);
        Assert.True(observation["_id"].AsInt64 > 9007199254740992L);
        Assert.Equal(counter.UserId, observation["actorId"].AsInt64); Assert.Equal(poolId, observation["poolBinding"]["poolId"].AsInt64);
        Assert.False(observation["poolBinding"]["active"].AsBoolean);
        var counts = data.Database.GetCollection<BsonDocument>("idempotencyRecords");
        var receipt = await counts.Find(new BsonDocument("operation", "inventory.stock-count.record")).SingleAsync();
        var audit = await data.Database.GetCollection<BsonDocument>("auditEvents").Find(new BsonDocument("operation", "inventory.stock-count.record")).SingleAsync();
        StockCountBson.Graph(observation, audit, receipt);
        var binding = await data.Database.GetCollection<BsonDocument>(OperationalScanCommandBindings.Collection).Find(new BsonDocument()).SingleAsync();
        OperationalScanCommandBindings.RequireRecord(binding);
        Assert.Equal("scanned", binding["mode"].AsString); Assert.Equal(counter.UserId, binding["actorId"].AsInt64);
        Assert.Equal(receipt["key"], binding["requestKey"]); Assert.Equal(receipt["_id"], binding["receiptId"]);
        Assert.Equal(OperationalScanCommandBindings.Digest(receipt), binding["receiptHash"].AsString);
        Assert.Equal(audit["_id"], binding["ownerAuditId"]); Assert.Equal(OperationalScanCommandBindings.Digest(audit), binding["ownerAuditHash"].AsString);
        var selected = Assert.Single(binding["selections"].AsBsonArray)["identifier"];
        Assert.Equal("gtin", selected["kind"].AsString); Assert.Equal("04006381333931", selected["canonicalValue"].AsString);
        Assert.Equal(sku.Id.Value, selected["skuId"].AsInt64); Assert.Equal(sku.CurrentDefinitionId.Value.Value, selected["definitionId"].AsInt64);
        Assert.Equal(1L, selected["identifierRevision"].AsInt64); Assert.Equal(1L, selected["definitionVersion"].AsInt64);
        var evidence = Assert.Single(binding["evidence"].AsBsonArray);
        Assert.Equal("4006381333931", evidence["assignment"]["rawValue"].AsString);
        Assert.Equal("BROWSER-12", evidence["sku"]["sku"].AsString);
        var identifiers = await data.Database.GetCollection<BsonDocument>(CatalogIdentifierRecords.Collection).Find(new BsonDocument("skuId", sku.Id.Value)).ToListAsync();
        Assert.Equal(3, identifiers.Count);
        var primary = Assert.Single(identifiers, row => row["isPrimary"].AsBoolean);
        Assert.Equal("BROWSER-CORRECTED-12", primary["canonicalValue"].AsString); Assert.True(primary["retiredAt"].IsBsonNull);
        var old = Assert.Single(identifiers, row => row["canonicalValue"] == "BROWSER-12");
        Assert.False(old["isPrimary"].AsBoolean); Assert.False(old["retiredAt"].IsBsonNull); Assert.Equal(3L, old["revision"].AsInt64);
        var currentSku = await data.Database.GetCollection<BsonDocument>("skus").Find(new BsonDocument("_id", sku.Id.Value)).SingleAsync();
        Assert.Equal("BROWSER-12", currentSku["sku"].AsString); Assert.Equal(sku.CurrentDefinitionId.Value.Value, currentSku["currentDefinitionId"].AsInt64);
        Assert.Equal(4L, currentSku["revision"].AsInt64);
        Assert.Equal(3L, await counts.CountDocumentsAsync(new BsonDocument("operation", new BsonDocument("$regex", "^catalog\\.identifier\\."))));
        Assert.Equal(3L, await data.Database.GetCollection<BsonDocument>("auditEvents").CountDocumentsAsync(new BsonDocument("operation", new BsonDocument("$regex", "^catalog\\.identifier\\."))));
        Assert.Equal(physical, await Physical(data));
        Assert.Equal(1L, await data.Database.GetCollection<BsonDocument>("identitySessions").CountDocumentsAsync(new BsonDocument("status", "active")));
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
                        provider.Challenge = QueryHelpers.ParseQuery(new Uri(location).Query).ToDictionary(item => item.Key, item => item.Value.ToString());
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
        foreach (var name in new[] { "lots", "stockBalances", "movements", "reservations", "journalEntries", "orders", "invoices", "paymentAllocations", "cashMovements" })
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
