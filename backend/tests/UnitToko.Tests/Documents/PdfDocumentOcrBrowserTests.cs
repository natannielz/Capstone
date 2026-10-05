using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Ordering;
using UnitToko.Tests.Procurement;
using Xunit.Abstractions;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PdfDocumentOcrBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    private const string Origin = "https://portal.unit-toko.co.id";
    [WindowsPaymentBrowserFact]
    public async Task Actual_supplier_browser_uploads_PDF_and_scoped_staff_read_every_page_without_a_financial_or_stock_effect() {
        await using var data = await SupplierDocumentTestData.CreateAsync(fixture);
        await using var scanner = await PdfEvidenceAdmissionTestRuntime.CreateAsync(data.Database, data.Unit, data.Clock, data.Files.Path, ProcurementInspectionTestData.StoreId);
        await PdfDocumentOcrProcessingTestData.ReadPolicyAsync(data.Database, ProcurementInspectionTestData.StoreId);
        var runtime = PdfDocumentOcrRuntime.CreateSupplierInvoice(data.Database, data.Unit, new(data.Database), data.Clock,
            PdfDocumentOcrProcessingTestData.Options(data.Files.Path, ProcurementInspectionTestData.StoreId, PdfDocumentOcrProcessingTestData.SupplierConsumer));
        await PdfDocumentOcrProcessingTestData.PolicyAsync(data.Database, data.Clock, ProcurementInspectionTestData.StoreId, runtime);
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"),
            SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var actorToken = await IssueAsync(data.Database, data.Unit, data.Clock, sessions, data.Actor, "Pencatat pemasok uji PDF");
        var reviewerToken = await IssueAsync(data.Database, data.Unit, data.Clock, sessions, data.EvidenceReader, "Pemeriksa dokumen pemasok uji PDF");
        var outsiderToken = await IssueAsync(data.Database, data.Unit, data.Clock, sessions, data.Outsider, "Lingkup lain uji PDF");
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = Settings(data.Database, keys);
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<SupplierDocumentService>(); services.AddSingleton(data.Service); services.RemoveAll<SupplierDocumentEvidenceService>(); services.AddSingleton(data.Evidence);
        }));
        var observed = data.Input(); var bytes = PdfDocumentOcrProcessingTestData.Pdf(ExtractionPurpose.SupplierInvoice);
        var before = await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database);
        var input = new Dictionary<string, object?> { ["mode"] = "supplier", ["sourceId"] = data.DraftId.ToString(), ["sourceRevision"] = "1",
            ["actorToken"] = actorToken, ["actorId"] = data.Actor.UserId.ToString(), ["reviewerToken"] = reviewerToken, ["reviewerId"] = data.EvidenceReader.UserId.ToString(),
            ["outsiderToken"] = outsiderToken, ["outsiderId"] = data.Outsider.UserId.ToString(), ["anchorOrderId"] = observed.AnchorOrderId.ToString(),
            ["anchorOrderApprovalId"] = observed.AnchorOrderApprovalId.ToString(), ["documentReference"] = observed.DocumentReference,
            ["locationKey"] = $"{observed.Location.OrganizationId}:{observed.Location.ShopId}:{observed.Location.WarehouseId}:{observed.Location.ChannelId}",
            ["expectedSourceKind"] = "SupplierInvoice", ["expectedAmountField"] = "total_amount" };
        await ChildAsync(factory, input, bytes, data.Files.Path, scanner.Runtime, runtime);
        await DurableAsync(data.Database, data.Unit, "SupplierInvoice", data.DraftId, data.Actor.UserId, data.EvidenceReader.UserId, PdfRasterProtocol.Digest(bytes));
        Assert.Equal(bytes, scanner.Scanner.Bytes); Assert.Equal(before, await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database));
        Assert.Equal(0, await data.Rows("supplierDocumentMatches").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsPaymentBrowserFact]
    public async Task Actual_supplier_browser_manually_matches_with_persisted_native_page_reference_and_recovers_one_commit() {
        await using var fixtureData = await SupplierDocumentPdfMatchTestData.CreateAsync(fixture, submit: false);
        var data = fixtureData.Data;
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"),
            SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var actorToken = await IssueAsync(data.Database, data.Unit, data.Clock, sessions, data.Actor, "Pencatat invoice PDF uji");
        var reviewerToken = await IssueAsync(data.Database, data.Unit, data.Clock, sessions, data.Matcher, "Petugas pencocokan invoice PDF uji");
        var outsiderToken = await IssueAsync(data.Database, data.Unit, data.Clock, sessions, data.Outsider, "Lingkup lain uji PDF");
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = Settings(data.Database, keys);
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<SupplierDocumentService>(); services.AddSingleton(data.Service); services.RemoveAll<SupplierDocumentEvidenceService>(); services.AddSingleton(data.Evidence);
        }));
        var observed = fixtureData.Input;
        var notice = await data.Inspection.Receiving.Service.ReadAsync(data.Inspection.Receiving.Reader, data.Inspection.NoticeId);
        var input = new Dictionary<string, object?> { ["mode"] = "supplier", ["sourceId"] = data.DraftId.ToString(), ["sourceRevision"] = "1",
            ["actorToken"] = actorToken, ["actorId"] = data.Actor.UserId.ToString(), ["reviewerToken"] = reviewerToken, ["reviewerId"] = data.Matcher.UserId.ToString(),
            ["outsiderToken"] = outsiderToken, ["outsiderId"] = data.Outsider.UserId.ToString(), ["documentReference"] = observed.DocumentReference,
            ["locationKey"] = $"{observed.Location.OrganizationId}:{observed.Location.ShopId}:{observed.Location.WarehouseId}:{observed.Location.ChannelId}",
            ["expectedSourceKind"] = "SupplierInvoice", ["expectedAmountField"] = "total_amount", ["inspectionId"] = data.InspectionId.ToString(),
            ["documentInput"] = UnitToko.Api.Procurement.SupplierDocumentEndpoints.Input(SupplierDocumentBson.Input(observed)),
            ["packageLabel"] = notice["notice"]["order"]["lines"][0]["packageLabel"].AsString };
        await ChildAsync(factory, input, fixtureData.Bytes, data.Files.Path, fixtureData.Scanner.Runtime, fixtureData.Runtime,
            "verify-supplier-pdf-match-browser.mjs", "REAL_SUPPLIER_PDF_MATCH_BROWSER_PASS", ["34000.00", "34000.00"]);
        await DurableAsync(data.Database, data.Unit, "SupplierInvoice", data.DraftId, data.Actor.UserId, data.Matcher.UserId, PdfRasterProtocol.Digest(fixtureData.Bytes));
        Assert.Equal(fixtureData.Bytes, fixtureData.Scanner.Scanner.Bytes);
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("kind", PdfDocumentExtractionContract.SupplierJobKind)).SingleAsync();
        var result = await data.Unit.ExecuteAsync((tx, ct) => new PdfDocumentExtractionRecords(data.Database).ResultAsync(tx, job, ct)); Assert.NotNull(result);
        var submission = await data.Rows(SupplierDocumentBson.Submissions).Find(new BsonDocument("_id", result["binding"]["submissionId"])).SingleAsync();
        var row = await data.Rows(SupplierDocumentBson.Matches).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(3, row["schemaVersion"]); Assert.Equal(data.Matcher.UserId, row["actorId"]); Assert.True(row["input"]["ocrResultId"].IsBsonNull);
        var observation = Assert.Single(row["input"]["pdfObservations"].AsBsonArray);
        Assert.Equal("total_amount", observation["field"]); Assert.True(observation["originalReviewed"].AsBoolean);
        Assert.Equal(result["_id"], observation["candidate"]["resultId"]); Assert.Equal(result["resultHash"], observation["candidate"]["resultHash"]);
        Assert.Equal(1, observation["candidate"]["pageIndex"]); Assert.Equal(submission["evidence"], observation["candidate"]["evidence"]);
        Assert.Equal(submission["_id"], row["documentId"]); Assert.Equal(submission["snapshotHash"], row["documentHash"]);
        Assert.Equal(SupplierDocumentBson.Input(observed), row["input"]["reviewedInput"]);
        Assert.Equal(2, row["input"]["lines"][0]["packages"]); Assert.Equal("20000.00", row["input"]["lines"][0]["netAmount"]);
        Assert.Equal("2000.00", row["input"]["lines"][0]["taxAmount"]); Assert.Equal("500.00", row["input"]["lines"][0]["chargeAmount"]);
        Assert.Equal(1L, row["after"]["lines"][0]["remainingPackages"]);
        var audit = await data.Rows("auditEvents").Find(new BsonDocument("_id", row["auditId"])).SingleAsync();
        Assert.Equal(SupplierDocumentBson.Audit(row), audit);
        var read = await data.Rows(PdfDocumentExtractionRecords.Reads).Find(new BsonDocument {
            { "actorId", data.Matcher.UserId }, { "correlationId", row["correlationId"] }, { "resultId", result["_id"] } }).SingleAsync();
        Assert.Equal("supplier-document-pdf-ocr-view", read["purpose"]); Assert.Equal(result["resultHash"], read["resultHash"]);
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            Assert.Equal(1L, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, SupplierDocumentTestData.MatchOperation)));
        var prior = fixtureData.Baseline.DeepClone().AsBsonDocument; prior.Remove("supplierDocumentSubmissions");
        var after = await fixtureData.PhysicalAndMoneyAsync(); after.Remove("supplierDocumentSubmissions");
        Assert.Equal(prior, after); Assert.Equal(0L, await data.Rows("supplierDocumentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(SupplierDocumentBson.Submissions).CountDocumentsAsync(new BsonDocument()));
        await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    private async Task ChildAsync(WebApplicationFactory<Program> factory, Dictionary<string, object?> input, byte[] bytes, string privateRoot,
        PdfEvidenceValidationRuntime security, PdfDocumentOcrRuntime extraction, string script = "verify-pdf-document-ocr-browser.mjs",
        string successMarker = "REAL_PDF_DOCUMENT_BROWSER_PASS", string[]? expectedAmounts = null) {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID"))); Assert.True(File.Exists(Path.Combine(root, "frontend", "scripts", script)));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko private PDF rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(Origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password));
        var runDirectory = Path.GetFullPath(Path.Combine(root, "tmp", "production", "pdf-document-browser", input["mode"] + "-" + Guid.NewGuid().ToString("N")));
        if (!runDirectory.StartsWith(Path.Combine(root, "tmp", "production") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected browser artifact location");
        Directory.CreateDirectory(runDirectory); var logPath = Path.Combine(runDirectory, "native-child.log");
        var validationReadyPath = Path.Combine(runDirectory, "validated.txt"); var extractionReadyPath = Path.Combine(runDirectory, "extracted.txt");
        input["apiAddress"] = apiAddress; input["origin"] = Origin; input["pfx"] = pfx; input["password"] = password;
        input["spki"] = Convert.ToBase64String(SHA256.HashData(tls.PublicKey.ExportSubjectPublicKeyInfo())); input["playwrightPackage"] = playwright;
        input["pdfBase64"] = Convert.ToBase64String(bytes); input["pdfSha256"] = PdfRasterProtocol.Digest(bytes); input["pdfFilename"] = "bukti-uji-dua-halaman.pdf";
        input["expectedAmounts"] = expectedAmounts ?? ["1234567.89", "2345678.90"]; input["runDirectory"] = runDirectory;
        input["validationReadyPath"] = validationReadyPath; input["extractionReadyPath"] = extractionReadyPath;
        var secrets = input.Where(pair => pair.Key.EndsWith("Token", StringComparison.Ordinal) || pair.Key is "pfx" or "password").Select(pair => pair.Value?.ToString()).OfType<string>().Append(privateRoot).ToArray();
        string Safe(string line) { foreach (var secret in secrets) line = line.Replace(secret, "[private]", StringComparison.Ordinal); return line; }
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", script));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PDF browser child did not start");
        var lines = new StringBuilder(); var validated = false; var extracted = false;
        async Task ReadAsync(StreamReader stream, bool events) {
            while (await stream.ReadLineAsync() is { } original) {
                var line = Safe(original); lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); }
                if (events && original == "PDF_UPLOADED") {
                    Assert.False(validated); validated = true; Assert.Equal("completed", await security.RunOnceAsync());
                    await File.WriteAllTextAsync(validationReadyPath, "Named synthetic scanner protocol and actual pinned whole-PDF raster validation completed");
                }
                if (events && original == "PDF_SUBMITTED") {
                    Assert.True(validated); Assert.False(extracted); extracted = true; Assert.Equal("completed", await extraction.RunOnceAsync());
                    await File.WriteAllTextAsync(extractionReadyPath, "Actual pinned native OCR processed every page of the immutable private original");
                }
            }
        }
        var stdout = ReadAsync(process.StandardOutput, true); var stderr = ReadAsync(process.StandardError, false);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            var exit = process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6)); var first = await Task.WhenAny(exit, stdout, stderr); if (first.IsFaulted) await first;
            await exit; await Task.WhenAll(stdout, stderr);
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains(successMarker, lines.ToString()); Assert.True(validated); Assert.True(extracted);
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString()); output.WriteLine("Actual private PDF browser artifact log: " + logPath);
        }
    }
    private static async Task DurableAsync(IMongoDatabase database, MongoUnitOfWork unit, string sourceKind, long sourceId, long actorId, long reviewerId, string hash) {
        Assert.Equal("SupplierInvoice", sourceKind); var purpose = "SupplierDocumentPdf";
        var reservation = await database.GetCollection<BsonDocument>("evidenceUploadReservations").Find(new BsonDocument { { "purpose", purpose }, { "target.id", sourceId } }).SingleAsync();
        var job = await database.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument("kind", PdfDocumentExtractionRecords.Kind(sourceKind))).SingleAsync();
        Assert.Equal("completed", job["status"]); Assert.Equal(hash, job["payload"]["original"]["sha256"]); Assert.Equal(reservation["_id"], job["payload"]["reservationId"]);
        var result = await unit.ExecuteAsync((tx, ct) => new PdfDocumentExtractionRecords(database).ResultAsync(tx, job, ct)); Assert.NotNull(result);
        Assert.Equal(2, result["pageCount"]); Assert.Equal(hash, result["binding"]["reference"]["sha256"]); Assert.Equal(actorId, result["binding"]["uploaderId"]);
        Assert.Equal(PdfDocumentExtractionRecords.Receipt(result), job["processingReceipt"]);
        var submission = await database.GetCollection<BsonDocument>("supplierDocumentSubmissions").Find(new BsonDocument("_id", result["binding"]["submissionId"])).SingleAsync();
        var pin = await database.GetCollection<BsonDocument>("evidencePins").Find(new BsonDocument("_id", result["binding"]["pinId"])).SingleAsync();
        Assert.Equal(submission["_id"], pin["associationId"]); Assert.Equal(actorId, pin["actorId"]); Assert.Equal(hash, pin["reference"]["sha256"]);
        var proofJob = await database.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument { { "kind", PdfEvidenceValidationRecords.Kind }, { "payload.reservationId", reservation["_id"] } }).SingleAsync();
        Assert.Equal("completed", proofJob["status"]);
        foreach (var owner in new[] { PdfDocumentExtractionRecords.Results, PdfDocumentExtractionRecords.Audits, PdfDocumentExtractionRecords.ProcessingReads })
            Assert.Equal(1, await database.GetCollection<BsonDocument>(owner).CountDocumentsAsync(new BsonDocument()));
        foreach (var reader in new[] { actorId, reviewerId }) Assert.True(await database.GetCollection<BsonDocument>(PdfDocumentExtractionRecords.Reads).CountDocumentsAsync(new BsonDocument("actorId", reader)) >= 1);
        var leases = await database.GetCollection<BsonDocument>("evidenceReadLeases").Find(new BsonDocument("reservationId", reservation["_id"])).ToListAsync();
        Assert.NotEmpty(leases); Assert.All(leases, lease => Assert.Equal("released", lease["state"]));
        Assert.Equal(0, await database.GetCollection<BsonDocument>("outboxEvents").CountDocumentsAsync(new BsonDocument { { "kind", SupplierDocumentExtractionRecords.Kind } }));
    }
    private static Dictionary<string, string?> Settings(IMongoDatabase database, IdentityRuntimeTests.RuntimeKeys keys) {
        var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = database.DatabaseNamespace.DatabaseName; settings["Release:Profile"] = "full";
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning"; return settings;
    }
    private static async Task<string> IssueAsync(IMongoDatabase database, MongoUnitOfWork unit, IClock clock, MongoIdentitySessionStore sessions, ActorContext actor, string name) {
        await database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), new BsonDocument("$set", new BsonDocument("securityVersion", 1L)));
        var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "pdf-" + actor.UserId + "@example.invalid", true, "fixture-connection", clock.UtcNow, clock.UtcNow, "otp", "fixture-policy");
        var issue = await unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-pdf-browser-no-provider-call", ct));
        await ProfileAsync(database, clock, actor.UserId, name); return issue.OpaqueToken;
    }
    private static async Task ProfileAsync(IMongoDatabase database, IClock clock, long id, string name) {
        if (await database.GetCollection<BsonDocument>("accountProfiles").Find(new BsonDocument("_id", id)).AnyAsync()) return;
        await database.GetCollection<BsonDocument>("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", name },
            { "verifiedEmail", "pdf-" + id + "@example.invalid" }, { "verifiedAt", clock.UtcNow.UtcDateTime }, { "updatedAt", clock.UtcNow.UtcDateTime } });
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run the PDF browser rehearsal from the authoritative checkout");
    }
}
