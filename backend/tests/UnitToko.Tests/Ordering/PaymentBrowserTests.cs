using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;
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
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Identity;
using Xunit.Abstractions;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [WindowsPaymentBrowserFact]
    public async Task Browser_recovers_draft_reservation_and_original_upload_without_duplicate_financial_effects() {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright)); Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var files = await EvidenceAdmissionTestData.CreateAsync(fixture); await using var data = await PaymentDraftData.CreateAsync(fixture); var order = data.Invoice.Order;
        var policy = EvidenceAdmissionSchemaTests.Policy(); policy["_id"] = (await new IdAllocator(order.Database).NextAsync("evidenceStorePolicies")).Value; await data.Invoice.Rows("evidenceStorePolicies").InsertOneAsync(policy);
        var evidence = new PaymentEvidenceService(order.Database, new(order.Prices.Catalog.Client), new(order.Database), order.Prices.Catalog.Clock, data.Service, files.Registration);
        await PaymentEvidenceReadTests.ReadData.PolicyAsync(order.Database, files.StoreId, order.Prices.Catalog.Clock.UtcNow);
        // Actual durable processor and decoder; the scanner verdict is an explicit synthetic fixture, not live malware acceptance.
        var processor = new EvidenceValidationProcessor(order.Database, new(order.Prices.Catalog.Client), new(order.Database), order.Prices.Catalog.Clock, files.Registration,
            EvidenceValidationTests.Consumer, new string('c', 64), new(["evidence.validation.requested"], 3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)),
            new(1024, 100, 100, 10000), new EvidenceValidationTests.FixtureScanner(signaturesAt: order.Prices.Catalog.Clock.UtcNow.AddHours(-1)), new WindowsImageDecoder(ImageDecoderTests.Options()));
        await EvidenceValidationTests.Policy(order.Database, order.Prices.Catalog.Clock, files.StoreId, processor.PipelineId);
        var nativeOcr = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNITTOKO_OCR_PYTHON"));
        ILocalOcrEngine ocrEngine = nativeOcr ? new WindowsLocalOcrEngine(LocalOcrTests.Options()) : new PaymentOcrProcessorTests.FixtureEngine();
        var ocr = new PaymentOcrProcessor(order.Database, new(order.Prices.Catalog.Client), new(order.Database), order.Prices.Catalog.Clock, files.Registration, PaymentOcrProcessorTests.Consumer, ocrEngine);
        await data.Invoice.Rows("documentProcessingPolicies").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 }, { "consumerId", PaymentOcrProcessorTests.Consumer },
            { "storeId", files.StoreId }, { "pipelineId", ocr.PipelineId }, { "active", true }, { "revision", 1L }, { "fence", 0L },
            { "approvedAt", order.Prices.Catalog.Clock.UtcNow.AddMinutes(-1).UtcDateTime }, { "expiresAt", order.Prices.Catalog.Clock.UtcNow.AddHours(1).UtcDateTime },
            { "hostAcceptanceRef", "synthetic-host-only" }, { "modelSelectionRef", "synthetic-model-only" } });
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        var reviewer = await order.Prices.Catalog.ActorAsync(9952, UnitToko.Tests.Catalog.CatalogAuthoringTestData.Shop,
            UnitToko.Modules.Identity.Permissions.PaymentReviewRead, UnitToko.Modules.Identity.Permissions.PaymentReviewCorrect, UnitToko.Modules.Identity.Permissions.InvoiceRead);
        await data.Invoice.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", reviewer.UserId }, { "schemaVersion", 1 }, { "version", 1L },
            { "displayName", "Finance reviewer fixture" }, { "verifiedEmail", "reviewer@example.invalid" }, { "verifiedAt", order.Prices.Catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", order.Prices.Catalog.Clock.UtcNow.UtcDateTime } });
        settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
            services.RemoveAll<InvoiceService>(); services.AddSingleton(data.Invoice.Service);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Service);
            services.RemoveAll<PaymentEvidenceService>(); services.AddSingleton(evidence);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko payment rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { apiAddress, origin, nativeOcr, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            buyerToken = order.Prices.Catalog.Issued[order.Buyer.UserId].OpaqueToken, outsiderToken = order.Prices.Catalog.Issued[data.Invoice.Finance.UserId].OpaqueToken,
            reviewerToken = order.Prices.Catalog.Issued[reviewer.UserId].OpaqueToken,
            buyerId = order.Buyer.UserId.ToString(), transferDate = data.Input.TransferDate.ToString("yyyy-MM-dd"), imageBase64 = Convert.ToBase64String(ImageDecoderTests.Png(13, 7)) };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-payment-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser child did not start");
        try {
            var stdout = ReadOutputAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5)); output.WriteLine(await stdout); output.WriteLine(await stderr);
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("REAL_PAYMENT_BROWSER_PASS", await stdout); Assert.Contains("REAL_PAYMENT_CORRECTION_BROWSER_PASS", await stdout);
            async Task<string> ReadOutputAsync() {
                var text = new StringBuilder(); var validationStarted = false; var ocrStarted = false;
                while (await process.StandardOutput.ReadLineAsync() is { } line) {
                    text.AppendLine(line);
                    if (line == "VALIDATE_EVIDENCE_READY") { Assert.False(validationStarted); validationStarted = true; Assert.Equal("completed", await processor.RunOnceAsync()); }
                    if (line == "PROCESS_PAYMENT_OCR_READY") { Assert.False(ocrStarted); ocrStarted = true; Assert.Equal("completed", await ocr.RunOnceAsync()); }
                }
                Assert.True(validationStarted); Assert.True(ocrStarted); return text.ToString();
            }
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        Assert.Equal(1, await data.Invoice.Count(PaymentDraftBson.Heads)); Assert.Equal(2, await data.Invoice.Count(PaymentDraftBson.Versions));
        Assert.Equal(1, await data.Invoice.Count("documentVersions")); Assert.Equal(0, await data.Invoice.Count("payments")); Assert.Equal(0, await data.Invoice.Count("bankReceipts")); Assert.Equal(1, await data.Invoice.Count("journalEntries"));
        var head = await data.Invoice.Rows(PaymentDraftBson.Heads).Find(new BsonDocument()).SingleAsync(); Assert.Equal("submitted", head["state"]);
        Assert.Equal(1, await data.Invoice.Count("paymentSubmissions")); Assert.Equal(1, await data.Invoice.Count("evidencePins")); Assert.Equal(1, await data.Invoice.Count("evidencePinAudits"));
        Assert.Equal(1, await data.Invoice.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument { { "kind", "payment.ocr.requested" }, { "status", "completed" } }));
        Assert.Equal(1, await data.Invoice.Count(PaymentCorrectionBson.Heads)); Assert.Equal(1, await data.Invoice.Count(PaymentCorrectionBson.Versions));
        Assert.Equal(1, await data.Invoice.Count("documentExtractionResults")); Assert.Equal(1, await data.Invoice.Count("documentExtractionAudits"));
        Assert.True(await data.Invoice.Count("documentExtractionReadAudits") >= 2);
        Assert.True(await data.Invoice.Rows("documentExtractionReadAudits").CountDocumentsAsync(new BsonDocument("actorId", reviewer.UserId)) >= 2);
        Assert.True(await data.Invoice.Rows("auditEvents").CountDocumentsAsync(new BsonDocument { { "actorId", reviewer.UserId }, { "kind", "billing.payment.review.read" } }) >= 4);
        var version = await data.Invoice.Rows("documentVersions").Find(new BsonDocument()).SingleAsync(); Assert.Equal("quarantined", version["state"]); Assert.Equal("unavailable", version["scanStatus"]);
        var validation = await data.Invoice.Rows("evidenceValidationResults").Find(new BsonDocument()).SingleAsync(); Assert.Equal("passed", validation["status"]); Assert.Equal(version["sha256"], validation["source"]["sha256"]);
        Assert.Equal(1, await data.Invoice.Count("evidenceValidationAudits"));
        var leases = await data.Invoice.Rows("evidenceReadLeases").Find(new BsonDocument()).ToListAsync(); Assert.NotEmpty(leases); Assert.All(leases, row => Assert.Equal("released", row["state"]));
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
public sealed class WindowsPaymentBrowserFactAttribute : FactAttribute
{
    public WindowsPaymentBrowserFactAttribute() {
        if (!OperatingSystem.IsWindows()) Skip = "The real private-file adapter requires Windows acceptance; other hosts remain unavailable.";
        else if (Environment.GetEnvironmentVariable("UNITTOKO_STOCK_BROWSER") != "1") Skip = "Opt-in real HTTPS/Edge/Next/Mongo/private-file rehearsal.";
    }
}
