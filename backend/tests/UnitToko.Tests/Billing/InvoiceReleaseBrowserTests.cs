using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
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
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;

namespace UnitToko.Tests.Billing;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class InvoiceReleaseBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Browser_independently_approves_actual_received_goods_issues_once_and_recovers_three_committed_commands_after_process_restarts()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Genuine finite inventory browser fixture requires Windows");
        var root = Root(); var frontend = Frontend(root);
        var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node));
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        var buildPath = Path.Combine(frontend, ".next-full", "BUILD_ID"); Assert.True(File.Exists(buildPath));
        // Native fixture calls real approved commercial, finite-stock, shipment,
        // recipient and accounting owners. It creates NO release or invoice.
        await using var data = await InvoiceReleaseTestData.CreateAsync(fixture, systemClock: true);
        var original = await data.ProtectedAsync(); Assert.NotEmpty(original["cogsJournals"].AsBsonArray);
        foreach (var collection in new[] { B.Heads, B.Events, B.Bindings, InvoiceBson.Invoices, InvoiceBson.Allocations })
            Assert.False(await data.Rows(collection).Find(new BsonDocument()).AnyAsync());
        var catalog = data.Dispatch.Catalog;
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Release:Profile"] = "full"; settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<MongoClient>(); services.AddSingleton((MongoClient)data.Database.Client);
            services.RemoveAll<IMongoDatabase>(); services.AddSingleton(data.Database);
            services.RemoveAll<MongoUnitOfWork>(); services.AddSingleton(data.Unit);
            services.RemoveAll<IdAllocator>(); services.AddSingleton(new IdAllocator(data.Database));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(catalog.Sessions);
            services.RemoveAll<ShipmentReceiptService>(); services.AddSingleton(data.Receipts);
            services.RemoveAll<InvoiceReleaseService>(); services.AddSingleton(data.Releases);
            services.RemoveAll<InvoiceService>(); services.AddSingleton(data.Invoices);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        Assert.Equal("127.0.0.1", new Uri(apiAddress).Host);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=UnitToko invoice release rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); request.CertificateExtensions.Add(names.Build());
        using var tls = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var directory = Path.Combine(root, "tmp", "production", "invoice-release-browser-results"); Directory.CreateDirectory(directory);
        var run = "invoice-release-" + Guid.NewGuid().ToString("N");
        static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
        static string Wib(DateTimeOffset value) => value.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        var config = new {
            invoiceRelease = true, apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, buildId = (await File.ReadAllTextAsync(buildPath)).Trim(), proofPrefix = run, directory,
            makerToken = catalog.Issued[data.Maker.UserId].OpaqueToken, checkerToken = catalog.Issued[data.Checker.UserId].OpaqueToken,
            financeToken = catalog.Issued[data.Finance.UserId].OpaqueToken, readerToken = catalog.Issued[data.Reader.UserId].OpaqueToken,
            outsiderToken = catalog.Issued[data.Outsider.UserId].OpaqueToken,
            makerId = Id(data.Maker.UserId), checkerId = Id(data.Checker.UserId), financeId = Id(data.Finance.UserId),
            readerId = Id(data.Reader.UserId), outsiderId = Id(data.Outsider.UserId),
            selection = data.Selection.Select(row => new { shipmentId = Id(row.ShipmentId.Value), receiptId = Id(row.ReceiptId.Value) }).ToArray(),
            expected = new {
                expectedReleaseRevision = "0", periodFrom = data.Input.PeriodFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                periodTo = data.Input.PeriodTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                validFrom = data.Input.ValidFrom.ToString("O"), validUntil = data.Input.ValidUntil.ToString("O"),
                validFromWib = Wib(data.Input.ValidFrom), validUntilWib = Wib(data.Input.ValidUntil),
                templateApprovalReference = data.Input.TemplateApprovalReference, numberingApprovalReference = data.Input.NumberingApprovalReference,
                eligibilityReviewReference = data.Input.EligibilityReviewReference,
                mappings = new { receivable = data.Input.Mappings.Receivable, revenue = data.Input.Mappings.Revenue, tax = data.Input.Mappings.Tax, charges = data.Input.Mappings.Charges },
                allocations = data.Input.Allocations.Select(row => new { receiptId = Id(row.ReceiptId.Value), skuId = Id(row.SkuId.Value),
                    lineRevision = Id(row.LineRevision), tax = row.Tax.ToString(), charges = row.Charges.ToString() }).ToArray()
            }
        };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = frontend, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(frontend, "scripts", "verify-invoice-release-harness.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Invoice release browser child did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6));
            var captured = await stdout + await stderr; output.WriteLine(captured);
            await File.WriteAllTextAsync(Path.Combine(directory, run + "-native-child.log"), captured);
            Assert.True(process.ExitCode == 0, captured); Assert.Contains("REAL_INVOICE_RELEASE_BROWSER_PASS", captured);
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        var events = await data.Rows(B.Events).Find(new BsonDocument()).Sort(new BsonDocument("revision", 1)).ToListAsync();
        Assert.Equal(2, events.Count); Assert.Equal("propose", events[0]["action"].AsString); Assert.Equal("approve", events[1]["action"].AsString);
        Assert.Equal(data.Maker.UserId, events[0]["actorId"].AsInt64); Assert.Equal(data.Checker.UserId, events[1]["actorId"].AsInt64);
        Assert.NotEqual(events[0]["actorId"], events[1]["actorId"]);
        Assert.Equal(B.Input(data.Input), events[0]["input"]); Assert.Equal(events[0]["input"], events[1]["input"]);
        Assert.Equal(events[0]["source"], events[1]["source"]);
        var invoice = await data.Rows(InvoiceBson.Invoices).Find(new BsonDocument()).SingleAsync();
        var binding = await data.Rows(B.Bindings).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Finance.UserId, invoice["issuedBy"].AsInt64); Assert.Equal(invoice["_id"], binding["invoiceId"]);
        Assert.Equal(events[1]["_id"], binding["decisionId"]); Assert.Equal("approved", binding["kind"].AsString);
        Assert.Equal(invoice["snapshotHash"], binding["invoiceHash"]);
        Assert.Equal(1, await data.Rows(InvoiceBson.Allocations).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", "billing.invoice")));
        foreach (var operation in new[] { B.Prefix + "propose", B.Prefix + "approve", InvoiceBson.Operation })
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, operation)));
        Assert.Equal(original, await data.ProtectedAsync());
    }
    private static string Frontend(string root) {
        if (Environment.GetEnvironmentVariable("UNITTOKO_INVOICE_RELEASE_BROWSER_FRONTEND") is not { Length: > 0 } candidate) return Path.Combine(root, "frontend");
        Assert.True(Path.IsPathFullyQualified(candidate)); var resolved = Path.GetFullPath(candidate);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "tmp")) + Path.DirectorySeparatorChar, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(resolved, "package.json")) && File.Exists(Path.Combine(resolved, "scripts", "verify-invoice-release-harness.mjs")));
        return resolved;
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
