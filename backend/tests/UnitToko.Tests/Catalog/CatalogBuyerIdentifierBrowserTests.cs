using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using UnitToko.Tests.Ordering;
using Xunit.Abstractions;
using D = UnitToko.Platform.Ordering.OrderDraftBson;

namespace UnitToko.Tests.Catalog;

/// <summary>Actual production Next/HTTPS/API/Mongo. The source order and finite
/// stock and unavailable-code fixtures are created through their real owners; only local identity/contact
/// bootstrap and infrastructure use synthetic test inputs.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class CatalogBuyerIdentifierBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Browser_searches_reviewed_current_codes_and_keeps_one_stable_sku_draft_after_reply_loss_and_restart()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native browser rehearsal requires Windows");
        var root = RepositoryRoot(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node));
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        var frontend = Frontend(root);
        var buildPath = Path.Combine(frontend, ".next-full", "BUILD_ID"); Assert.True(File.Exists(buildPath));
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, converted: false, cogs: false,
            createOrder: true, reviewedSafetyBufferBaseUnits: 0, useSystemClock: true);
        var buyer = await data.IssueExistingSessionAsync(data.Buyer);
        var denied = await data.Base.AddActorAsync(721201, CatalogAuthoringTestData.Audience, Permissions.CatalogRead);
        denied = await data.IssueExistingSessionAsync(denied);
        foreach (var actor in new[] { buyer, denied }) {
            if (await data.Rows("accountProfiles").Find(new BsonDocument("_id", actor.UserId)).AnyAsync()) continue;
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument {
                { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic bulk-entry participant" }, { "verifiedEmail", "bulk-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime } });
        }
        Assert.Equal(1L, await data.Rows("orders").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows(D.Heads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows(D.Versions).CountDocumentsAsync(new BsonDocument()));
        // Negative exact-code cases are real catalog owner mutations before the immutable business baseline.
        const string inactiveCode = "BULK-INACTIVE-12", hiddenCode = "BULK-OTHER-ASSORTMENT-12", missingCode = "BULK-MISSING-12";
        var inactive = await data.Catalog.SkuAsync(code: inactiveCode, categoryCode: "BULKINACTIVE");
        await data.Catalog.Service.RetireSkuAsync(data.Catalog.Maker, new(inactive.Id, 1), CatalogAuthoringTestData.Metadata("bulk-inactive"));
        var hidden = await data.Catalog.SkuAsync(code: hiddenCode, categoryCode: "BULKHIDDEN");
        var hiddenAssortment = await data.Catalog.RowAsync("catalogAssortments", 502);
        await data.Catalog.Service.SetPublicationsAsync(data.Catalog.Maker,
            new(hidden.Id, 1, [new(502)], [new(new(502), hiddenAssortment["version"].AsInt64)]), CatalogAuthoringTestData.Metadata("bulk-hidden"));
        Assert.False((await data.Catalog.RowAsync("skus", inactive.Id.Value))["active"].AsBoolean);
        Assert.True((await data.Catalog.RowAsync("skus", hidden.Id.Value))["active"].AsBoolean);
        Assert.False(await data.Rows("skus").Find(new BsonDocument("sku", missingCode)).AnyAsync());
        var stableSku = await data.Catalog.RowAsync("skus", data.SkuId);
        var stableSkuCode = stableSku["sku"].AsString;
        var originalSourceOrder = (await data.Rows("orders").Find(new BsonDocument("_id", data.OrderId)).SingleAsync()).DeepClone().AsBsonDocument;
        originalSourceOrder.Remove("fence");
        const string skuCode = "BUYER-CURRENT-12", retiredAlias = "BUYER-RETIRED-ALIAS";
        var registry = await data.Catalog.Service.IdentifiersAsync(data.Catalog.Maker, data.SkuId, CatalogAuthoringTestData.Audit);
        var originalPrimary = Assert.Single(registry.Identifiers.Where(item => item.IsPrimary));
        var correction = await data.Catalog.Service.CorrectSkuCodeAsync(data.Catalog.Maker,
            new(new(data.SkuId), stableSku["revision"].AsInt64, originalPrimary.Id, originalPrimary.Revision, skuCode,
                "Reviewed current buyer spelling for the unchanged historical physical package"), CatalogAuthoringTestData.Metadata("buyer-ui-primary-correction"));
        var alias = await data.Catalog.Service.AssignIdentifierAsync(data.Catalog.Maker,
            new(new(data.SkuId), correction.SkuRevision, new(stableSku["currentDefinitionId"].AsInt64), "skuCode", null, retiredAlias,
                "Explicit internal alias to demonstrate permanent retirement"), CatalogAuthoringTestData.Metadata("buyer-ui-alias-assignment"));
        var retirement = await data.Catalog.Service.RetireIdentifierAsync(data.Catalog.Maker,
            new(new(data.SkuId), alias.SkuRevision, alias.IdentifierId, alias.IdentifierRevision,
                "This reviewed alias is no longer accepted in new buyer selection"), CatalogAuthoringTestData.Metadata("buyer-ui-alias-retirement"));
        var currentSku = await data.Catalog.RowAsync("skus", data.SkuId);
        Assert.Equal(stableSku["sku"], currentSku["sku"]); Assert.Equal(stableSku["currentDefinitionId"], currentSku["currentDefinitionId"]);
        Assert.Equal(stableSku["publicId"], currentSku["publicId"]);
        var sourceAfter = (await data.Rows("orders").Find(new BsonDocument("_id", data.OrderId)).SingleAsync()).DeepClone().AsBsonDocument;
        sourceAfter.Remove("fence"); Assert.Equal(originalSourceOrder, sourceAfter);
        Assert.Single(data.Input.Selections); Assert.Equal(1, data.Input.Selections[0].Packages);
        var baseline = await ProtectedBusinessState(data);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Release:Profile"] = "full"; settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<MongoClient>(); services.AddSingleton((MongoClient)data.Database.Client);
            services.RemoveAll<IMongoDatabase>(); services.AddSingleton(data.Database);
            services.RemoveAll<MongoUnitOfWork>(); services.AddSingleton(data.Unit);
            services.RemoveAll<IdAllocator>(); services.AddSingleton(new IdAllocator(data.Database));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Catalog.Sessions);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=Local bulk order rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); var prefix = "buyer-identifiers-" + Guid.NewGuid().ToString("N");
        static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
        var expected = data.Input with { PurchaseReference = null, Selections = data.Input.Selections.Select(line => line with { Packages = 4 }).ToImmutableArray() };
        var config = new { apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, buildId = (await File.ReadAllTextAsync(buildPath)).Trim(), proofPrefix = prefix, orderBulk = true,
            financeToken = data.Catalog.Issued[buyer.UserId].OpaqueToken, buyerId = Id(buyer.UserId),
            deniedToken = data.Catalog.Issued[denied.UserId].OpaqueToken, deniedId = Id(denied.UserId), orderId = Id(data.OrderId), deniedBranchId = "12",
            expectedInput = new {
                context = new { organizationId = Id(expected.Context.OrganizationId.Value), branchId = Id(expected.Context.BranchId.Value),
                    divisionId = expected.Context.DivisionId is { } division ? Id(division.Value) : null },
                selections = expected.Selections.Select(line => new { skuId = Id(line.SkuId.Value), definitionId = Id(line.DefinitionId.Value),
                    definitionVersion = Id(line.DefinitionVersion), priceId = Id(line.PriceId.Value), priceVersion = Id(line.PriceVersion), packages = line.Packages }).ToArray(),
                delivery = new { recipientName = expected.Delivery!.RecipientName, phone = expected.Delivery.Phone, address = expected.Delivery.Address,
                    city = expected.Delivery.City, postalCode = expected.Delivery.PostalCode, instructions = expected.Delivery.Instructions }, purchaseReference = (string?)null },
            originalPurchaseReference = data.Input.PurchaseReference, skuCode, stableSkuCode, skuPublicId = stableSku["publicId"].AsString, activeAlias = stableSkuCode, retiredAlias,
            currentPrimaryId = correction.IdentifierId.ToString(), currentPrimaryRevision = Id(correction.IdentifierRevision),
            activeAliasId = originalPrimary.Id.ToString(), activeAliasRevision = Id(originalPrimary.Revision + 1),
            skuRevision = Id(retirement.SkuRevision), inactiveCode, hiddenCode, missingCode, originalPackages = 1, bulkPackages = 4 };
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, prefix + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = frontend, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(frontend, "scripts", "verify-buyer-identifiers-harness.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Bulk order browser child did not start");
        async Task Read(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) lock (lines) { lines.AppendLine(line); File.AppendAllText(log, line + Environment.NewLine); } }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6)); await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_BUYER_IDENTIFIERS_BROWSER_PASS", lines.ToString());
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser child log: " + log);
        }
        var head = await data.Rows(D.Heads).Find(new BsonDocument()).SingleAsync();
        var version = await data.Rows(D.Versions).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(1L, head["revision"].AsInt64); Assert.Equal(1L, version["revision"].AsInt64);
        Assert.Equal(buyer.UserId, head["createdBy"].AsInt64); Assert.Equal(buyer.UserId, version["createdBy"].AsInt64);
        Assert.Equal(version["_id"], head["versionId"]); Assert.Equal(head["_id"], version["draftId"]);
        Assert.Equal(D.Input(expected), version["input"]);
        Assert.Equal("Buat draf pemesanan ulang dari pesanan " + Id(data.OrderId), version["reason"].AsString);
        var audit = await data.Rows("auditEvents").Find(new BsonDocument("operation", D.Operation)).SingleAsync();
        Assert.Equal(D.Audit(version), audit);
        var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", D.Operation)).SingleAsync();
        Assert.Equal(buyer.UserId, receipt["actorId"].AsInt64); Assert.Equal(version["requestKey"], receipt["key"]);
        Assert.Equal(version["bodyHash"], receipt["bodyHash"]); Assert.Equal(D.Result(version), receipt["result"]);
        var outbox = await data.Rows("outboxEvents").Find(new BsonDocument("kind", "ordering.draft.saved")).SingleAsync();
        Assert.Equal(D.Result(version), outbox["payload"]);
        var owner = factory.Services.GetRequiredService<OrderDraftService>();
        var recovered = await owner.RecoverAsync(buyer, version["requestKey"].AsString);
        Assert.Equal(D.Result(version), recovered);
        var persisted = await owner.ReadAsync(buyer, head["_id"].AsInt64);
        Assert.Equal(D.Input(expected), D.Input(persisted.Input));
        Assert.Equal(baseline, await ProtectedBusinessState(data));
        var afterSku = await data.Catalog.RowAsync("skus", data.SkuId);
        Assert.Equal(stableSku["sku"], afterSku["sku"]); Assert.Equal(stableSku["currentDefinitionId"], afterSku["currentDefinitionId"]);
        Assert.Equal(3L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", new BsonDocument("$in",
            new BsonArray { "catalog.identifier.correct-sku", "catalog.identifier.assign", "catalog.identifier.retire" }))));
    }

    private static async Task<BsonDocument> ProtectedBusinessState(GenuineFiniteDispatchTestData data)
    {
        var snapshot = new BsonDocument();
        foreach (var name in new[] { "orders", "orderSuborders", "orderDecisions", "reservations", "stockBalances", "inventoryPools", "lots", "movements",
            "lotValueHeldHeads", "lotValueAvailableHeads", "lotValueTransitions", "shipmentPreparations", "shipmentPreparationVersions", "shipments",
            "journalEntries", "invoices", "cashMovements", "paymentAllocations", "issuedDocuments", "catalogIdentifiers", "catalogIdentifierHistory",
            "authorizationGrants", "memberships", "skus", "productFamilies", "catalogCategories", "variantDefinitions", "priceVersions", "catalogAssortments", "catalogEntitlements" }) {
            var rows = await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            // Owner reads advance optimistic fences. Every business field, ID,
            // revision and collection membership must remain byte-identical.
            foreach (var row in rows) row.Remove("fence");
            snapshot[name] = new BsonArray(rows);
        }
        snapshot["originalReceipts"] = new BsonArray(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", new BsonDocument("$ne", D.Operation)))
            .Sort(new BsonDocument("_id", 1)).ToListAsync());
        return snapshot;
    }
    private static string Frontend(string root)
    {
        var configured = Environment.GetEnvironmentVariable("UNITTOKO_BUYER_IDENTIFIERS_FRONTEND");
        Assert.True(Path.IsPathFullyQualified(configured ?? ""), "Use the explicitly composed isolated buyer frontend");
        var path = Path.GetFullPath(configured!);
        Assert.StartsWith(Path.Combine(root, "tmp") + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(path, "scripts", "verify-buyer-identifiers-harness.mjs")));
        return path;
    }
    private static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run the browser proof from the authoritative checkout");
    }
}
