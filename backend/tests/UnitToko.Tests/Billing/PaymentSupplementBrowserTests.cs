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
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Ordering;
using Xunit.Abstractions;

namespace UnitToko.Tests.Billing;

// Real production Next/.NET/Mongo/private files/native PDF raster+OCR. Accounts,
// provider proofs, D06/host policies and the scanner protocol are synthetic local inputs.
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentSupplementBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    private const string Origin = "https://portal.unit-toko.co.id";

    [WindowsPaymentBrowserFact]
    public Task Actual_browser_recovers_separate_supplement_and_reads_all_native_pages_without_replacing_primary_image_or_money() => RunAsync(false);

    [WindowsPaymentBrowserFact]
    public Task Actual_browser_manually_corrects_image_with_persisted_supplementary_page_reference_and_recovers_one_commit() => RunAsync(true);

    private async Task RunAsync(bool correction)
    {
        var primaryImage = correction ? await PaymentSupplementCorrectionTestData.ImageAsync() : null;
        await using var evidence = await PaymentSupplementEvidenceTestData.CreateAsync(fixture, approve: false,
            maximumContentBytes: PaymentSupplementPdfOcrTestData.MaximumOriginalBytes, primaryImageBytes: primaryImage,
            primaryImageLimits: correction ? new(PaymentSupplementPdfOcrTestData.MaximumOriginalBytes, 1001, 651, 651651) : null,
            maximumReadBytes: correction ? PaymentSupplementPdfOcrTestData.MaximumOriginalBytes : 1024,
            verificationDurationMs: correction ? 60000 : 10000, leaseDurationMs: correction ? 10000 : 5000);
        var data = evidence.Data; await evidence.ApproveAsync();
        var reviewer = correction ? await PaymentCorrectionTests.Corrector(data, 9951) : await PaymentReviewTests.Reviewer(data);
        var foreign = await data.Catalog.ActorAsync(95781, new GrantScope(GrantScopeKind.BuyerOwned, 20, 21), Permissions.PaymentEvidence, Permissions.InvoiceRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Catalog.Clock, reviewer.UserId, foreign.UserId);
        await ProfileAsync(data, data.Actor.UserId, "Pemohon cabang uji lokal");
        await ProfileAsync(data, reviewer.UserId, "Peninjau independen uji lokal");
        await ProfileAsync(data, foreign.UserId, "Pemohon cabang lain uji lokal");
        var unit = new MongoUnitOfWork(data.Catalog.Client);
        await using var scanner = await PdfEvidenceAdmissionTestRuntime.CreateAsync(data.Database, unit, data.Catalog.Clock, data.Files.Root, data.Files.StoreId);
        await PdfDocumentOcrProcessingTestData.ReadPolicyAsync(data.Database, data.Files.StoreId);
        if (correction) {
            await PaymentSupplementCorrectionTestData.ApplyAsync(data.Database, data.Catalog.Clock);
            var imageWorker = PaymentOcrProcessorTests.Processor(data, new WindowsLocalOcrEngine(LocalOcrTests.Options()));
            await PaymentOcrProcessorTests.Policy(data, imageWorker); Assert.Equal("completed", await imageWorker.RunOnceAsync());
            var extraction = await data.Service.ReadExtractionAsync(data.Actor, data.DeclarationId, 1, data.Reference, new("payment-ocr-view", "native-text-primary-browser"));
            Assert.Equal("succeeded", extraction.State); Assert.Equal("1234567.89", Assert.Single(extraction.Extraction!.Fields, field => field.Field == "amount").Value);
        }
        var runtime = PdfDocumentOcrRuntime.CreatePaymentSupplement(data.Database, unit, new(data.Database), data.Catalog.Clock,
            PdfDocumentOcrProcessingTestData.Options(data.Files.Root, data.Files.StoreId, PaymentSupplementPdfOcrTestData.Consumer));
        await PaymentSupplementPdfOcrTestData.ApproveProcessingAsync(data.Database, data.Catalog.Clock, data.Files.StoreId, runtime);
        var before = await evidence.FinancialAsync();
        var primaryPins = new BsonArray(await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "PaymentSubmission")).ToListAsync());
        var bytes = PdfDocumentOcrProcessingTestData.Pdf(ExtractionPurpose.Payment);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Release:Profile"] = "full";
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Catalog.Sessions);
            services.RemoveAll<InvoiceService>(); services.AddSingleton(data.Payment.Invoice.Service);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Payment.Service);
            services.RemoveAll<PaymentEvidenceService>(); services.AddSingleton(data.Service);
            services.RemoveAll<PaymentSupplementEvidenceService>(); services.AddSingleton(evidence.Facade);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        using var rsa = RSA.Create(2048);
        var certificate = new CertificateRequest("CN=UnitToko supplementary PDF local acceptance", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(Origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var runDirectory = Path.GetFullPath(Path.Combine(root, "tmp", "production", "payment-supplement-browser", Guid.NewGuid().ToString("N")));
        Assert.StartsWith(Path.Combine(root, "tmp", "production") + Path.DirectorySeparatorChar, runDirectory, StringComparison.Ordinal);
        Directory.CreateDirectory(runDirectory);
        var validationReadyPath = Path.Combine(runDirectory, "validated.txt"); var extractionReadyPath = Path.Combine(runDirectory, "extracted.txt");
        var input = new { apiAddress, origin = Origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            spki = Convert.ToBase64String(SHA256.HashData(tls.PublicKey.ExportSubjectPublicKeyInfo())), playwrightPackage = playwright,
            actorToken = data.Catalog.Issued[data.Actor.UserId].OpaqueToken, reviewerToken = data.Catalog.Issued[reviewer.UserId].OpaqueToken,
            outsiderToken = data.Catalog.Issued[foreign.UserId].OpaqueToken,
            actorId = Number(data.Actor.UserId), reviewerId = Number(reviewer.UserId), outsiderId = Number(foreign.UserId),
            declarationId = Number(evidence.Primary.DeclarationId), declarationRevision = Number(evidence.Primary.DeclarationRevision),
            submissionId = Number(evidence.Primary.SubmissionId), primaryReservationId = Number(data.Reservation.ReservationId),
            primaryEvidence = new { id = Number(data.Reference.Id), version = "1", sha256 = data.Reference.Sha256 },
            imageBase64 = Convert.ToBase64String(data.Bytes), pdfBase64 = Convert.ToBase64String(bytes), pdfSha256 = PdfRasterProtocol.Digest(bytes),
            imageWidth = correction ? 1000 : 13, imageHeight = correction ? 650 : 7,
            pdfFilename = "lampiran-sintetis-dua-halaman.pdf", runDirectory, validationReadyPath, extractionReadyPath };
        var secrets = new[] { input.actorToken, input.reviewerToken, input.outsiderToken, input.pfx, input.password, data.Files.Root };
        string Safe(string line) { foreach (var secret in secrets) line = line.Replace(secret, "[private]", StringComparison.Ordinal); return line; }
        var logPath = Path.Combine(runDirectory, "native-child.log"); var lines = new StringBuilder(); var validated = false; var extracted = false;
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", correction ? "verify-payment-supplement-correction-browser.mjs" : "verify-payment-supplement-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Supplement browser child did not start");
        async Task ReadAsync(StreamReader stream, bool events) {
            while (await stream.ReadLineAsync() is { } original) {
                var line = Safe(original); lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); }
                if (events && original == "SUPPLEMENT_PDF_UPLOADED") {
                    Assert.False(validated); validated = true;
                    await PaymentSupplementPdfOcrTestData.RequireQueuedValidationAsync(data, bytes);
                    Assert.Equal("completed", await scanner.Runtime.RunOnceAsync());
                    await File.WriteAllTextAsync(validationReadyPath, "Synthetic scanner protocol and actual pinned whole-PDF raster validation completed");
                }
                if (events && original == "SUPPLEMENT_PDF_ATTACHED") {
                    Assert.True(validated); Assert.False(extracted); extracted = true; Assert.Equal("completed", await runtime.RunOnceAsync());
                    await File.WriteAllTextAsync(extractionReadyPath, "Actual pinned native OCR processed every page of the separate immutable supplementary original");
                }
            }
        }
        var stdout = ReadAsync(process.StandardOutput, true); var stderr = ReadAsync(process.StandardError, false);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            var exit = process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(8)); var first = await Task.WhenAny(exit, stdout, stderr); if (first.IsFaulted) await first;
            await exit; await Task.WhenAll(stdout, stderr);
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains(correction ? "REAL_PAYMENT_SUPPLEMENT_CORRECTION_BROWSER_PASS" : "REAL_PAYMENT_SUPPLEMENT_BROWSER_PASS", lines.ToString());
            Assert.True(validated); Assert.True(extracted);
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString()); output.WriteLine("Supplement browser proof: " + runDirectory);
        }
        using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDirectory, "proof.json"))); var saved = proof.RootElement;
        Assert.Equal("local-synthetic-inputs-actual-native-runtime", saved.GetProperty("acceptance").GetString());
        var supplementId = long.Parse(saved.GetProperty("supplementId").GetString()!, CultureInfo.InvariantCulture);
        var reservationId = long.Parse(saved.GetProperty("reservationId").GetString()!, CultureInfo.InvariantCulture);
        var resultId = long.Parse(saved.GetProperty("resultId").GetString()!, CultureInfo.InvariantCulture);
        var attachment = await data.Rows(PaymentSupplementBson.Attachments).Find(new BsonDocument("_id", supplementId)).SingleAsync();
        Assert.Equal(PaymentSupplementBson.Primary(evidence.Primary), attachment["primary"]); Assert.Equal(reservationId, attachment["reservationId"]);
        Assert.Equal(PdfRasterProtocol.Digest(bytes), attachment["evidence"]["sha256"]); Assert.Equal(2L, attachment["supplementRevision"]);
        Assert.Equal(1L, await data.Rows(PaymentSupplementBson.Heads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, await data.Rows(PaymentSupplementBson.Versions).CountDocumentsAsync(new BsonDocument()));
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("kind", PaymentSupplementBson.JobKind)).SingleAsync();
        Assert.Equal("completed", job["status"]); Assert.Equal(PaymentSupplementBson.JobKey(supplementId), job["sourceKey"]);
        Assert.Equal(PaymentSupplementBson.JobPayload(attachment), job["payload"]);
        var result = await unit.ExecuteAsync((tx, ct) => new PdfDocumentExtractionRecords(data.Database).ResultAsync(tx, job, ct)); Assert.NotNull(result);
        Assert.Equal(resultId, result["_id"]); Assert.Equal(saved.GetProperty("resultHash").GetString(), result["resultHash"]);
        Assert.Equal(2, result["pageCount"]); Assert.Equal("PaymentSupplement", result["sourceKind"]); Assert.Equal("Payment", result["purpose"]);
        Assert.Equal(supplementId, result["binding"]["submissionId"]); Assert.Equal(supplementId, result["binding"]["supplementId"]);
        Assert.Equal(attachment["snapshotHash"], result["binding"]["submissionHash"]); Assert.Equal(attachment["primary"], result["binding"]["primary"]);
        Assert.Equal(PdfDocumentExtractionRecords.Receipt(result), job["processingReceipt"]);
        foreach (var operation in new[] { PaymentSupplementBson.CreateOperation, PaymentSupplementBson.AttachOperation, "evidence.payment-supplement-pdf.reserve" })
            Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", operation)));
        foreach (var reader in new[] { data.Actor.UserId, reviewer.UserId })
            Assert.True(await data.Rows(PdfDocumentExtractionRecords.Reads).CountDocumentsAsync(new BsonDocument("actorId", reader)) >= 2);
        Assert.False(await data.Rows(PdfDocumentExtractionRecords.Reads).Find(new BsonDocument("actorId", foreign.UserId)).AnyAsync());
        var leases = await data.Rows("evidenceReadLeases").Find(new BsonDocument("reservationId", reservationId)).ToListAsync();
        Assert.NotEmpty(leases); Assert.All(leases, lease => Assert.Equal("released", lease["state"]));
        Assert.Equal(bytes, scanner.Scanner.Bytes); Assert.Equal(before, await evidence.FinancialAsync());
        Assert.Equal(primaryPins, new BsonArray(await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "PaymentSubmission")).ToListAsync()));
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", "payment.document-ocr.requested")).AnyAsync());
        if (correction) {
            var version = await data.Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument()).SingleAsync();
            Assert.Equal(2, version["schemaVersion"].AsInt32); Assert.Equal(reviewer.UserId, version["recordedBy"]);
            var reference = Assert.Single(PaymentCorrectionBson.Observations(version)!.Value);
            Assert.Equal(supplementId, reference.SupplementId); Assert.Equal(2, reference.SupplementRevision);
            Assert.Equal(resultId, reference.Candidate.ResultId); Assert.Equal(result["resultHash"].AsString, reference.Candidate.ResultHash);
            Assert.Equal(1, reference.Candidate.PageIndex); Assert.Equal("amount", reference.Candidate.Field);
            Assert.Equal(PdfRasterProtocol.Digest(bytes), reference.Candidate.Evidence.Sha256); Assert.True(reference.OriginalReviewed);
            Assert.Equal(data.Reference, PaymentCorrectionBson.Binding(version["binding"].AsBsonDocument).Evidence);
            Assert.Equal("4000.00", Assert.Single(PaymentCorrectionBson.Fields(version["fields"].AsBsonArray), field => field.Field == "amount").Value);
            Assert.Equal("1234567.89", Assert.Single(PaymentCorrectionBson.Fields(version["beforeFields"].AsBsonArray), field => field.Field == "amount").Value);
            Assert.Equal(PaymentCorrectionBson.Hash(version), version["snapshotHash"]);
            Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", PaymentCorrectionBson.Operation)));
            Assert.Equal(1L, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", PaymentCorrectionBson.Operation)));
        } else Assert.False(await data.Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument()).AnyAsync());
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static async Task ProfileAsync(PaymentEvidenceReadTests.ReadData data, long id, string name) {
        if (await data.Rows("accountProfiles").Find(new BsonDocument("_id", id)).AnyAsync()) return;
        await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", name },
            { "verifiedEmail", "supplement-" + Number(id) + "@example.invalid" }, { "verifiedAt", data.Catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Catalog.Clock.UtcNow.UtcDateTime } });
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
