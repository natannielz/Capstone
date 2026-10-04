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
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using UnitToko.Tests.Organizations;
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
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await StoreTestData.CreateAsync(fixture);
        data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        var maker = await data.AddActorAsync(51001, CatalogAuthoringTestData.Shop, Permissions.PricingManage);
        var checker = await data.AddActorAsync(51002, CatalogAuthoringTestData.Shop, Permissions.PricingApprove);
        var author = await data.AddActorAsync(51003, CatalogAuthoringTestData.Shop, Permissions.CatalogManage);
        var buyer = await data.AddActorAsync(51004, CatalogAuthoringTestData.Audience, Permissions.CatalogRead, Permissions.OrderSubmit);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, checker.UserId);
        var catalog = new CatalogAuthoringService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        var category = await catalog.SaveCategoryAsync(author, new(CatalogAuthoringTestData.Shop, null, 0, "PRICE", "Harga uji"), CatalogPriceTestData.Meta("category"));
        var family = await catalog.CreateFamilyAsync(author, new(CatalogAuthoringTestData.Shop, category.Id, "Pensil browser", "Kemasan uji terisolasi", []), CatalogPriceTestData.Meta("family"));
        var sku = await catalog.CreateSkuAsync(author, new(family.Id, family.Version, "BROWSER-12", "browser-pencil", CatalogAuthoringTestData.Definition()), CatalogPriceTestData.Meta("sku"));
        await data.Database.GetCollection<BsonDocument>("catalogAssortments").InsertOneAsync(CatalogVisibilityBson.Assortment(
            new CatalogAssortment(501, CatalogAuthoringTestData.Shop, AuthorizationStatus.Active, 1)));
        await catalog.SetPublicationsAsync(author, new(sku.Id, 1, [new(501)], [new(new(501), 1)]), CatalogPriceTestData.Meta("publish"));
        await data.Database.GetCollection<BsonDocument>("catalogEntitlements").InsertOneAsync(CatalogVisibilityBson.Entitlement(new CatalogEntitlement(
            601, buyer.UserId, buyer.UserId, 501, CatalogAuthoringTestData.Audience, AuthorizationStatus.Active,
            data.Clock.UtcNow, null, null, 1, 998, 999, 100601, 1, data.Clock.UtcNow)));
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
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
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
        var draft = Assert.Single(await data.Database.GetCollection<BsonDocument>("orderDrafts").Find(new BsonDocument()).ToListAsync());
        Assert.Equal(3L, draft["revision"].AsInt64); Assert.Equal(buyer.UserId, draft["createdBy"].AsInt64);
        var drafts = await data.Database.GetCollection<BsonDocument>("orderDraftVersions").Find(new BsonDocument()).Sort(new BsonDocument("revision", 1)).ToListAsync();
        Assert.Equal(3, drafts.Count); Assert.Equal(new[] { 3, 4, 6 }, drafts.Select(row => row["input"]["selections"][0]["packages"].AsInt32));
        Assert.Equal("Jalan Fixture 1\nLantai 2", drafts[^1]["input"]["delivery"]["address"].AsString);
        Assert.Equal(3, await data.Database.GetCollection<BsonDocument>("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "ordering.draft.save")));
        foreach (var name in new[] { "orders", "stockBalances", "movements", "reservations", "journalEntries" })
            Assert.Equal(0, await data.Database.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
    }
    [LocalStockBrowserFact]
    public async Task Primary_shop_reviews_exact_package_quantity_and_real_subtotal_and_recovers_one_saved_draft_without_resend_or_foreign_disclosure()
    {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await StoreTestData.CreateAsync(fixture);
        // All real session and TTL dates are current. These explicitly synthetic
        // provider proofs establish a local rehearsal, never Auth0 acceptance.
        data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); data.Clock.UseSystemClock = true;
        var postal = await OrganizationAddressTestData.PrepareAsync(data, fixture.Client);
        foreach (var (key, target, purpose, address) in new[] {
            ("primary-billing", OrganizationAddressTestData.Buyer, OrganizationAddressPurpose.LegalBilling,
                OrganizationAddressTestData.Postal with { Line1 = "Jalan Penagihan Utama 5", Line2 = "Lantai administrasi", PostalCode = "12820" }),
            ("primary-delivery", OrganizationAddressTestData.Branch, OrganizationAddressPurpose.Delivery, OrganizationAddressTestData.Postal) })
            await postal.DecideAsync(await postal.ProposeAsync(key, postal.Input(target: target, purpose: purpose) with { Address = address }), key + "-approved");
        var originalPostal = await postal.StateAsync();
        var author = await data.AddActorAsync(52001, CatalogAuthoringTestData.Shop, Permissions.CatalogManage, Permissions.PricingManage);
        var checker = await data.AddActorAsync(52002, CatalogAuthoringTestData.Shop, Permissions.PricingApprove);
        var buyer = await data.AddActorAsync(52004, CatalogAuthoringTestData.Audience, Permissions.CatalogRead, Permissions.OrderSubmit);
        var foreign = await data.AddActorAsync(52005, new GrantScope(GrantScopeKind.BuyerOwned, 20, 21), Permissions.CatalogRead, Permissions.OrderSubmit);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, author.UserId, checker.UserId, buyer.UserId);
        var catalog = new CatalogAuthoringService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        var category = await catalog.SaveCategoryAsync(author, new(CatalogAuthoringTestData.Shop, null, 0, "PRIMARY", "Kemasan uji utama"), CatalogPriceTestData.Meta("primary-category"));
        var family = await catalog.CreateFamilyAsync(author, new(CatalogAuthoringTestData.Shop, category.Id, "Pensil utama", "Kemasan terisolasi untuk cabang; bukan stok tersedia", []), CatalogPriceTestData.Meta("primary-family"));
        var definition = CatalogAuthoringTestData.Definition(12, "Dus dua belas") with { MinimumOrderPackages = 4, OrderIncrementPackages = 2 };
        var sku = await catalog.CreateSkuAsync(author, new(family.Id, family.Version, "PRIMARY-12", "primary-pencil", definition), CatalogPriceTestData.Meta("primary-sku"));
        await data.Database.GetCollection<BsonDocument>("catalogAssortments").InsertOneAsync(CatalogVisibilityBson.Assortment(new CatalogAssortment(501, CatalogAuthoringTestData.Shop, AuthorizationStatus.Active, 1)));
        await catalog.SetPublicationsAsync(author, new(sku.Id, 1, [new(501)], [new(new(501), 1)]), CatalogPriceTestData.Meta("primary-publish"));
        await data.Database.GetCollection<BsonDocument>("catalogEntitlements").InsertOneAsync(CatalogVisibilityBson.Entitlement(new CatalogEntitlement(
            601, buyer.UserId, buyer.UserId, 501, CatalogAuthoringTestData.Audience, AuthorizationStatus.Active,
            data.Clock.UtcNow, null, null, 1, 998, 999, 100601, 1, data.Clock.UtcNow)));
        var prices = new CatalogPriceService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        var pricePreview = await prices.PreviewAsync(author, sku.Id.Value, CatalogPriceAccess.Prepare);
        var proposed = await prices.ProposeAsync(author, sku.Id.Value, pricePreview.Expected,
            new("IDR", UnitToko.Modules.Accounting.Money.Parse("12000.25")), CatalogPriceTestData.Meta("primary-price"));
        await prices.DecideAsync(checker, proposed["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve,
            "Synthetic independent exact package review", CatalogPriceTestData.Meta("primary-price-approved"));
        var skuRow = await data.Database.GetCollection<BsonDocument>("skus").Find(new BsonDocument("_id", sku.Id.Value)).SingleAsync();
        var originalSku = skuRow.DeepClone().AsBsonDocument; originalSku.Remove("fence");
        var originalPrice = await data.Database.GetCollection<BsonDocument>("priceVersions").Find(new BsonDocument("skuId", sku.Id.Value)).SingleAsync();
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production"));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var tokens = new Dictionary<string, string>();
        foreach (var (actor, label) in new[] { (buyer, "buyer"), (foreign, "foreign") }) {
            var subject = "auth0|primary-shop-" + label; var now = data.Clock.UtcNow;
            await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
                Builders<BsonDocument>.Update.Set("issuer", configured.Issuer).Set("subject", subject).Set("securityVersion", 1L));
            await data.Database.GetCollection<BsonDocument>("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Pembeli fixture " + label }, { "verifiedEmail", label + "@example.invalid" }, { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
            var proof = new ProviderIdentityProof(configured.Issuer, subject, label + "@example.invalid", true, configured.ConnectionId,
                now, now, "otp", configured.AssurancePolicyVersion);
            var sessions = factory.Services.GetRequiredService<MongoIdentitySessionStore>();
            var issued = await factory.Services.GetRequiredService<MongoUnitOfWork>().ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, configured.Issuer, subject), proof, "synthetic-primary-shop-provider", ct)); tokens[label] = issued.OpaqueToken;
        }
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko primary shop rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(configured.PublicOrigin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { primaryShop = true, apiAddress, origin = configured.PublicOrigin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, tokens, buyerId = buyer.UserId.ToString(), foreignId = foreign.UserId.ToString(), skuId = sku.Id.ToString(),
            publicId = skuRow["publicId"].AsString, definitionId = skuRow["currentDefinitionId"].AsInt64.ToString(), priceId = originalPrice["_id"].AsInt64.ToString() };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-catalog-price-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser child did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("REAL_PRIMARY_SHOP_BROWSER_PASS", await stdout);
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            output.WriteLine(await stdout); output.WriteLine(await stderr);
        }
        var draft = Assert.Single(await data.Database.GetCollection<BsonDocument>(OrderDraftBson.Heads).Find(new BsonDocument()).ToListAsync());
        Assert.Equal(1L, draft["revision"].AsInt64); Assert.Equal(buyer.UserId, draft["createdBy"].AsInt64);
        var version = Assert.Single(await data.Database.GetCollection<BsonDocument>(OrderDraftBson.Versions).Find(new BsonDocument()).ToListAsync());
        Assert.Equal(draft["_id"], version["draftId"]); Assert.Equal(1L, version["revision"].AsInt64);
        Assert.Equal(10L, version["input"]["context"]["organizationId"].AsInt64); Assert.Equal(11L, version["input"]["context"]["branchId"].AsInt64);
        var line = Assert.Single(version["input"]["selections"].AsBsonArray);
        Assert.Equal(sku.Id.Value, line["skuId"].AsInt64); Assert.Equal(skuRow["currentDefinitionId"], line["definitionId"]);
        Assert.Equal(1L, line["definitionVersion"].AsInt64); Assert.Equal(originalPrice["_id"], line["priceId"]); Assert.Equal(1L, line["priceVersion"].AsInt64); Assert.Equal(6, line["packages"].AsInt32);
        Assert.True(version["input"]["delivery"].IsBsonNull); Assert.True(version["input"]["purchaseReference"].IsBsonNull);
        Assert.Equal(72001.50m, Decimal128.ToDecimal(originalPrice["unitPrice"].AsDecimal128) * line["packages"].AsInt32);
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", OrderDraftBson.Operation)));
        var afterSku = await data.Database.GetCollection<BsonDocument>("skus").Find(new BsonDocument("_id", sku.Id.Value)).SingleAsync(); afterSku.Remove("fence"); Assert.Equal(originalSku, afterSku);
        Assert.Equal(originalPrice, await data.Database.GetCollection<BsonDocument>("priceVersions").Find(new BsonDocument("_id", originalPrice["_id"])).SingleAsync());
        Assert.Equal(originalPostal, await postal.StateAsync());
        foreach (var table in new[] { "orders", "stockBalances", "movements", "reservations", "journalEntries", "media" })
            Assert.False(await data.Database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run this fixture from the authoritative checkout");
    }
}
