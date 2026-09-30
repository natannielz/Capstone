using System.Diagnostics;
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
using UnitToko.Platform.Identity;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;
using Xunit.Abstractions;
using UnitToko.Platform.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Tests.Catalog;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentVerificationBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [WindowsPaymentBrowserFact]
    public Task Browser_verifies_exact_evidence_recovers_a_lost_response_and_retains_one_cash_event() => Run(false);
    [WindowsPaymentBrowserFact]
    public Task Browser_records_manual_OCR_exception_recovers_each_lost_response_and_verifies_real_bank_receipt() => Run(true);
    private async Task Run(bool manual) {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright)); Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture, manual ? "ocr_unavailable" : null);
        if (manual) await PaymentManualReviewTests.Policy(data);
        var outside = await data.Read.Catalog.ActorAsync(7490, CatalogAuthoringTestData.OtherOwner,
            [Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.PaymentVerify]);
        foreach (var actor in new[] { data.Reviewer, data.Reader, outside })
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Payment verification fixture " + actor.UserId }, { "verifiedEmail", "verification-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", data.Read.Catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Read.Catalog.Clock.UtcNow.UtcDateTime } });
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Read.Catalog.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Read.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Read.Catalog.Sessions);
            services.RemoveAll<InvoiceService>(); services.AddSingleton(data.Read.Payment.Invoice.Service);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Read.Payment.Service);
            services.RemoveAll<PaymentEvidenceService>(); services.AddSingleton(data.Read.Service);
            services.RemoveAll<BankReceiptService>(); services.AddSingleton(data.Bank.Service);
            services.RemoveAll<PaymentVerificationService>(); services.AddSingleton(data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko statement rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            reviewerToken = data.Read.Catalog.Issued[data.Reviewer.UserId].OpaqueToken, readerToken = data.Read.Catalog.Issued[data.Reader.UserId].OpaqueToken,
            otherToken = data.Read.Catalog.Issued[outside.UserId].OpaqueToken, otherId = outside.UserId.ToString(),
            declarationId = data.Read.DeclarationId.ToString(), receiptId = data.ReceiptId.ToString(), imageBase64 = Convert.ToBase64String(data.Read.Bytes), manual };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-payment-verification-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser child did not start");
        try {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5)); output.WriteLine(await stdout); output.WriteLine(await stderr);
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("REAL_PAYMENT_VERIFICATION_BROWSER_PASS", await stdout);
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        Assert.Equal(1, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows(B.Parties).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("paymentAllocations").CountDocumentsAsync(new BsonDocument()));
        var saved = await data.Rows(B.Decisions).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.ReceiptId, saved["receiptId"].AsInt64); Assert.Equal(1, saved["review"]["revision"].AsInt64);
        Assert.Equal(data.Reviewer.UserId, saved["actorId"].AsInt64);
        Assert.Equal(manual ? 2 : 1, saved["schemaVersion"].AsInt32);
        Assert.Equal(manual ? 1 : 0, await data.Rows(PaymentManualReviewRecords.Versions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(manual ? 0 : 1, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        var leases = await data.Rows("evidenceReadLeases").Find(new BsonDocument()).ToListAsync();
        Assert.NotEmpty(leases); Assert.All(leases, row => Assert.Equal("released", row["state"]));
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
