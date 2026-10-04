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
using D = UnitToko.Tests.Procurement.SupplierBankTestData;


namespace UnitToko.Tests.Procurement;

// Genuine producer history with explicitly synthetic people, verification policy, sessions and commercial document.
[Collection("Mongo")]
public sealed class SupplierBankBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_proposes_bank_details_independently_observes_and_approves_with_original_key_recovery_and_private_reads()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This native browser rehearsal requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await D.CreateAsync(fixture); data.Clock.UseSystemClock = true;
        var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"), protector);
        var issued = new Dictionary<long, SessionIssue>();
        foreach (var actor in new[] { data.Maker, data.Checker, data.Reader, data.DetailsReader, data.Outsider }) {
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L)); var now = data.Clock.UtcNow;
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "po-" + actor.UserId + "@example.invalid", true, "fixture-connection", now, now, "otp", "fixture-policy");
            issued[actor.UserId] = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-po-provider", ct));
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", "Synthetic supplier-bank participant " + actor.UserId },
                { "verifiedEmail", "po-" + actor.UserId + "@example.invalid" }, { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
        }
        var agedAt = data.Clock.UtcNow.AddMinutes(-6);
        var agedProof = new ProviderIdentityProof(data.Maker.ProviderIssuer, data.Maker.ProviderSubject, "po-" + data.Maker.UserId + "@example.invalid", true,
            "fixture-connection", agedAt, agedAt, "otp", "fixture-policy");
        // Create valid evidence at its real fixture time, then resolve it through the advancing browser clock without renewing factor assurance.
        var historicalSessions = new MongoIdentitySessionStore(data.Database, data.Unit, new UnitToko.Tests.Authorization.MutableAuthorizationClock(agedAt),
            new("https://approved.example/", "fixture-connection", "fixture-policy"), protector);
        var agedSession = await data.Unit.ExecuteAsync((tx, ct) => historicalSessions.CreateAsync(tx,
            new(data.Maker.UserId, 1, data.Maker.GrantVersion, data.Maker.ProviderIssuer, data.Maker.ProviderSubject), agedProof, "synthetic-bank-aged-factor", ct));
        Assert.Equal(agedAt, agedSession.Session.CreatedAt);
        var currentAgedSession = Assert.IsType<IdentitySession>(await sessions.ReadAsync(agedSession.OpaqueToken));
        Assert.Equal(agedAt, currentAgedSession.Proof.FactorCompletedAt);
        var original = new BsonDocument();
        foreach (var table in new[] { "organizationProfiles", "organizationProposals", "authorizationHistory", "authorizationScopes", "catalogCategories", "productFamilies", "skus", "variantDefinitions", ProcurementRequestTestData.Requests,
            ProcurementRequestTestData.Decisions, SupplierTestData.Policies, SupplierTestData.Proposals, SupplierTestData.Observations, SupplierTestData.Decisions, SupplierTestData.Versions, D.Policies }) {
            var rows = await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); original[table] = new BsonArray(rows);
        }
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<SupplierBankService>(); services.AddSingleton(data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses); const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko supplier-bank rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { supplierBanks = true, apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = issued[data.Maker.UserId].OpaqueToken, makerToken = issued[data.Maker.UserId].OpaqueToken, checkerToken = issued[data.Checker.UserId].OpaqueToken,
            readerToken = issued[data.Reader.UserId].OpaqueToken, detailsReaderToken = issued[data.DetailsReader.UserId].OpaqueToken, outsiderToken = issued[data.Outsider.UserId].OpaqueToken, agedToken = agedSession.OpaqueToken,
            makerId = data.Maker.UserId.ToString(), checkerId = data.Checker.UserId.ToString(), readerId = data.Reader.UserId.ToString(), detailsReaderId = data.DetailsReader.UserId.ToString(), outsiderId = data.Outsider.UserId.ToString(),
            organizationId = "100", shopId = "101", channelId = "103", supplierId = data.Supplier["supplierId"].AsInt64.ToString(),
            supplierVersionId = data.Supplier["versionId"].AsInt64.ToString(), supplierVersion = data.Supplier["version"].AsInt64.ToString(),
            policyId = D.PolicyId.ToString(), policyRevision = "1", supplierName = SupplierTestData.Details.DisplayName, supplierSubject = SupplierTestData.Details.SubjectReference,
            trustedChannelReference = D.TrustedChannel, sourceReference = D.SourceReference,
            details = new { bankCode = D.Details.BankCode, accountNumber = D.Details.AccountNumber, holderName = D.Details.HolderName, currency = D.Details.Currency },
            organizationName = "Synthetic cooperative", shopName = "Synthetic fulfillment shop", channelName = "Synthetic retail channel" };
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "supplier-bank-" + Guid.NewGuid().ToString("N") + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Procurement-funding browser child did not start");
        async Task ReadAsync(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) { lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); } } }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try { await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString()); Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_SUPPLIER_BANK_BROWSER_PASS", lines.ToString());
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser child log: " + logPath); }
        var proposal = await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync(); var observation = await data.Rows(D.Observations).Find(new BsonDocument()).SingleAsync();
        var decision = await data.Rows(D.Decisions).Find(new BsonDocument()).SingleAsync(); var version = await data.Rows(D.Versions).Find(new BsonDocument()).SingleAsync();
        var head = await data.Rows(D.Heads).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Maker.UserId, proposal["actorId"].AsInt64); Assert.Equal(data.Checker.UserId, observation["actorId"].AsInt64); Assert.Equal(data.Checker.UserId, decision["actorId"].AsInt64);
        Assert.Equal("approve", decision["decision"]); Assert.Equal("000012345678", proposal["input"]["details"]["accountNumber"]);
        Assert.Equal(proposal["input"]["details"], observation["observed"]); Assert.Equal(data.Supplier["supplierId"], proposal["input"]["supplierId"]);
        Assert.Equal(version["_id"], head["versionId"]); Assert.Equal(1L, head["version"].AsInt64);
        Assert.Equal("approved", (await data.Service.ReadAsync(data.DetailsReader, proposal["_id"].AsInt64))["state"]);
        var current = await data.Service.CurrentAsync(data.Reader, data.Input.SupplierId); Assert.True(current["eligible"].AsBoolean);
        Assert.Equal("********5678", current["details"]["maskedAccount"]); Assert.DoesNotContain(D.Details.AccountNumber, current.ToJson(), StringComparison.Ordinal);
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            foreach (var operation in new[] { "propose", "observe", "approve" }) Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, D.Prefix + operation)));
        foreach (var element in original) { var rows = await data.Rows(element.Name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); Assert.Equal(element.Value, new BsonArray(rows)); }
        await data.NoFinancialEffectsAsync(); await data.Suppliers.NoBusinessEffectsAsync();
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
