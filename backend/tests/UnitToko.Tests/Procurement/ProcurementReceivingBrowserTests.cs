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
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;
using D = UnitToko.Tests.Procurement.ProcurementReceivingTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementReceivingBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_records_actual_supplier_notice_and_partial_container_custody_with_separate_key_recovery()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This native browser rehearsal requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await D.CreateAsync(fixture); var originalOrders = await data.Orders.StateAsync(); data.Clock.UseSystemClock = true;
        var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock,
            new("https://approved.example/", "fixture-connection", "fixture-policy"), protector);
        var issued = new Dictionary<long, SessionIssue>();
        async Task<SessionIssue> IssueAsync(ActorContext actor, bool aged = false) {
            var now = aged ? data.Clock.UtcNow.AddMinutes(-6) : data.Clock.UtcNow;
            var issuingStore = aged ? new MongoIdentitySessionStore(data.Database, data.Unit,
                new UnitToko.Tests.Authorization.MutableAuthorizationClock(now),
                new("https://approved.example/", "fixture-connection", "fixture-policy"), protector) : sessions;
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "receiving-" + actor.UserId + "@example.invalid", true,
                "fixture-connection", now, now, "otp", "fixture-policy");
            var issue = await data.Unit.ExecuteAsync((tx, ct) => issuingStore.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-receiving-provider", ct));
            Assert.Equal(now, Assert.IsType<IdentitySession>(await sessions.ReadAsync(issue.OpaqueToken)).Proof.FactorCompletedAt);
            return issue;
        }
        foreach (var actor in new[] { data.Maker, data.Receiver, data.Reader, data.Outsider }) {
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            issued[actor.UserId] = await IssueAsync(actor); var now = data.Clock.UtcNow;
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic receiving participant " + actor.UserId }, { "verifiedEmail", "receiving-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
        }
        var agedMaker = await IssueAsync(data.Maker, true); var agedReceiver = await IssueAsync(data.Receiver, true);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<ProcurementReceivingService>(); services.AddSingleton(data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses); const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko procurement container rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var notice = data.NoticeInput(); var intake = data.IntakeInput();
        var config = new {
            procurementReceiving = true, apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = issued[data.Maker.UserId].OpaqueToken, makerToken = issued[data.Maker.UserId].OpaqueToken, makerId = data.Maker.UserId.ToString(CultureInfo.InvariantCulture),
            receiverToken = issued[data.Receiver.UserId].OpaqueToken, receiverId = data.Receiver.UserId.ToString(CultureInfo.InvariantCulture),
            readerToken = issued[data.Reader.UserId].OpaqueToken, readerId = data.Reader.UserId.ToString(CultureInfo.InvariantCulture),
            outsiderToken = issued[data.Outsider.UserId].OpaqueToken, outsiderId = data.Outsider.UserId.ToString(CultureInfo.InvariantCulture), agedMakerToken = agedMaker.OpaqueToken, agedReceiverToken = agedReceiver.OpaqueToken,
            organizationId = "100", shopId = "101", warehouseId = "102", channelId = "103", organizationName = "Synthetic cooperative", shopName = "Synthetic fulfillment shop",
            warehouseName = "Synthetic dispatch warehouse", channelName = "Synthetic retail channel", orderId = data.OrderId.ToString(CultureInfo.InvariantCulture), orderVersion = "1", orderApprovalId = data.ApprovalId.ToString(CultureInfo.InvariantCulture),
            supplierName = SupplierTestData.Details.DisplayName, supplierSubject = SupplierTestData.Details.SubjectReference, route = "ddo", commercialReference = "SYNTHETIC-QUOTE-0001",
            noticeDate = notice.NoticeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), supplierDocumentReference = notice.SupplierDocumentReference,
            ddoRequestReference = notice.DdoRequestReference, ddoTelephoneReference = notice.DdoTelephoneReference, declaredContainers = notice.DeclaredContainers, note = notice.Note,
            receivedDate = intake.ReceivedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), intakeReference = intake.IntakeReference, matchedContainers = intake.MatchedContainers,
            excessContainers = intake.ExcessContainers, heldContainers = intake.HeldContainers, condition = intake.Condition, discrepancy = intake.Discrepancy
        };
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "procurement-receiving-" + Guid.NewGuid().ToString("N") + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Procurement receiving browser did not start");
        async Task ReadAsync(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) { lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); } } }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config)); process.StandardInput.Close();
            var exit = process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4)); var first = await Task.WhenAny(exit, stdout, stderr); if (first.IsFaulted) await first;
            await exit; await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_PROCUREMENT_RECEIVING_BROWSER_PASS", lines.ToString());
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser child log: " + logPath); }
        var savedNotice = await data.Rows(D.Notices).Find(new BsonDocument()).SingleAsync(); var savedIntake = await data.Rows(D.Intakes).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Maker.UserId, savedNotice["actorId"].AsInt64); Assert.Equal(data.Receiver.UserId, savedIntake["actorId"].AsInt64);
        Assert.Equal(savedNotice["_id"], savedIntake["noticeId"]); Assert.Equal(data.OrderId, savedNotice["input"]["orderId"].AsInt64);
        var view = await data.Service.ReadAsync(data.Reader, savedNotice["_id"].AsInt64); var capacity = view["capacity"];
        Assert.Equal(5, capacity["declaredContainers"].AsInt32); Assert.Equal(3, capacity["matchedContainers"].AsInt32); Assert.Equal(2, capacity["remainingContainers"].AsInt32);
        Assert.Equal(1L, capacity["excessContainers"].AsInt64); Assert.Equal(1L, capacity["heldContainers"].AsInt64); Assert.Equal(4L, capacity["receivedContainers"].AsInt64);
        Assert.Equal("pending_inspection", Assert.Single(view["receipts"].AsBsonArray)["state"].AsString);
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            foreach (var operation in new[] { D.NoticeOperation, D.IntakeOperation }) Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, operation)));
        Assert.Equal(originalOrders, await data.Orders.StateAsync()); await data.Orders.NoFinancialOrReceiptEffectsAsync();
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
