using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;
using D = UnitToko.Tests.Procurement.ProcurementSupplierPayableTestData;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementSupplierPayableBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_recognizes_genuine_supplier_payable_and_applies_verified_advance_with_original_key_recovery()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This native browser rehearsal requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await D.CreateAsync(fixture, diagnostics: output.WriteLine); data.Clock.UseSystemClock = true;
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        // The synthetic browser accounts use the same non-placeholder issuer as the existing OIDC rehearsal.
        data.Maker = data.Maker with { ProviderIssuer = configured.Issuer }; data.Checker = data.Checker with { ProviderIssuer = configured.Issuer };
        data.Reader = data.Reader with { ProviderIssuer = configured.Issuer }; data.Outsider = data.Outsider with { ProviderIssuer = configured.Issuer };
        var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new(configured.Issuer, configured.ConnectionId, configured.AssurancePolicyVersion), protector);
        var issued = new Dictionary<long, SessionIssue>();
        foreach (var actor in new[] { data.Maker, data.Checker, data.Reader, data.Outsider }) {
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L).Set("issuer", configured.Issuer));
            var now = data.Clock.UtcNow;
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "payable-" + actor.UserId + "@example.invalid", true,
                configured.ConnectionId, now, now, "otp", configured.AssurancePolicyVersion);
            issued[actor.UserId] = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-payable-provider", ct));
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic payable participant " + actor.UserId }, { "verifiedEmail", "payable-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
        }
        var agedAt = data.Clock.UtcNow.AddMinutes(-6);
        var historicalSessions = new MongoIdentitySessionStore(data.Database, data.Unit, new UnitToko.Tests.Authorization.MutableAuthorizationClock(agedAt),
            new(configured.Issuer, configured.ConnectionId, configured.AssurancePolicyVersion), protector);
        var agedProof = new ProviderIdentityProof(data.Maker.ProviderIssuer, data.Maker.ProviderSubject, "payable-" + data.Maker.UserId + "@example.invalid", true,
            configured.ConnectionId, agedAt, agedAt, "otp", configured.AssurancePolicyVersion);
        var agedSession = await data.Unit.ExecuteAsync((tx, ct) => historicalSessions.CreateAsync(tx,
            new(data.Maker.UserId, 1, data.Maker.GrantVersion, data.Maker.ProviderIssuer, data.Maker.ProviderSubject), agedProof, "synthetic-payable-aged-factor", ct));
        Assert.Equal(agedAt, Assert.IsType<IdentitySession>(await sessions.ReadAsync(agedSession.OpaqueToken)).Proof.FactorCompletedAt);
        var preserved = await data.ProtectedAsync();
        using var provider = new ProviderHandlerTests.FakeProvider(configured, data.Clock);
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.Configure<OpenIdConnectOptions>(Auth0Options.Scheme, options => options.Backchannel = new HttpClient(provider, disposeHandler: false));
            services.AddSingleton<IStartupFilter>(new ObserveProviderChallenge(provider, configured.Issuer));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<ProcurementSupplierPayableService>(); services.AddSingleton(data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko supplier payable rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); names.AddDnsName(new Uri(configured.Issuer).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "procurement-supplier-payable-" + Guid.NewGuid().ToString("N") + "-native-child.log");
        var observed = data.Recognize(); static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
        var recognitionInput = new { kind = "recognize", location = new { organizationId = Id(observed.Location.OrganizationId), shopId = Id(observed.Location.ShopId),
            warehouseId = Id(observed.Location.WarehouseId), channelId = Id(observed.Location.ChannelId) }, valuationAllocationId = Id(data.AllocationId),
            payableId = (string?)null, settlementId = (string?)null, amount = (string?)null,
            policyId = Id(observed.PolicyId), policyRevision = Id(observed.PolicyRevision), postingDate = observed.PostingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), reason = observed.Reason };
        var input = new { procurementSupplierPayable = true, apiAddress, origin, providerIssuer = configured.Issuer, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = issued[data.Maker.UserId].OpaqueToken, makerToken = issued[data.Maker.UserId].OpaqueToken, checkerToken = issued[data.Checker.UserId].OpaqueToken,
            readerToken = issued[data.Reader.UserId].OpaqueToken, outsiderToken = issued[data.Outsider.UserId].OpaqueToken, agedToken = agedSession.OpaqueToken,
            makerId = Id(data.Maker.UserId), checkerId = Id(data.Checker.UserId), readerId = Id(data.Reader.UserId), outsiderId = Id(data.Outsider.UserId),
            recognitionInput, settlementId = Id(data.SettlementId), applicationAmount = "4000.00", decisionReason = D.Reason };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Supplier payable browser child did not start");
        var elapsed = Stopwatch.StartNew();
        // The adjacent four-source financial journey uses this same overall bound; each browser operation retains its own strict deadline.
        var journeyBudget = TimeSpan.FromMinutes(8);
        var lines = new StringBuilder();
        async Task ReadAsync(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); } }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))); process.StandardInput.Close();
            var exit = process.WaitForExitAsync().WaitAsync(journeyBudget); var first = await Task.WhenAny(exit, stdout, stderr); if (first.IsFaulted) await first;
            await exit; await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_PROCUREMENT_SUPPLIER_PAYABLE_BROWSER_PASS", lines.ToString());
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            try { await Task.WhenAll(stdout, stderr); } finally {
                output.WriteLine($"Actual browser journey: {elapsed.Elapsed.TotalSeconds:F3}s; overall budget: {journeyBudget.TotalSeconds:F0}s");
                output.WriteLine("Actual browser child log: " + logPath);
            }
        }
        Assert.True(provider.SawValidPkce); Assert.Equal(2, provider.TokenRequests);
        var transactions = await data.Rows("oidcTransactions").Find(new BsonDocument()).ToListAsync();
        Assert.Equal(2, transactions.Count);
        foreach (var (old, operation, returnPath) in new[] {
            (agedSession, "procurement.payable.propose", "/procurement/supplier-payables"),
            (issued[data.Checker.UserId], "procurement.payable.decide", "/identity/account") }) {
            var transaction = Assert.Single(transactions, row => row["expectedSessionHash"] == old.Session.Reference.Id);
            Assert.Equal("stepUp", transaction["purpose"]); Assert.Equal("consumed", transaction["status"]);
            Assert.Equal(operation, transaction["operation"]); Assert.Equal(returnPath, transaction["returnPath"]);
            Assert.Equal(old.Session.UserId, transaction["expectedUserId"].AsInt64);
            Assert.Null(await sessions.ReadAsync(old.OpaqueToken));
            var rotated = await data.Rows("identitySessions").Find(new BsonDocument("_id", old.Session.Reference.Id)).SingleAsync();
            Assert.Equal("rotated", rotated["status"]); Assert.Equal("step_up_rotation", rotated["revocationReason"]);
            var replacement = await data.Rows("identitySessions").Find(new BsonDocument { { "familyId", rotated["familyId"] }, { "status", "active" } }).SingleAsync();
            Assert.Equal(rotated["absoluteExpiresAt"], replacement["absoluteExpiresAt"]);
            Assert.Equal(rotated["userId"], replacement["userId"]);
            Assert.True(replacement["factorCompletedAt"].ToUniversalTime() > rotated["factorCompletedAt"].ToUniversalTime());
        }
        Assert.Equal(preserved, await data.ProtectedAsync());
        var proposals = await data.Rows(B.Proposals).Find(new BsonDocument()).ToListAsync();
        var decisions = await data.Rows(B.Decisions).Find(new BsonDocument()).ToListAsync();
        Assert.Equal(2, proposals.Count); Assert.Equal(2, decisions.Count);
        Assert.All(proposals, row => Assert.Equal(data.Maker.UserId, row["actorId"].AsInt64));
        Assert.All(decisions, row => { Assert.Equal(data.Checker.UserId, row["actorId"].AsInt64); Assert.Equal("approve", row["decision"]); });
        var recognition = Assert.Single(decisions, row => row["input"]["kind"] == "recognize");
        var application = Assert.Single(decisions, row => row["input"]["kind"] == "apply_advance");
        Assert.Equal(recognition["payableId"], application["payableId"]);
        var applicationRow = await data.Rows(B.Applications).Find(new BsonDocument("_id", application["applicationId"])).SingleAsync();
        Assert.Equal(application["payableId"], applicationRow["payableId"]);
        Assert.Equal(application["_id"], applicationRow["decisionId"]); Assert.Equal(application["journalId"], applicationRow["journalId"]);
        await ProcurementSupplierPayableTests.JournalAsync(data, recognition["journalId"].AsInt64, 11250m, "receipt_accrual", "supplier_payable");
        await ProcurementSupplierPayableTests.JournalAsync(data, application["journalId"].AsInt64, 4000m, "supplier_payable", "supplier_advance");
        Assert.Equal(1L, await data.Rows(B.Payables).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(B.Applications).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(5L, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        var payable = await data.Rows(B.PayableHeads).Find(new BsonDocument("_id", recognition["payableId"])).SingleAsync();
        var advance = await data.Rows(B.AdvanceHeads).Find(new BsonDocument("_id", data.SettlementId)).SingleAsync();
        Assert.Equal("11250.00", payable["totalAmount"]); Assert.Equal("4000.00", payable["appliedAmount"]); Assert.Equal("7250.00", payable["remainingAmount"]);
        Assert.Equal("6000.00", advance["totalAmount"]); Assert.Equal("4000.00", advance["appliedAmount"]); Assert.Equal("2000.00", advance["remainingAmount"]);
        foreach (var operation in new[] { B.ProposeOperation, B.DecideOperation })
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                Assert.Equal(2L, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, operation)));
    }

    private sealed class ObserveProviderChallenge(ProviderHandlerTests.FakeProvider provider, string issuer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use((context, nextRequest) => {
                context.Response.OnStarting(() => {
                    var location = context.Response.Headers.Location.ToString();
                    if (location.StartsWith(issuer + "authorize?", StringComparison.Ordinal)) {
                        // Only the synthetic provider is replaced; current-session admission, state,
                        // PKCE, nonce, signature, assurance binding and rotation use the actual host.
                        Assert.Equal("/auth/step-up", context.Request.Path.Value);
                        var session = Assert.IsType<VerifiedSessionFeature>(context.Features.Get<VerifiedSessionFeature>()).Session;
                        provider.Challenge = QueryHelpers.ParseQuery(new Uri(location).Query).ToDictionary(item => item.Key, item => item.Value.ToString());
                        provider.Subject = session.Proof.Subject; provider.Email = session.Proof.Email;
                    }
                    return Task.CompletedTask;
                }); return nextRequest();
            }); next(app);
        };
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
