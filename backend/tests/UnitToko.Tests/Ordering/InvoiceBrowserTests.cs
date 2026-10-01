using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class InvoiceBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public Task Browser_issues_received_goods_recovers_lost_response_and_downloads_private_invoice() => RunAsync(false);
    [LocalStockBrowserFact]
    public Task Browser_links_finalized_sale_case_recovers_original_request_and_preserves_sale() => RunAsync(true);
    [LocalStockBrowserFact]
    public Task Browser_independently_decides_return_recovers_lost_decision_and_shows_private_buyer_explanation() => RunAsync(true, true);
    private async Task RunAsync(bool afterSales, bool caseDecision = false) {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await InvoiceData.CreateAsync(fixture); var order = data.Order;
        if (afterSales) await order.Prices.Catalog.Base.AddGrantAsync(order.Buyer, 99810, CatalogAuthoringTestData.Audience, [Permissions.CaseRead, Permissions.CaseRequest]);
        if (caseDecision) {
            await order.Prices.Catalog.Base.AddGrantAsync(data.Finance, 99830, CatalogAuthoringTestData.Shop, [Permissions.CaseRead, Permissions.CaseManage]);
            foreach (var person in new[] { order.Buyer.UserId, data.Finance.UserId }) if (!await data.Rows("personRecords").Find(new BsonDocument("_id", PersonTestData.PersonId(person))).AnyAsync())
                await PersonTestData.SeedDistinctPeopleAsync(order.Database, order.Prices.Catalog.Clock, person);
        }
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
            services.RemoveAll<InvoiceService>(); services.AddSingleton(data.Service);
            if (afterSales) { services.RemoveAll<AfterSalesCaseService>(); services.AddSingleton(new AfterSalesCaseService(order.Database, new(order.Prices.Catalog.Client), new(order.Database), order.Prices.Catalog.Clock, data.Service)); }
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko invoice rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = order.Prices.Catalog.Issued[data.Finance.UserId].OpaqueToken, buyerToken = order.Prices.Catalog.Issued[order.Buyer.UserId].OpaqueToken,
            courierToken = order.Prices.Catalog.Issued[data.Dispatch.Courier.UserId].OpaqueToken, financeId = data.Finance.UserId.ToString(), buyerId = order.Buyer.UserId.ToString(), receiptId = data.Receipt.Id.ToString(), afterSales, caseDecision };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser child did not start");
        try {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); output.WriteLine(await stdout); output.WriteLine(await stderr);
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("REAL_INVOICE_BROWSER_PASS", await stdout);
            if (afterSales) Assert.Contains("REAL_CASE_BROWSER_PASS", await stdout);
            if (caseDecision) Assert.Contains("REAL_CASE_DECISION_BROWSER_PASS", await stdout);
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        Assert.Equal(1, await data.Count(InvoiceBson.Invoices)); Assert.Equal(1, await data.Count(InvoiceBson.Allocations)); Assert.Equal(1, await data.Count("journalEntries"));
        var row = await data.Rows(InvoiceBson.Invoices).Find(new BsonDocument()).SingleAsync(); Assert.Equal(6L, row["lines"][0]["acceptedBaseUnits"].AsInt64);
        Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", InvoiceBson.Operation)));
        Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", InvoiceBson.Operation)));
        if (afterSales) {
            Assert.Equal(1, await data.Count("afterSalesCases")); var complaint = await data.Rows("afterSalesCases").Find(new BsonDocument()).SingleAsync();
            Assert.Equal(row["_id"], complaint["input"]["invoiceId"]); Assert.Equal(1L, complaint["input"]["lines"][0]["reportedBaseUnits"].AsInt64);
            Assert.Equal(row["snapshotHash"], complaint["source"]["invoiceHash"]); Assert.Equal(6L, complaint["source"]["lines"][0]["acceptedBaseUnits"].AsInt64);
            Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "fulfillment.case.request")));
            Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.case.request")));
            if (caseDecision) {
                var decision = await data.Rows("afterSalesCaseDecisions").Find(new BsonDocument()).SingleAsync(); Assert.Equal(complaint["_id"], decision["_id"]);
                Assert.Equal("return_authorized", decision["state"]); Assert.Equal(data.Finance.UserId, decision["actorId"]); Assert.Equal(order.Buyer.UserId, decision["source"]["requestedBy"]);
                Assert.Equal(1L, decision["input"]["lines"][0]["reportedBaseUnits"].AsInt64);
                Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "fulfillment.case.decide")));
                Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.case.decide")));
                Assert.Equal(1L, row["postingFence"].AsInt64);
            }
        }
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
