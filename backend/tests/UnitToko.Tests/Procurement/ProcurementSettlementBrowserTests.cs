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
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;
using D = UnitToko.Tests.Procurement.ProcurementSettlementTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementSettlementBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_independently_verifies_actual_debit_and_recovers_one_claim_and_journal()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native private evidence requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await D.CreateAsync(fixture); var source = data.Disbursement;
        var recorder = source.Actor; var importer = source.Bank.Statements.Maker;
        string[] actionGrants = [Permissions.ProcurementSettlementRead, Permissions.DisbursementVerify, Permissions.ProcurementDisbursementRead,
            Permissions.ProcurementDisbursementEvidenceRead, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead,
            Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead];
        await source.Base.AddGrantAsync(recorder, 889971, CatalogAuthoringTestData.Shop, actionGrants);
        await source.Base.AddGrantAsync(importer, 889972, CatalogAuthoringTestData.Shop, actionGrants);
        data.Clock.UseSystemClock = true;
        var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"), protector);
        var issued = new Dictionary<long, SessionIssue>();
        foreach (var actor in new[] { data.Verifier, data.Reader, data.Outsider, recorder, importer }) {
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            var now = data.Clock.UtcNow; var email = "settlement-" + actor.UserId + "@example.invalid";
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, email, true, "fixture-connection", now, now, "otp", "fixture-policy");
            issued[actor.UserId] = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-settlement-provider", ct));
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic settlement participant " + actor.UserId }, { "verifiedEmail", email }, { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
        }
        var agedAt = data.Clock.UtcNow.AddMinutes(-6); var verifier = data.Verifier;
        var historicalSessions = new MongoIdentitySessionStore(data.Database, data.Unit, new UnitToko.Tests.Authorization.MutableAuthorizationClock(agedAt),
            new("https://approved.example/", "fixture-connection", "fixture-policy"), protector);
        var agedProof = new ProviderIdentityProof(verifier.ProviderIssuer, verifier.ProviderSubject, "settlement-" + verifier.UserId + "@example.invalid", true,
            "fixture-connection", agedAt, agedAt, "otp", "fixture-policy");
        var aged = await data.Unit.ExecuteAsync((tx, ct) => historicalSessions.CreateAsync(tx,
            new(verifier.UserId, 1, verifier.GrantVersion, verifier.ProviderIssuer, verifier.ProviderSubject), agedProof, "synthetic-settlement-aged-factor", ct));
        Assert.Equal(agedAt, Assert.IsType<IdentitySession>(await sessions.ReadAsync(aged.OpaqueToken)).Proof.FactorCompletedAt);
        var original = await source.PreservedAsync(); var originalRecord = await source.StateAsync();
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<ProcurementDisbursementService>(); services.AddSingleton(source.Service);
            services.RemoveAll<ProcurementDisbursementEvidenceService>(); services.AddSingleton(source.Evidence);
            services.RemoveAll<ProcurementSettlementService>(); services.AddSingleton(data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko procurement settlement rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "procurement-settlement-" + Guid.NewGuid().ToString("N") + "-native-child.log");
        var selected = source.Input(); var policy = data.Policy(); var observed = data.Input();
        var input = new { procurementSettlement = true, apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = issued[verifier.UserId].OpaqueToken, verifierToken = issued[verifier.UserId].OpaqueToken, verifierId = verifier.UserId.ToString(),
            recorderToken = issued[recorder.UserId].OpaqueToken, recorderId = recorder.UserId.ToString(), importerToken = issued[importer.UserId].OpaqueToken, importerId = importer.UserId.ToString(),
            readerToken = issued[data.Reader.UserId].OpaqueToken, readerId = data.Reader.UserId.ToString(), outsiderToken = issued[data.Outsider.UserId].OpaqueToken,
            outsiderId = data.Outsider.UserId.ToString(), agedToken = aged.OpaqueToken,
            organizationId = "100", shopId = "101", warehouseId = "102", channelId = "103", organizationName = "Synthetic cooperative",
            shopName = "Synthetic fulfillment shop", warehouseName = "Synthetic dispatch warehouse", channelName = "Synthetic retail channel",
            disbursementId = data.DisbursementId.ToString(), supplierName = SupplierTestData.Details.DisplayName, supplierSubject = SupplierTestData.Details.SubjectReference,
            amount = selected.Amount.ToString(), transferDate = selected.TransferDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), transferReference = selected.TransferReference,
            policyId = D.PolicyId.ToString(), policyRevision = "1", policyApprovalReference = policy["approvalReference"].AsString, trustedBankChannelReference = policy["trustedBankChannelReference"].AsString,
            importId = data.BankImportId.ToString(), importFilename = "synthetic-advance-debit.csv", importProfileName = "Synthetic bank export profile", rowNumber = 2,
            transactionId = "ADV-DEBIT-0001", bookedDate = selected.TransferDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            observedBankCode = observed.ObservedBeneficiary.BankCode, observedAccountNumber = observed.ObservedBeneficiary.AccountNumber, observedHolderName = observed.ObservedBeneficiary.HolderName,
            beneficiaryVerificationReference = observed.BeneficiaryVerificationReference, transferVerificationReference = observed.TransferVerificationReference,
            originalSha256 = source.Reference.Sha256, originalWidth = 13, originalHeight = 7, supplierAdvanceAccountCode = "A1201", cashAccountCode = "A1101" };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Procurement settlement browser did not start"); var lines = new StringBuilder();
        async Task ReadAsync(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); } }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            var exit = process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4)); var first = await Task.WhenAny(exit, stdout, stderr); if (first.IsFaulted) await first;
            await exit; await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_PROCUREMENT_SETTLEMENT_BROWSER_PASS", lines.ToString());
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            try { await Task.WhenAll(stdout, stderr); } finally { output.WriteLine("Actual browser child log: " + logPath); }
        }
        var settled = await data.Rows(D.Events).Find(new BsonDocument()).SingleAsync(); Assert.Equal(verifier.UserId, settled["actorId"].AsInt64);
        Assert.Equal(data.DisbursementId, settled["disbursementId"].AsInt64); Assert.Equal("6000.00", settled["amount"].AsString);
        var claim = await data.Rows("bankTransactionClaims").Find(new BsonDocument("ownerKind", "procurementDisbursementSettlement")).SingleAsync();
        Assert.Equal(settled["_id"], claim["ownerId"]); Assert.Equal(settled["snapshotHash"], claim["ownerHash"]); Assert.Equal(2, claim["schemaVersion"].AsInt32);
        Assert.Equal(original["journalEntries"].AsBsonArray.Count + 1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("_id", settled["journalId"])).SingleAsync();
        var advance = journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "supplier_advance"); var cash = journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "bank_cash");
        Assert.Equal(6000m, advance["debit"].AsDecimal); Assert.Equal(0m, advance["credit"].AsDecimal); Assert.Equal(0m, cash["debit"].AsDecimal); Assert.Equal(6000m, cash["credit"].AsDecimal);
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, D.Operation)));
        Assert.Equal(originalRecord, await source.StateAsync()); var after = await source.PreservedAsync();
        foreach (var name in new[] { "bankTransactionClaims", "journalEntries" }) { original.Remove(name); after.Remove(name); }
        Assert.Equal(original, after);
        Assert.False(await data.Rows("evidenceReadLeases").Find(new BsonDocument("state", new BsonDocument("$ne", "released"))).AnyAsync());
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Use the authoritative checkout");
    }
}
