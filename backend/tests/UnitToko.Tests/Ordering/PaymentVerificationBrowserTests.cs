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
using UnitToko.Tests.Documents;
using UnitToko.Platform.DocumentProcessing;
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
    [WindowsPaymentBrowserFact]
    public Task Browser_explicitly_selects_recovered_OCR_after_manual_policy_expires_and_preserves_both_histories() => Run(true, true);
    [WindowsPaymentBrowserFact]
    public Task Browser_claims_releases_and_requests_replacement_recovers_a_lost_response_and_blocks_stale_verification() => Run(false, workflow: true);
    [WindowsPaymentBrowserFact]
    public Task Browser_creates_linked_replacement_recovers_without_resending_and_submits_new_checked_original() => Run(false, workflow: true, replacement: true);
    private async Task Run(bool manual, bool manualRecovery = false, bool workflow = false, bool replacement = false) {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright)); Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture, manual ? "ocr_unavailable" : null);
        if (manual) await PaymentManualReviewTests.Policy(data);
        if (workflow) await PaymentReviewActionTests.Grant(data);
        if (workflow) data.Read.Catalog.Clock.UseSystemClock = true; // Match the actual worker host's clock instead of manufacturing a future receipt.
        if (manualRecovery) {
            var shown = await data.Service.PreviewManualReviewAsync(data.Reviewer, data.Read.DeclarationId);
            await data.Service.RecordManualReviewAsync(data.Reviewer, data.Read.DeclarationId, 0, new(shown.Binding, true, PaymentManualReviewTests.Fields()), PaymentVerificationTestData.Meta("manual-before-browser-recovery"));
            await data.RecoverOcrAsync();
            await data.Rows(PaymentManualReviewRecords.Policies).UpdateManyAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", false));
        }
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
            buyerToken = data.Read.Catalog.Issued[data.Read.Actor.UserId].OpaqueToken,
            declarationId = data.Read.DeclarationId.ToString(), receiptId = data.ReceiptId.ToString(), imageBase64 = Convert.ToBase64String(data.Read.Bytes),
            replacementImageBase64 = replacement ? Convert.ToBase64String(ImageDecoderTests.Png(14, 7)) : null, manual = manual && !manualRecovery, manualRecovery, workflow, replacement };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-payment-verification-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser child did not start");
        var replacementValidated = false; var noticesProcessed = false;
        try {
            var stdout = ReadOutputAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5)); output.WriteLine(await stdout); output.WriteLine(await stderr);
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("REAL_PAYMENT_VERIFICATION_BROWSER_PASS", await stdout);
            if (replacement) { Assert.True(replacementValidated); Assert.Contains("REAL_LINKED_REPLACEMENT_BROWSER_PASS", await stdout); }
            async Task<string> ReadOutputAsync() {
                var lines = new System.Text.StringBuilder();
                while (await process.StandardOutput.ReadLineAsync() is { } line) {
                    lines.AppendLine(line);
                    if (line == "PAYMENT_NOTICE_READY") {
                        Assert.True(workflow); Assert.False(noticesProcessed); noticesProcessed = true;
                        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
                        var workerDll = Path.Combine(root, "backend", "src", "UnitToko.Worker", "bin", configuration, "net10.0", "UnitToko.Worker.dll"); Assert.True(File.Exists(workerDll));
                        var workerStart = new ProcessStartInfo(OutboxProcessTests.FindDotnetHost()) { UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(workerDll)! };
                        workerStart.ArgumentList.Add(workerDll);
                        workerStart.Environment["Mongo__Uri"] = Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0";
                        workerStart.Environment["Mongo__Database"] = data.Read.Database.DatabaseNamespace.DatabaseName;
                        workerStart.Environment["Notifications__PaymentReviewInApp__Enabled"] = "true";
                        workerStart.Environment["Logging__LogLevel__Default"] = "Warning";
                        using var worker = Process.Start(workerStart) ?? throw new InvalidOperationException("Notice worker did not start");
                        var workerOutput = worker.StandardOutput.ReadToEndAsync(); var workerError = worker.StandardError.ReadToEndAsync();
                        try {
                            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                            while (await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument { { "kind", PaymentReviewActionRecords.Operation }, { "status", new BsonDocument("$ne", "completed") } }, cancellationToken: budget.Token) > 0) {
                                Assert.False(worker.HasExited, "Actual notice worker exited before completion"); await Task.Delay(100, budget.Token);
                            }
                        } finally { if (!worker.HasExited) worker.Kill(true); await worker.WaitForExitAsync(); output.WriteLine(await workerOutput); output.WriteLine(await workerError); }
                    }
                    if (line == "REPLACEMENT_VALIDATE_READY") {
                        Assert.True(replacement); Assert.False(replacementValidated); replacementValidated = true;
                        var processor = new EvidenceValidationProcessor(data.Read.Database, new(data.Read.Catalog.Client), new(data.Read.Database), data.Read.Catalog.Clock, data.Read.Files.Registration,
                            EvidenceValidationTests.Consumer, new string('c', 64), EvidenceValidationTests.ClaimPolicy, new(1024, 100, 100, 10000),
                            new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Read.Catalog.Clock.UtcNow.AddHours(-1)), new WindowsImageDecoder(ImageDecoderTests.Options()));
                        Assert.Equal("completed", await processor.RunOnceAsync());
                    }
                }
                return lines.ToString();
            }
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        Assert.Equal(workflow ? 0 : 1, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(workflow ? 0 : 1, await data.Rows(B.Parties).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("paymentAllocations").CountDocumentsAsync(new BsonDocument()));
        if (workflow) {
            Assert.True(noticesProcessed);
            var events = await data.Rows("outboxEvents").Find(new BsonDocument("kind", PaymentReviewActionRecords.Operation)).ToListAsync();
            Assert.Equal(3, events.Count); Assert.All(events, row => Assert.Equal("completed", row["status"]));
            Assert.Single(events, row => row["notificationReceipt"]["disposition"] == "available");
            Assert.Equal(3, await data.Rows(PaymentReviewActionRecords.Versions).CountDocumentsAsync(new BsonDocument()));
            var latest = await data.Service.ReadReviewActionsAsync(data.Reader, data.Read.DeclarationId); Assert.Equal("replacement_requested", latest.Latest!["state"]);
            Assert.Equal(2, latest.Latest["schemaVersion"]); Assert.Equal("Unggah foto bukti asli yang terbaca jelas.", latest.Latest["publicExplanation"]);
            Assert.Equal(latest.Latest["snapshotHash"], Assert.Single(events, row => row["notificationReceipt"]["disposition"] == "available")["notificationReceipt"]["actionHash"]);
            if (replacement) {
                var first = await data.Rows(PaymentDraftBson.Versions).Find(new BsonDocument("schemaVersion", 2)).SingleAsync();
                var child = await data.Read.Payment.Service.ReadAsync(data.Read.Actor, first["declarationId"].AsInt64);
                Assert.Equal("submitted", child.State); Assert.Equal(2, child.Revision); Assert.Equal(4500m, child.Input.DeclaredAmount.Value);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(ImageDecoderTests.Png(14, 7))).ToLowerInvariant(), child.Submission!.Evidence.Sha256);
                Assert.Equal(data.Read.Reference, (await data.Read.Payment.Service.ReadAsync(data.Read.Actor, data.Read.DeclarationId)).Submission!.Evidence);
                Assert.Equal(2, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument()));
            }
            var leasesAfterActions = await data.Rows("evidenceReadLeases").Find(new BsonDocument()).ToListAsync(); Assert.NotEmpty(leasesAfterActions); Assert.All(leasesAfterActions, row => Assert.Equal("released", row["state"])); return;
        }
        var saved = await data.Rows(B.Decisions).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.ReceiptId, saved["receiptId"].AsInt64); Assert.Equal(1, saved["review"]["revision"].AsInt64);
        Assert.Equal(data.Reviewer.UserId, saved["actorId"].AsInt64);
        Assert.Equal(manual && !manualRecovery ? 2 : 1, saved["schemaVersion"].AsInt32);
        Assert.Equal(manual ? 1 : 0, await data.Rows(PaymentManualReviewRecords.Versions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(manual && !manualRecovery ? 0 : 1, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        var leases = await data.Rows("evidenceReadLeases").Find(new BsonDocument()).ToListAsync();
        Assert.NotEmpty(leases); Assert.All(leases, row => Assert.Equal("released", row["state"]));
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
