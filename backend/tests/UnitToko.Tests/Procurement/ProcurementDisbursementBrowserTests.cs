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
using D = UnitToko.Tests.Procurement.ProcurementDisbursementTestData;


namespace UnitToko.Tests.Procurement;

// Genuine producer history with explicitly synthetic people, verification policy, sessions and commercial document.
[Collection("Mongo")]
public sealed class ProcurementDisbursementBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_records_real_private_original_with_exact_key_recovery_and_no_financial_effects()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This native browser rehearsal requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await D.CreateAsync(fixture, createDraft: false); data.Clock.UseSystemClock = true;
        var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"), protector);
        var issued = new Dictionary<long, SessionIssue>();
        foreach (var actor in new[] { data.Actor, data.Reader, data.EvidenceReader, data.Outsider }) {
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L)); var now = data.Clock.UtcNow;
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "po-" + actor.UserId + "@example.invalid", true, "fixture-connection", now, now, "otp", "fixture-policy");
            issued[actor.UserId] = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-po-provider", ct));
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", "Synthetic supplier-bank participant " + actor.UserId },
                { "verifiedEmail", "po-" + actor.UserId + "@example.invalid" }, { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
        }
        var agedAt = data.Clock.UtcNow.AddMinutes(-6);
        var agedProof = new ProviderIdentityProof(data.Actor.ProviderIssuer, data.Actor.ProviderSubject, "po-" + data.Actor.UserId + "@example.invalid", true,
            "fixture-connection", agedAt, agedAt, "otp", "fixture-policy");
        // Create valid evidence at its real fixture time, then resolve it through the advancing browser clock without renewing factor assurance.
        var historicalSessions = new MongoIdentitySessionStore(data.Database, data.Unit, new UnitToko.Tests.Authorization.MutableAuthorizationClock(agedAt),
            new("https://approved.example/", "fixture-connection", "fixture-policy"), protector);
        var agedSession = await data.Unit.ExecuteAsync((tx, ct) => historicalSessions.CreateAsync(tx,
            new(data.Actor.UserId, 1, data.Actor.GrantVersion, data.Actor.ProviderIssuer, data.Actor.ProviderSubject), agedProof, "synthetic-bank-aged-factor", ct));
        Assert.Equal(agedAt, agedSession.Session.CreatedAt);
        var currentAgedSession = Assert.IsType<IdentitySession>(await sessions.ReadAsync(agedSession.OpaqueToken));
        Assert.Equal(agedAt, currentAgedSession.Proof.FactorCompletedAt);
        var original = await data.PreservedAsync();
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<ProcurementDisbursementService>(); services.AddSingleton(data.Service);
            services.RemoveAll<ProcurementDisbursementEvidenceService>(); services.AddSingleton(data.Evidence);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses); const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko procurement disbursement rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var run = "procurement-disbursement-" + Guid.NewGuid().ToString("N"); var logPath = Path.Combine(directory, run + "-native-child.log");
        var imagePath = Path.Combine(directory, run + ".png"); var validationReadyPath = Path.Combine(directory, run + "-validated.txt");
        await File.WriteAllBytesAsync(imagePath, data.Bytes); var selected = data.Input();
        var input = new { procurementDisbursement = true, apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = issued[data.Actor.UserId].OpaqueToken, makerToken = issued[data.Actor.UserId].OpaqueToken,
            readerToken = issued[data.Reader.UserId].OpaqueToken, evidenceReaderToken = issued[data.EvidenceReader.UserId].OpaqueToken,
            outsiderToken = issued[data.Outsider.UserId].OpaqueToken, agedToken = agedSession.OpaqueToken,
            makerId = data.Actor.UserId.ToString(), readerId = data.Reader.UserId.ToString(), evidenceReaderId = data.EvidenceReader.UserId.ToString(), outsiderId = data.Outsider.UserId.ToString(),
            organizationId = "100", shopId = "101", warehouseId = "102", channelId = "103",
            fundingRequestId = selected.FundingRequestId.ToString(), fundingDecisionId = selected.FundingDecisionId.ToString(), authorizationId = selected.AuthorizationId.ToString(),
            supplierId = selected.SupplierId.ToString(), beneficiaryVersionId = selected.BeneficiaryVersionId.ToString(), beneficiaryVersion = selected.BeneficiaryVersion.ToString(),
            payingAccountId = selected.PayingAccountId.ToString(), payingAccountVersionId = selected.PayingAccountVersionId.ToString(),
            policyId = D.PolicyId.ToString(), policyRevision = "1", supplierName = SupplierTestData.Details.DisplayName,
            amount = selected.Amount.ToString(), transferDate = selected.TransferDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), transferReference = selected.TransferReference,
            supplierAccount = SupplierBankTestData.Details.AccountNumber, payingAccount = UnitToko.Tests.Ordering.ReceivingAccountTestData.Details.AccountNumber,
            imagePath, validationReadyPath, originalSha256 = Convert.ToHexString(SHA256.HashData(data.Bytes)).ToLowerInvariant(),
            organizationName = "Synthetic cooperative", shopName = "Synthetic fulfillment shop", warehouseName = "Synthetic dispatch warehouse", channelName = "Synthetic retail channel" };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Procurement disbursement browser child did not start");
        var lines = new StringBuilder(); var validationStarted = false;
        async Task ReadAsync(StreamReader stream) {
            while (await stream.ReadLineAsync() is { } line) {
                lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); }
                if (line == "VALIDATE_PROCUREMENT_DISBURSEMENT_EVIDENCE_READY") {
                    Assert.False(validationStarted); validationStarted = true;
                    await data.ValidateAsync(); await File.WriteAllTextAsync(validationReadyPath, "Actual native scanner and decoder validation completed");
                }
            }
        }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            var exit = process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4));
            var first = await Task.WhenAny(exit, stdout, stderr); if (first.IsFaulted) await first;
            await exit; await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_PROCUREMENT_DISBURSEMENT_BROWSER_PASS", lines.ToString()); Assert.True(validationStarted);
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            try { await Task.WhenAll(stdout, stderr); } finally { output.WriteLine("Actual browser child log: " + logPath); }
        }
        Assert.Equal(original, await data.PreservedAsync());
        var draft = await data.Rows(D.Drafts).Find(new BsonDocument()).SingleAsync(); var record = await data.Rows(D.Records).Find(new BsonDocument()).SingleAsync();
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "ProcurementDisbursement")).SingleAsync();
        Assert.Equal(data.Actor.UserId, record["actorId"].AsInt64); Assert.Equal("recorded_unverified", record["state"]);
        Assert.Equal("6000.00", record["input"]["amount"]); Assert.Equal(record["_id"], draft["recordedDisbursementId"]);
        Assert.Equal(record["pinId"], pin["_id"]); Assert.Equal(record["_id"], pin["associationId"]); Assert.Equal(draft["_id"], pin["procurementDisbursementDraftId"]);
        Assert.Equal(record["snapshotHash"], pin["associationHash"]); Assert.Equal(record["evidence"], pin["reference"]);
        Assert.Equal(1, await data.Rows(D.Allocations).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows(D.Heads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("evidencePinAudits").CountDocumentsAsync(new BsonDocument("pinId", pin["_id"])));
        foreach (var operation in new[] { D.Prefix + "draft", D.Prefix + "record" })
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, operation)));
        var evidence = await data.Rows("evidenceUploadReservations").Find(new BsonDocument("target.kind", "ProcurementDisbursement")).SingleAsync();
        Assert.Equal("ProcurementDisbursementImage", evidence["purpose"]); Assert.Equal("evidence.procurement-disbursement-image.reserve", evidence["operation"]);
        Assert.Equal(data.Actor.UserId, evidence["uploader"]["userId"].AsInt64); Assert.Equal(draft["_id"], evidence["target"]["id"]);
        var leases = await data.Rows("evidenceReadLeases").Find(new BsonDocument("target.kind", "ProcurementDisbursement")).ToListAsync();
        Assert.NotEmpty(leases); Assert.All(leases, row => Assert.Equal("released", row["state"]));
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}