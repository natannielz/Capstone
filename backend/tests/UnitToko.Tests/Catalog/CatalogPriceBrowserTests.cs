using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;

namespace UnitToko.Tests.Catalog;

/// <summary>Actual local HTTPS/Next/API/Mongo. Pre-issued synthetic sessions do not establish Auth0 acceptance.</summary>
[Collection("Mongo")]
public sealed class CatalogPriceBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Browser_price_selection_independent_decisions_four_recoveries_and_stale_package_use_real_Mongo()
    {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node));
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await StoreTestData.CreateAsync(fixture);
        data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        var maker = await data.AddActorAsync(51001, CatalogAuthoringTestData.Shop, Permissions.PricingManage);
        var checker = await data.AddActorAsync(51002, CatalogAuthoringTestData.Shop, Permissions.PricingApprove);
        var author = await data.AddActorAsync(51003, CatalogAuthoringTestData.Shop, Permissions.CatalogManage);
        var buyer = await data.AddActorAsync(51004, CatalogAuthoringTestData.Audience, Permissions.CatalogRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, checker.UserId);
        var catalog = new CatalogAuthoringService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        var category = await catalog.SaveCategoryAsync(author, new(CatalogAuthoringTestData.Shop, null, 0, "PRICE", "Harga uji"), CatalogPriceTestData.Meta("category"));
        var family = await catalog.CreateFamilyAsync(author, new(CatalogAuthoringTestData.Shop, category.Id, "Pensil browser", "Kemasan uji terisolasi", []), CatalogPriceTestData.Meta("family"));
        var sku = await catalog.CreateSkuAsync(author, new(family.Id, family.Version, "BROWSER-12", "browser-pencil", CatalogAuthoringTestData.Definition()), CatalogPriceTestData.Meta("sku"));
        foreach (var name in new[] { "catalogPriceProposals", "catalogPriceDecisions", "priceVersions" })
            await data.Database.GetCollection<BsonDocument>("idCounters").UpdateOneAsync(new BsonDocument("_id", name),
                Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production"));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var tokens = new Dictionary<string, string>();
        foreach (var (actor, label) in new[] { (maker, "maker"), (checker, "checker"), (author, "author"), (buyer, "buyer") }) {
            var subject = "auth0|price-" + label;
            await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
                Builders<BsonDocument>.Update.Set("issuer", configured.Issuer).Set("subject", subject).Set("securityVersion", 1L));
            var now = DateTimeOffset.UtcNow;
            await data.Database.GetCollection<BsonDocument>("accountProfiles").InsertOneAsync(new BsonDocument
            {
                { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", "Petugas " + label },
                { "verifiedEmail", label + "@example.invalid" }, { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime }
            });
            var proof = new ProviderIdentityProof(configured.Issuer, subject, label + "@example.invalid", true, configured.ConnectionId,
                now, now, "otp", configured.AssurancePolicyVersion);
            var sessions = factory.Services.GetRequiredService<MongoIdentitySessionStore>();
            var issue = await factory.Services.GetRequiredService<MongoUnitOfWork>().ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, configured.Issuer, subject), proof, "synthetic-price-browser-provider", ct));
            tokens[label] = issue.OpaqueToken;
        }
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko price rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(configured.PublicOrigin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { apiAddress, origin = configured.PublicOrigin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, tokens, makerId = maker.UserId.ToString(), checkerId = checker.UserId.ToString(), authorId = author.UserId.ToString(), skuId = sku.Id.ToString() };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-catalog-price-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser child did not start");
        try {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            // Ephemeral sessions and TLS material travel only through this private pipe.
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
            output.WriteLine(await stdout); output.WriteLine(await stderr);
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("REAL_PRICE_BROWSER_PASS", await stdout);
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        var prices = await data.Database.GetCollection<BsonDocument>("priceVersions").Find(new BsonDocument()).Sort(new BsonDocument("version", 1)).ToListAsync();
        Assert.Equal(new[] { 12000.25m, 0m }, prices.Select(row => Decimal128.ToDecimal(row["unitPrice"].AsDecimal128)));
        Assert.All(prices, row => Assert.True(row["_id"].AsInt64 > 9007199254740992L));
        Assert.Equal(5, await data.Database.GetCollection<BsonDocument>("catalogPriceProposals").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(4, await data.Database.GetCollection<BsonDocument>("catalogPriceDecisions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(9, await data.Database.GetCollection<BsonDocument>("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "catalog.price.command")));
        Assert.Equal(9, await data.Database.GetCollection<BsonDocument>("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", new BsonDocument("$regex", "^catalog.price\\."))));
        foreach (var name in new[] { "stockBalances", "movements", "reservations", "journalEntries" })
            Assert.Equal(0, await data.Database.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
    }
    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run this fixture from the authoritative checkout");
    }
}
