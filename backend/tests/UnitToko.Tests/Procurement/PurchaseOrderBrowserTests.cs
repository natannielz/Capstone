using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;
using D = UnitToko.Tests.Procurement.PurchaseOrderTestData;
using S = UnitToko.Platform.Procurement.ProcurementRequestPurchaseOrderSchemas;

namespace UnitToko.Tests.Procurement;

// Genuine producer history with explicitly synthetic people, verification policy, sessions and commercial document.
[Collection("Mongo")]
public sealed class PurchaseOrderBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public Task Browser_proposes_explicit_commercial_order_and_independently_approves_real_request_quantity_with_original_key_recovery() => RunAsync(false);
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public Task Browser_reviews_supplier_arrival_independently_and_recovers_original_write_after_reply_loss() => RunAsync(true);
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task RunAsync(bool purchaseArrivals)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This native browser rehearsal requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await D.CreateAsync(fixture); data.Clock.UseSystemClock = true;
        var until = DateTimeOffset.FromUnixTimeSeconds(data.Clock.UtcNow.ToUnixTimeSeconds() / 60 * 60).AddDays(1);
        var date = UnitToko.Modules.Accounting.AccountingCalendar.BusinessDate(data.Clock.UtcNow);
        BsonDocument? existingOrder = null;
        if (purchaseArrivals) {
            var commercial = data.Input();
            existingOrder = await data.DecideAsync(await data.ProposeAsync("arrival-browser-po", commercial with {
                BusinessDate = date, CommercialReference = commercial.CommercialReference with { IssuedDate = date, ValidUntil = until },
                PaymentTerms = commercial.PaymentTerms with { DueDate = date.AddDays(30) }
            }), "arrival-browser-po-approved");
        }
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var issued = new Dictionary<long, SessionIssue>();
        foreach (var actor in new[] { data.Maker, data.Checker, data.Reader, data.Outsider }) {
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L)); var now = data.Clock.UtcNow;
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "po-" + actor.UserId + "@example.invalid", true, "fixture-connection", now, now, "otp", "fixture-policy");
            issued[actor.UserId] = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-po-provider", ct));
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", "Synthetic purchase-order participant " + actor.UserId },
                { "verifiedEmail", "po-" + actor.UserId + "@example.invalid" }, { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
        }
        var original = new BsonDocument();
        foreach (var table in new[] { "organizationProfiles", "organizationProposals", "authorizationHistory", "authorizationScopes", "catalogCategories", "productFamilies", "skus", "variantDefinitions", ProcurementRequestTestData.Requests,
            ProcurementRequestTestData.Decisions, SupplierTestData.Policies, SupplierTestData.Proposals, SupplierTestData.Observations, SupplierTestData.Decisions, SupplierTestData.Versions }) {
            var rows = await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); original[table] = new BsonArray(rows);
        }
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<PurchaseOrderService>(); services.AddSingleton(data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses); const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko purchase-order rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var expectedArrival = until.AddDays(2);
        var input = new { purchaseOrders = true, purchaseArrivals, orderId = existingOrder?["orderId"].AsInt64.ToString(),
            orderProposalId = existingOrder?["proposalId"].AsInt64.ToString(), expectedArrival = expectedArrival.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            arrivalWib = expectedArrival.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd'T'HH:mm"),
            apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = issued[data.Maker.UserId].OpaqueToken, makerToken = issued[data.Maker.UserId].OpaqueToken, checkerToken = issued[data.Checker.UserId].OpaqueToken,
            readerToken = issued[data.Reader.UserId].OpaqueToken, outsiderToken = issued[data.Outsider.UserId].OpaqueToken,
            makerId = data.Maker.UserId.ToString(), checkerId = data.Checker.UserId.ToString(), readerId = data.Reader.UserId.ToString(), outsiderId = data.Outsider.UserId.ToString(),
            organizationId = "100", shopId = "101", warehouseId = "102", channelId = "103", requestId = data.Request["requestId"].AsInt64.ToString(), requestDecisionId = data.RequestDecision["decisionId"].AsInt64.ToString(),
            supplierId = data.Supplier["supplierId"].AsInt64.ToString(), supplierVersionId = data.Supplier["versionId"].AsInt64.ToString(), supplierVersion = data.Supplier["version"].AsInt64.ToString(),
            supplierName = SupplierTestData.Details.DisplayName, supplierSubject = SupplierTestData.Details.SubjectReference,
            businessDate = date.ToString("yyyy-MM-dd"), dueDate = date.AddDays(30).ToString("yyyy-MM-dd"), validUntil = until.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            organizationName = "Synthetic cooperative", shopName = "Synthetic fulfillment shop", warehouseName = "Synthetic dispatch warehouse", channelName = "Synthetic retail channel",
            skuId = data.Procurement.Sku.Id.ToString(), definitionId = data.Procurement.Sku.CurrentDefinitionId!.Value.ToString(), definitionVersion = data.Procurement.Sku.DefinitionVersion!.Value.ToString(),
            skuCode = "PROC-PENCIL-12", familyName = "Pensil pengadaan", packageLabel = "Dus", specification = "Synthetic approved logo specification" };
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, (purchaseArrivals ? "purchase-arrivals-" : "purchase-orders-") + Guid.NewGuid().ToString("N") + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Purchase-order browser child did not start");
        async Task ReadAsync(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) { lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); } } }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try { await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString()); Assert.True(process.ExitCode == 0, lines.ToString());
            Assert.Contains(purchaseArrivals ? "REAL_PURCHASE_ARRIVAL_BROWSER_PASS" : "REAL_PURCHASE_ORDER_BROWSER_PASS", lines.ToString());
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser child log: " + logPath); }
        var proposal = await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync(); var decision = await data.Rows(D.Decisions).Find(new BsonDocument()).SingleAsync();
        var order = await data.Rows(D.Orders).Find(new BsonDocument()).SingleAsync(); var allocation = await data.Rows(S.Allocations).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Maker.UserId, proposal["actorId"].AsInt64); Assert.Equal(data.Checker.UserId, decision["actorId"].AsInt64); Assert.Equal("approve", decision["decision"]);
        Assert.Equal(data.Request["requestId"], order["requestId"]); Assert.Equal(data.RequestDecision["decisionId"], order["requestDecisionId"]);
        Assert.Equal(order["_id"], allocation["orderId"]); Assert.Equal(decision["snapshotHash"], allocation["approvalHash"]); Assert.Equal(3, allocation["packages"].AsInt32);
        Assert.Equal(3, (await data.Rows(S.Commitments).Find(new BsonDocument()).SingleAsync())["committedPackages"].AsInt32);
        var line = Assert.Single(proposal["input"]["lines"].AsBsonArray); Assert.Equal("10000.00", line["netPricePerPackage"]); Assert.Equal("3300.00", line["taxAmount"]); Assert.Equal("700.00", line["supplierChargeAmount"]);
        Assert.Equal("000001", line["sourceAccountReference"]); Assert.Equal("000012", line["externalItemCode"]); Assert.Equal(data.Procurement.Sku.CurrentDefinitionId!.Value.Value, line["definitionId"].AsInt64);
        Assert.Equal("credit", proposal["input"]["paymentTerms"]["kind"]); Assert.Equal("0.00", proposal["input"]["paymentTerms"]["requiredAdvance"]);
        Assert.Equal(until.UtcDateTime, proposal["input"]["commercialReference"]["validUntil"].ToUniversalTime());
        Assert.Equal("approved", (await data.Service.ReadAsync(data.Reader, proposal["_id"].AsInt64))["state"]);
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") }) {
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, D.Prefix + "propose"))); Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, D.Prefix + "approve")));
        }
        foreach (var element in original) { var rows = await data.Rows(element.Name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); Assert.Equal(element.Value, new BsonArray(rows)); }
        if (purchaseArrivals) {
            var events = await data.Rows(PurchaseArrivalRecords.Events).Find(new BsonDocument()).Sort(new BsonDocument("revision", 1)).ToListAsync();
            Assert.Equal(2, events.Count); Assert.Equal("propose", events[0]["action"]); Assert.Equal("approve", events[1]["action"]);
            Assert.Equal(data.Maker.UserId, events[0]["actorId"].AsInt64); Assert.Equal(data.Checker.UserId, events[1]["actorId"].AsInt64);
            Assert.Equal(expectedArrival.UtcDateTime, events[1]["input"]["expectedAt"].ToUniversalTime());
            Assert.Equal(2, await data.Rows(PurchaseArrivalRecords.Audits).CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(2, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", new BsonRegularExpression("^procurement\\.purchase\\.arrival\\."))));
            var arrival = await data.Service.ReadArrivalAsync(data.Reader, order["_id"].AsInt64, data.Procurement.Sku.Id.Value);
            Assert.Equal("approved", arrival["state"]); Assert.Equal(expectedArrival.UtcDateTime, arrival["expectedAt"].ToUniversalTime());
        }
        await data.NoFinancialOrReceiptEffectsAsync();
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
