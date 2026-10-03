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
using UnitToko.Tests.Documents;
using D = UnitToko.Tests.Procurement.SupplierDocumentTestData;


namespace UnitToko.Tests.Procurement;

// Genuine producer history with explicitly synthetic people, verification policy, sessions and commercial document.
[Collection("Mongo")]
public sealed class SupplierDocumentBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_records_private_invoice_runs_native_ocr_and_reviews_partial_match_with_original_key_recovery()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This native browser rehearsal requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await D.CreateAsync(fixture, createDraft: false);
        var ocrRuntime = await SupplierDocumentOcrTestData.PrepareNativeAsync(data); data.Clock.UseSystemClock = true;
        var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"), protector);
        var issued = new Dictionary<long, SessionIssue>();
        foreach (var actor in new[] { data.Actor, data.Reader, data.EvidenceReader, data.Matcher, data.Outsider }) {
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L)); var now = data.Clock.UtcNow;
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "po-" + actor.UserId + "@example.invalid", true, "fixture-connection", now, now, "otp", "fixture-policy");
            issued[actor.UserId] = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-po-provider", ct));
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", "Synthetic supplier document participant " + actor.UserId },
                { "verifiedEmail", "po-" + actor.UserId + "@example.invalid" }, { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
        }
        var agedAt = data.Clock.UtcNow.AddMinutes(-6);
        var agedProof = new ProviderIdentityProof(data.Actor.ProviderIssuer, data.Actor.ProviderSubject, "po-" + data.Actor.UserId + "@example.invalid", true,
            "fixture-connection", agedAt, agedAt, "otp", "fixture-policy");
        // Create valid evidence at its real fixture time, then resolve it through the advancing browser clock without renewing factor assurance.
        var historicalSessions = new MongoIdentitySessionStore(data.Database, data.Unit, new UnitToko.Tests.Authorization.MutableAuthorizationClock(agedAt),
            new("https://approved.example/", "fixture-connection", "fixture-policy"), protector);
        var agedSession = await data.Unit.ExecuteAsync((tx, ct) => historicalSessions.CreateAsync(tx,
            new(data.Actor.UserId, 1, data.Actor.GrantVersion, data.Actor.ProviderIssuer, data.Actor.ProviderSubject), agedProof, "synthetic-inspection-aged-factor", ct));
        Assert.Equal(agedAt, agedSession.Session.CreatedAt);
        var currentAgedSession = Assert.IsType<IdentitySession>(await sessions.ReadAsync(agedSession.OpaqueToken));
        Assert.Equal(agedAt, currentAgedSession.Proof.FactorCompletedAt);
        var custody = await data.Inspection.Receiving.StateAsync(); var orders = await data.Inspection.Receiving.Orders.StateAsync();
        var inspectionBefore = await data.Inspection.Rows(ProcurementInspectionTestData.Submissions).Find(new BsonDocument()).ToListAsync();
        var sourceNotice = await data.Inspection.Receiving.Service.ReadAsync(data.Inspection.Receiving.Reader, data.Inspection.NoticeId);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<SupplierDocumentService>(); services.AddSingleton(data.Service);
            services.RemoveAll<SupplierDocumentEvidenceService>(); services.AddSingleton(data.Evidence);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses); const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko supplier document rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var run = "supplier-document-" + Guid.NewGuid().ToString("N"); var logPath = Path.Combine(directory, run + "-native-child.log");
        var imagePath = Path.Combine(directory, run + ".png"); var validationReadyPath = Path.Combine(directory, run + "-validated.txt"); var extractionReadyPath = Path.Combine(directory, run + "-extracted.txt");
        await File.WriteAllBytesAsync(imagePath, data.Bytes); var observed = data.Input();
        static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var input = new { supplierDocument = true, apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = issued[data.Actor.UserId].OpaqueToken, makerToken = issued[data.Actor.UserId].OpaqueToken, matcherToken = issued[data.Matcher.UserId].OpaqueToken,
            readerToken = issued[data.Reader.UserId].OpaqueToken, evidenceReaderToken = issued[data.EvidenceReader.UserId].OpaqueToken,
            outsiderToken = issued[data.Outsider.UserId].OpaqueToken, agedToken = agedSession.OpaqueToken,
            makerId = data.Actor.UserId.ToString(), readerId = data.Reader.UserId.ToString(), evidenceReaderId = data.EvidenceReader.UserId.ToString(), outsiderId = data.Outsider.UserId.ToString(),
            documentInput = new { location = new { organizationId = observed.Location.OrganizationId.ToString(), shopId = observed.Location.ShopId.ToString(), warehouseId = observed.Location.WarehouseId.ToString(), channelId = observed.Location.ChannelId.ToString() },
                anchorOrderId = observed.AnchorOrderId.ToString(), anchorOrderVersion = observed.AnchorOrderVersion.ToString(), anchorOrderApprovalId = observed.AnchorOrderApprovalId.ToString(), kind = "invoice",
                documentReference = observed.DocumentReference, documentDate = observed.DocumentDate is { } date ? Day(date) : null, currency = observed.Currency, documentTotal = observed.DocumentTotal?.ToString(),
                lines = observed.Lines.Select(line => new { sourceLineReference = line.SourceLineReference, description = line.Description, sourceSystem = line.SourceSystem, sourceAccount = line.SourceAccount, sourceCode = line.SourceCode,
                    packages = line.Packages, netPricePerPackage = line.NetPricePerPackage?.ToString(), taxAmount = line.TaxAmount?.ToString(), chargeAmount = line.ChargeAmount?.ToString(), relatedInvoiceReference = line.RelatedInvoiceReference, statementAmount = line.StatementAmount?.ToString() }).ToArray(), discrepancy = observed.Discrepancy },
            inspectionId = data.InspectionId.ToString(), packageLabel = sourceNotice["notice"]["order"]["lines"][0]["packageLabel"].AsString, imagePath, validationReadyPath, extractionReadyPath,
            originalSha256 = Convert.ToHexString(SHA256.HashData(data.Bytes)).ToLowerInvariant(), originalLength = data.Bytes.Length };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Supplier document browser child did not start");
        var lines = new StringBuilder(); var validationStarted = false; var extractionStarted = false;
        async Task ReadAsync(StreamReader stream) {
            while (await stream.ReadLineAsync() is { } line) {
                lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); }
                if (line == "VALIDATE_SUPPLIER_DOCUMENT_EVIDENCE_READY") {
                    Assert.False(validationStarted); validationStarted = true; await data.ValidateAsync();
                    await File.WriteAllTextAsync(validationReadyPath, "Synthetic loopback scanner protocol and actual native image decoder completed");
                }
                if (line == "EXTRACT_SUPPLIER_DOCUMENT_READY") {
                    Assert.False(extractionStarted); extractionStarted = true; Assert.Equal("completed", await ocrRuntime.RunOnceAsync());
                    await File.WriteAllTextAsync(extractionReadyPath, "Actual locally pinned OCR worker consumed the genuine submitted private original");
                }
            }
        }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            // Four independent lost-response recoveries plus image validation and native OCR run through real scoped reads.
            var exit = process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6)); var first = await Task.WhenAny(exit, stdout, stderr); if (first.IsFaulted) await first;
            await exit; await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_SUPPLIER_DOCUMENT_BROWSER_PASS", lines.ToString()); Assert.True(validationStarted); Assert.True(extractionStarted);
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            try { await Task.WhenAll(stdout, stderr); } finally { output.WriteLine("Actual browser child log: " + logPath); }
        }
        Assert.Equal(custody, await data.Inspection.Receiving.StateAsync()); Assert.Equal(orders, await data.Inspection.Receiving.Orders.StateAsync());
        await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
        Assert.Equal(inspectionBefore, await data.Inspection.Rows(ProcurementInspectionTestData.Submissions).Find(new BsonDocument()).ToListAsync());
        var draft = await data.Rows("supplierDocumentDrafts").Find(new BsonDocument()).SingleAsync(); var submitted = await data.Rows("supplierDocumentSubmissions").Find(new BsonDocument()).SingleAsync();
        var matched = await data.Rows("supplierDocumentMatches").Find(new BsonDocument()).SingleAsync(); var pin = await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "SupplierDocument")).SingleAsync();
        Assert.Equal(data.Actor.UserId, submitted["actorId"].AsInt64); Assert.Equal(submitted["_id"], draft["submittedDocumentId"]); Assert.Equal(submitted["pinId"], pin["_id"]);
        Assert.Equal(submitted["_id"], pin["associationId"]); Assert.Equal(submitted["snapshotHash"], pin["associationHash"]); Assert.Equal(submitted["evidence"], pin["reference"]);
        Assert.Equal(2L, matched["after"]["lines"][0]["matchedPackages"].AsInt64); Assert.Equal(1L, matched["after"]["lines"][0]["remainingPackages"].AsInt64);
        Assert.Equal("20000.00", matched["after"]["lines"][0]["matchedNetAmount"].AsString); Assert.False(matched["input"]["ocrResultId"].IsBsonNull);
        foreach (var operation in new[] { D.DraftOperation, D.SubmitOperation, D.MatchOperation })
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, operation)));
        var reservation = await data.Rows("evidenceUploadReservations").Find(new BsonDocument("target.kind", "SupplierDocument")).SingleAsync();
        Assert.Equal("SupplierDocumentImage", reservation["purpose"]); Assert.Equal("evidence.supplier-document-image.reserve", reservation["operation"]);
        var leases = await data.Rows("evidenceReadLeases").Find(new BsonDocument("target.kind", "SupplierDocument")).ToListAsync(); Assert.NotEmpty(leases); Assert.All(leases, row => Assert.Equal("released", row["state"]));
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
