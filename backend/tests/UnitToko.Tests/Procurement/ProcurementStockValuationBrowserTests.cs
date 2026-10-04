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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Api.Procurement;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;
using D = UnitToko.Tests.Procurement.ProcurementStockValuationTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementStockValuationBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public Task Browser_independently_values_real_held_receipt_and_recovers_both_original_keys_without_duplicate_posting() => RunAsync(false);

    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public Task Browser_matches_explicit_quality_subset_then_values_it_once_with_real_step_up_and_original_key_recovery() => RunAsync(true);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task RunAsync(bool mixedQuality)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This native browser rehearsal requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await D.CreateAsync(fixture, mixedQuality: mixedQuality, matchInvoice: !mixedQuality); data.Clock.UseSystemClock = true;
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        var issuer = mixedQuality ? configured.Issuer : "https://approved.example/";
        var connection = mixedQuality ? configured.ConnectionId : "fixture-connection";
        var assurance = mixedQuality ? configured.AssurancePolicyVersion : "fixture-policy";
        if (mixedQuality) {
            data.Maker = data.Maker with { ProviderIssuer = issuer }; data.Checker = data.Checker with { ProviderIssuer = issuer };
            data.Reader = data.Reader with { ProviderIssuer = issuer }; data.Outsider = data.Outsider with { ProviderIssuer = issuer };
            data.Documents.Matcher = data.Documents.Matcher with { ProviderIssuer = issuer };
        }
        var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new(issuer, connection, assurance), protector);
        var issued = new Dictionary<long, SessionIssue>();
        var participants = new[] { data.Maker, data.Checker, data.Reader, data.Outsider }.AsEnumerable();
        if (mixedQuality) participants = participants.Append(data.Documents.Matcher);
        foreach (var actor in participants) {
            var identityUpdate = Builders<BsonDocument>.Update.Set("securityVersion", 1L);
            if (mixedQuality) identityUpdate = identityUpdate.Set("issuer", issuer);
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), identityUpdate);
            var now = data.Clock.UtcNow;
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "valuation-" + actor.UserId + "@example.invalid", true,
                connection, now, now, "otp", assurance);
            issued[actor.UserId] = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-valuation-provider", ct));
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic valuation participant " + actor.UserId }, { "verifiedEmail", "valuation-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
        }
        var agedAt = data.Clock.UtcNow.AddMinutes(-6);
        var historicalSessions = new MongoIdentitySessionStore(data.Database, data.Unit, new UnitToko.Tests.Authorization.MutableAuthorizationClock(agedAt),
            new(issuer, connection, assurance), protector);
        var agedProof = new ProviderIdentityProof(data.Maker.ProviderIssuer, data.Maker.ProviderSubject, "valuation-" + data.Maker.UserId + "@example.invalid", true,
            connection, agedAt, agedAt, "otp", assurance);
        var agedSession = await data.Unit.ExecuteAsync((tx, ct) => historicalSessions.CreateAsync(tx,
            new(data.Maker.UserId, 1, data.Maker.GrantVersion, data.Maker.ProviderIssuer, data.Maker.ProviderSubject), agedProof, "synthetic-valuation-aged-factor", ct));
        Assert.Equal(agedAt, Assert.IsType<IdentitySession>(await sessions.ReadAsync(agedSession.OpaqueToken)).Proof.FactorCompletedAt);
        // The stale-factor denial probe needs its own live session: the maker's
        // actual challenge rotates and revokes the old token before this probe runs.
        var agedProbeSession = mixedQuality ? await data.Unit.ExecuteAsync((tx, ct) => historicalSessions.CreateAsync(tx,
            new(data.Maker.UserId, 1, data.Maker.GrantVersion, data.Maker.ProviderIssuer, data.Maker.ProviderSubject), agedProof, "synthetic-valuation-aged-probe", ct)) : agedSession;
        Assert.Equal(agedAt, Assert.IsType<IdentitySession>(await sessions.ReadAsync(agedProbeSession.OpaqueToken)).Proof.FactorCompletedAt);
        // Both actors must complete the real local provider challenge. Start the maker
        // with this genuinely expired factor so a fast run cannot reuse the fresh fixture session.
        if (mixedQuality) issued[data.Maker.UserId] = agedSession;
        async Task<BsonDocument> PreservedAsync() {
            var state = await data.PreservedAsync();
            if (!mixedQuality) return state;
            // Matching changes its heads/effects; its original private source must remain exact.
            var documents = state["documents"].AsBsonDocument;
            foreach (var name in documents.Names.Where(name => name != SupplierDocumentBson.Drafts && name != SupplierDocumentBson.Submissions && name != SupplierDocumentBson.Identities && name != "pins").ToArray()) documents.Remove(name);
            foreach (var table in new[] { ProcurementQualityAcceptanceBson.Proposals, ProcurementQualityAcceptanceBson.Decisions,
                ProcurementQualityAcceptanceBson.Allocations, ProcurementQualityAcceptanceBson.ReceiptHeads }) {
                var rows = await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
                foreach (var row in rows) row.Remove("fence"); state[table] = new BsonArray(rows);
            }
            return state;
        }
        var preserved = await PreservedAsync();
        using var provider = mixedQuality ? new ProviderHandlerTests.FakeProvider(configured, data.Clock) : null;
        var committedRotations = mixedQuality ? new ObserveCommittedRotations(data.Rows("oidcTransactions")) : null;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            if (provider is not null) {
                services.Configure<OpenIdConnectOptions>(Auth0Options.Scheme, options => options.Backchannel = new HttpClient(provider, disposeHandler: false));
                services.AddSingleton<IStartupFilter>(new ProcurementQualityAcceptanceBrowserTests.ObserveProviderChallenge(provider, configured.Issuer));
                services.AddSingleton<IStartupFilter>(committedRotations!);
            }
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<ProcurementStockValuationService>(); services.AddSingleton(data.Service);
            if (mixedQuality) {
                services.RemoveAll<SupplierDocumentService>(); services.AddSingleton(data.Documents.Service);
                services.RemoveAll<SupplierDocumentEvidenceService>(); services.AddSingleton(data.Documents.Evidence);
            }
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko held-stock valuation rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host);
        if (mixedQuality) names.AddDnsName(new Uri(issuer).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "procurement-stock-valuation-" + Guid.NewGuid().ToString("N") + "-native-child.log");
        var observed = mixedQuality ? data.Input(invoiceMatchId: 0) : data.Input(); static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
        SupplierDocumentQualityDto? expectedQuality = null;
        if (mixedQuality) {
            var approved = data.Quality!;
            var qualityProposal = await data.Rows(ProcurementQualityAcceptanceBson.Proposals).Find(new BsonDocument("_id", approved["proposalId"])).SingleAsync();
            var accepted = ProcurementQualityAcceptanceBson.Input(qualityProposal["input"]);
            var receipt = await data.Rows(ProcurementStockReceiptBson.Receipts).Find(new BsonDocument("_id", data.ReceiptId)).SingleAsync();
            expectedQuality = new(new(approved["allocationId"].AsInt64), new(qualityProposal["_id"].AsInt64), new(approved["decisionId"].AsInt64),
                new(data.ReceiptId), receipt["input"]["receiptReference"].AsString, Id(accepted.AcceptedPackages), accepted.LotReference,
                accepted.ExpiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), accepted.AcceptedCondition,
                accepted.PutawayReference, accepted.SegregationReference, new DateTimeOffset(approved["recordedAt"].ToUniversalTime()));
        }
        var valuationInput = new { location = new { organizationId = Id(observed.Location.OrganizationId), shopId = Id(observed.Location.ShopId), warehouseId = Id(observed.Location.WarehouseId), channelId = Id(observed.Location.ChannelId) },
            receiptId = Id(observed.ReceiptId), receiptRevision = Id(observed.ReceiptRevision), invoiceDocumentId = Id(observed.InvoiceDocumentId), invoiceDocumentRevision = Id(observed.InvoiceDocumentRevision),
            invoiceMatchId = mixedQuality ? null : Id(observed.InvoiceMatchId), matchLineNumber = observed.MatchLineNumber, packages = observed.Packages,
            netAmount = observed.NetAmount.ToString(), taxAmount = observed.TaxAmount.ToString(), chargeAmount = observed.ChargeAmount.ToString(), capitalizedAmount = observed.CapitalizedAmount.ToString(),
            policyId = Id(observed.PolicyId), policyRevision = Id(observed.PolicyRevision), postingDate = observed.PostingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), reason = observed.Reason };
        var input = new { procurementStockValuation = true, mixedQuality, providerIssuer = mixedQuality ? issuer : null,
            apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            documentMatcherToken = mixedQuality ? issued[data.Documents.Matcher.UserId].OpaqueToken : null,
            documentMatcherId = mixedQuality ? Id(data.Documents.Matcher.UserId) : null,
            supplierDraftId = mixedQuality ? Id(data.Documents.DraftId) : null,
            supplierMatchInput = mixedQuality ? SupplierDocumentEndpoints.MatchInput(SupplierDocumentBson.Input(data.MixedMatchInput!)) : null,
            quality = expectedQuality,
            financeToken = issued[data.Maker.UserId].OpaqueToken, makerToken = issued[data.Maker.UserId].OpaqueToken, checkerToken = issued[data.Checker.UserId].OpaqueToken,
            readerToken = issued[data.Reader.UserId].OpaqueToken, outsiderToken = issued[data.Outsider.UserId].OpaqueToken, agedToken = agedProbeSession.OpaqueToken,
            makerId = Id(data.Maker.UserId), checkerId = Id(data.Checker.UserId), readerId = Id(data.Reader.UserId), outsiderId = Id(data.Outsider.UserId), valuationInput, decisionReason = D.Reason };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Stock valuation browser child did not start");
        var elapsed = Stopwatch.StartNew();
        // Mixed mode adds a complete private-invoice matching/recovery flow before valuation and real session rotations.
        var journeyBudget = TimeSpan.FromMinutes(mixedQuality ? 10 : 8);
        var lines = new StringBuilder();
        async Task ReadAsync(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); } }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))); process.StandardInput.Close();
            var exit = process.WaitForExitAsync().WaitAsync(journeyBudget); var first = await Task.WhenAny(exit, stdout, stderr); if (first.IsFaulted) await first;
            await exit; await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_PROCUREMENT_STOCK_VALUATION_BROWSER_PASS", lines.ToString());
            if (mixedQuality) Assert.Contains("REAL_PROCUREMENT_MIXED_QUALITY_MATCH_BROWSER_PASS", lines.ToString());
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            try { await Task.WhenAll(stdout, stderr); } finally {
                output.WriteLine($"Actual browser journey: {elapsed.Elapsed.TotalSeconds:F3}s; overall budget: {journeyBudget.TotalSeconds:F0}s");
                output.WriteLine("Actual browser child log: " + logPath);
            }
        }
        Assert.Equal(preserved, await PreservedAsync()); await data.NoPaymentOrReleaseAsync();
        if (mixedQuality) {
            Assert.NotNull(provider); Assert.True(provider.SawValidPkce); Assert.Equal(2, provider.TokenRequests);
            // Consumed challenges retain their five-minute TTL. Observe their actual committed
            // Mongo proof at callback time, before a long journey can legitimately expire it.
            var rotations = Assert.IsType<ObserveCommittedRotations>(committedRotations).Read(); Assert.Equal(2, rotations.Count);
            foreach (var (actor, operation) in new[] { (data.Maker, "procurement.stockvaluation.propose"), (data.Checker, "procurement.stockvaluation.decide") }) {
                var old = issued[actor.UserId]; var transaction = Assert.Single(rotations, row => row["expectedSessionHash"] == old.Session.Reference.Id);
                Assert.Equal("stepUp", transaction["purpose"]); Assert.Equal("consumed", transaction["status"]);
                Assert.Equal(UnitToko.Modules.Identity.Contracts.SessionPolicy.StepUpLifetime,
                    transaction["expiresAt"].ToUniversalTime() - transaction["createdAt"].ToUniversalTime());
                Assert.InRange(transaction["consumedAt"].ToUniversalTime(), transaction["createdAt"].ToUniversalTime(),
                    transaction["expiresAt"].ToUniversalTime().AddTicks(-1));
                Assert.Equal(operation, transaction["operation"]); Assert.Equal(actor.UserId, transaction["expectedUserId"].AsInt64);
                Assert.Null(await sessions.ReadAsync(old.OpaqueToken));
                var previous = await data.Rows("identitySessions").Find(new BsonDocument("_id", old.Session.Reference.Id)).SingleAsync();
                Assert.Equal("rotated", previous["status"]); Assert.Equal("step_up_rotation", previous["revocationReason"]);
                var replacement = await data.Rows("identitySessions").Find(new BsonDocument { { "familyId", previous["familyId"] }, { "status", "active" } }).SingleAsync();
                Assert.Equal(previous["absoluteExpiresAt"], replacement["absoluteExpiresAt"]);
                Assert.True(replacement["factorCompletedAt"].ToUniversalTime() > previous["factorCompletedAt"].ToUniversalTime());
            }
            var match = await data.Rows(SupplierDocumentBson.Matches).Find(new BsonDocument()).SingleAsync();
            Assert.Equal(2, match["schemaVersion"]); Assert.Equal(data.Documents.Matcher.UserId, match["actorId"].AsInt64);
            Assert.Equal(data.MixedMatchInput!.Lines[0].QualityAllocationId, match["input"]["lines"][0]["qualityAllocationId"].AsInt64);
            Assert.Equal(data.ReceiptId, match["links"][0]["quality"]["receiptId"].AsInt64);
            data.Match = SupplierDocumentBson.Result(match);
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                Assert.Equal(1L, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, SupplierDocumentBson.MatchOperation)));
        }
        var proposal = await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync(); var decision = await data.Rows(D.Decisions).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Maker.UserId, proposal["actorId"].AsInt64); Assert.Equal(data.Checker.UserId, decision["actorId"].AsInt64);
        Assert.Equal("approve", decision["decision"].AsString); Assert.Equal(proposal["_id"], decision["proposalId"]);
        Assert.Equal(1L, await data.Rows(D.Allocations).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1L, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        await ProcurementStockValuationTests.JournalAsync(data, decision["journalId"].AsInt64, 11250m);
        Assert.Equal(2L, (await data.Rows(D.ReceiptHeads).Find(new BsonDocument()).SingleAsync())["remainingPackages"].AsInt64);
        Assert.Equal(1L, (await data.Rows(D.MatchHeads).Find(new BsonDocument()).SingleAsync())["remainingPackages"].AsInt64);
        foreach (var operation in new[] { D.ProposeOperation, D.DecideOperation })
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                Assert.Equal(1L, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, operation)));
    }
    private sealed class ObserveCommittedRotations(IMongoCollection<BsonDocument> transactions) : IStartupFilter
    {
        private readonly Dictionary<string, BsonDocument> committed = new(StringComparer.Ordinal);
        internal IReadOnlyList<BsonDocument> Read() { lock (committed) return committed.Values.Select(row => row.DeepClone().AsBsonDocument).ToArray(); }
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use((context, nextRequest) => {
                if (context.Request.Path == Auth0Options.CallbackPath) context.Response.OnStarting(async () => {
                    if (context.Response.StatusCode != 302) return;
                    var rows = await transactions.Find(new BsonDocument { { "purpose", "stepUp" }, { "status", "consumed" } }).ToListAsync();
                    lock (committed) foreach (var row in rows) {
                        var key = row["_id"].AsString;
                        if (committed.TryGetValue(key, out var previous)) Assert.Equal(previous, row);
                        else committed.Add(key, row.DeepClone().AsBsonDocument);
                    }
                });
                return nextRequest();
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
