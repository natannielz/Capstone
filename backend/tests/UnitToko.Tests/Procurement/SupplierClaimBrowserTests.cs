using System.Diagnostics;
using System.Globalization;
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
using UnitToko.Api.Procurement;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;
using D = UnitToko.Tests.Procurement.SupplierClaimTestData;

namespace UnitToko.Tests.Procurement;

// Genuine inspection/private-video history; synthetic people, policy and protected provider sessions.
[Collection("Mongo")]
public sealed class SupplierClaimBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_claims_actual_damaged_goods_and_independently_reviews_with_original_key_recovery()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This native browser rehearsal requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node));
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await D.CreateAsync(fixture); data.Clock.UseSystemClock = true;
        var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"), protector);
        var issued = new Dictionary<long, SessionIssue>();
        IMongoCollection<BsonDocument> Rows(string name) => data.Database.GetCollection<BsonDocument>(name);
        foreach (var actor in new[] { data.Maker, data.Checker, data.Reader, data.Outsider }) {
            await Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            var now = data.Clock.UtcNow;
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "claim-" + actor.UserId + "@example.invalid", true, "fixture-connection", now, now, "otp", "fixture-policy");
            issued[actor.UserId] = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-claim-provider", ct));
            await Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic supplier claim participant " + actor.UserId }, { "verifiedEmail", "claim-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
        }
        var agedAt = data.Clock.UtcNow.AddMinutes(-6);
        var agedProof = new ProviderIdentityProof(data.Maker.ProviderIssuer, data.Maker.ProviderSubject, "claim-" + data.Maker.UserId + "@example.invalid", true,
            "fixture-connection", agedAt, agedAt, "otp", "fixture-policy");
        var historicalSessions = new MongoIdentitySessionStore(data.Database, data.Unit, new UnitToko.Tests.Authorization.MutableAuthorizationClock(agedAt),
            new("https://approved.example/", "fixture-connection", "fixture-policy"), protector);
        var agedSession = await data.Unit.ExecuteAsync((tx, ct) => historicalSessions.CreateAsync(tx,
            new(data.Maker.UserId, 1, data.Maker.GrantVersion, data.Maker.ProviderIssuer, data.Maker.ProviderSubject), agedProof, "synthetic-claim-aged-factor", ct));
        var readAged = Assert.IsType<IdentitySession>(await sessions.ReadAsync(agedSession.OpaqueToken));
        Assert.Equal(agedAt, readAged.Proof.FactorCompletedAt);
        var inspectionBefore = await data.Inspection.StateAsync(); var custodyBefore = await data.Inspection.Receiving.StateAsync();
        var ordersBefore = await data.Inspection.Receiving.Orders.StateAsync();
        var notice = await data.Inspection.Receiving.Service.ReadAsync(data.Inspection.Receiving.Reader, data.Inspection.NoticeId);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1";
        settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<SupplierClaimService>(); services.AddSingleton(data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko supplier claim rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "supplier-claim-" + Guid.NewGuid().ToString("N") + "-native-child.log");
        var observed = data.Input(); static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        static LongId? Id(long? value) => value is { } id ? new(id) : null;
        var claimInput = new SupplierClaimInputDto(new(new(observed.Location.OrganizationId), new(observed.Location.ShopId), new(observed.Location.WarehouseId), new(observed.Location.ChannelId)),
            new(observed.OrderId), observed.OrderVersion.ToString(CultureInfo.InvariantCulture), new(observed.OrderApprovalId), observed.ClaimReference,
            Day(observed.BusinessDate), Day(observed.ResponseDueOn), observed.DeadlineBasisReference, "replacement", observed.Reason,
            [.. observed.Lines.Select(line => new SupplierClaimLineInputDto("physical_received", new(line.InspectionId), line.InspectionLineNumber, line.Packages, line.Issue,
                Id(line.InvoiceDocumentId), line.InvoiceDocumentLineNumber, Id(line.InvoiceMatchId), line.InvoiceMatchLineNumber))]);
        var input = new { supplierClaim = true, apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = issued[data.Maker.UserId].OpaqueToken, makerToken = issued[data.Maker.UserId].OpaqueToken, checkerToken = issued[data.Checker.UserId].OpaqueToken,
            readerToken = issued[data.Reader.UserId].OpaqueToken, outsiderToken = issued[data.Outsider.UserId].OpaqueToken, agedToken = agedSession.OpaqueToken,
            makerId = data.Maker.UserId.ToString(CultureInfo.InvariantCulture), checkerId = data.Checker.UserId.ToString(CultureInfo.InvariantCulture),
            readerId = data.Reader.UserId.ToString(CultureInfo.InvariantCulture), outsiderId = data.Outsider.UserId.ToString(CultureInfo.InvariantCulture),
            claimInput, inspectionId = data.InspectionId.ToString(CultureInfo.InvariantCulture), packageLabel = notice["notice"]["order"]["lines"][0]["packageLabel"].AsString };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Supplier claim browser child did not start");
        var lines = new StringBuilder();
        async Task ReadAsync(StreamReader stream) {
            while (await stream.ReadLineAsync() is { } line) lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); }
        }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))); process.StandardInput.Close();
            var exit = process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6)); var first = await Task.WhenAny(exit, stdout, stderr); if (first.IsFaulted) await first;
            await exit; await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_SUPPLIER_CLAIM_BROWSER_PASS", lines.ToString());
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            try { await Task.WhenAll(stdout, stderr); } finally { output.WriteLine("Actual browser child log: " + logPath); }
        }
        Assert.Equal(inspectionBefore, await data.Inspection.StateAsync()); Assert.Equal(custodyBefore, await data.Inspection.Receiving.StateAsync());
        Assert.Equal(ordersBefore, await data.Inspection.Receiving.Orders.StateAsync()); await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
        var request = await Rows("supplierClaimRequests").Find(new BsonDocument()).SingleAsync(); var decision = await Rows("supplierClaimDecisions").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Maker.UserId, request["actorId"].AsInt64); Assert.Equal(data.Checker.UserId, decision["actorId"].AsInt64);
        Assert.Equal("approve", decision["decision"]); Assert.Equal(request["claimId"], decision["claimId"]);
        Assert.True(request["lines"][0]["invoice"].IsBsonNull);
        var saved = await data.Service.ReadAsync(data.Reader, request["claimId"].AsInt64);
        Assert.Equal("approved_pending_supplier_action", saved["state"]);
        var capacity = Assert.Single(saved["capacity"].AsBsonArray, row => row["basis"] == "physical_received");
        Assert.Equal(1L, capacity["occupiedPackages"].AsInt64); Assert.Equal(2L, capacity["remainingPackages"].AsInt64);
        foreach (var operation in new[] { "procurement.supplierclaim.request", "procurement.supplierclaim.approve" })
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                Assert.Equal(1, await Rows(table).CountDocumentsAsync(new BsonDocument(field, operation)));
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
