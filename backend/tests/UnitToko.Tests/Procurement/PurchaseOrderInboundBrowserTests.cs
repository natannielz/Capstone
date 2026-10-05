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
using D = UnitToko.Tests.Procurement.PurchaseOrderTestData;
using S = UnitToko.Platform.Procurement.ProcurementRequestPurchaseOrderSchemas;

namespace UnitToko.Tests.Procurement;

// Genuine producer history with explicitly synthetic people, verification policy, sessions and commercial document.
[Collection("Mongo")]
public sealed class PurchaseOrderInboundBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_plans_real_inactive_receiving_pool_and_preserves_exact_independent_approval_after_original_key_recovery_and_reload()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This native browser rehearsal requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var inbound = await PurchaseOrderInboundBindingTestData.CreateAsync(fixture);
        var data = inbound.Orders; var maker = inbound.Maker; var checker = inbound.Checker;
        var reader = inbound.Data.Reader; var outsider = inbound.Data.Outsider; data.Clock.UseSystemClock = true;
        var poolBefore = await data.Rows("inventoryPools").Find(new BsonDocument("_id", inbound.Data.PoolId)).SingleAsync();
        Assert.False(poolBefore["active"].AsBoolean);
        var setupDecision = await data.Rows("stockSourceDecisions").Find(new BsonDocument("poolId", inbound.Data.PoolId)).SingleAsync();
        var setupView = await inbound.Data.Sources.ReadAsync(inbound.Data.SourceMaker, new(new(100), new(101), new(102), new(103)), setupDecision["proposalId"].AsInt64);
        Assert.NotNull(setupView); var poolLabel = setupView["proposal"]["input"]["label"].AsString;
        var legacy = new BsonDocument();
        foreach (var table in new[] { D.Proposals, D.Decisions, D.Orders, S.Allocations })
            legacy[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var unchangedCustody = new BsonDocument();
        foreach (var table in new[] { "procurementArrivalNotices", "procurementContainerHeads", "procurementContainerIntakes", "procurementInspectionHeads", "procurementInspectionDrafts", "procurementInspectionSubmissions",
            "procurementHeldStockSourceHeads", "procurementHeldStockSourceTransitions", "stockSourceProposals", "stockSourceDecisions", "stockSourcePolicyProposals", "stockSourcePolicyDecisions", "stockSourcePolicies",
            "procurementStockReceipts", "procurementQuarantineDispositions", "lots", "stockBalances", "movements", "reservations", "journalEntries", "invoices", "paymentAllocations", "refundPayouts", "disbursements", "bankReceipts" })
            unchangedCustody[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var issued = new Dictionary<long, SessionIssue>();
        foreach (var actor in new[] { maker, checker, reader, outsider }) {
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L)); var now = data.Clock.UtcNow;
            var providerProof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "po-" + actor.UserId + "@example.invalid", true, "fixture-connection", now, now, "otp", "fixture-policy");
            issued[actor.UserId] = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), providerProof, "synthetic-inbound-po-provider", ct));
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", "Synthetic purchase-order participant " + actor.UserId },
                { "verifiedEmail", "po-" + actor.UserId + "@example.invalid" }, { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
        }
        var original = new BsonDocument();
        foreach (var table in new[] { "organizationProfiles", "organizationProposals", "authorizationHistory", "authorizationScopes", "catalogCategories", "productFamilies", "skus", "variantDefinitions", ProcurementRequestTestData.Requests,
            ProcurementRequestTestData.Decisions, SupplierTestData.Policies, SupplierTestData.Proposals, SupplierTestData.Observations, SupplierTestData.Decisions, SupplierTestData.Versions }) {
            var rows = await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); original[table] = new BsonArray(rows);
        }
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<PurchaseOrderService>(); services.AddSingleton(data.Service);
            services.RemoveAll<ProcurementStockReceiptService>(); services.AddSingleton(inbound.Data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses); const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko purchase-order rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var until = DateTimeOffset.FromUnixTimeSeconds(data.Clock.UtcNow.ToUnixTimeSeconds() / 60 * 60).AddDays(1);
        var date = UnitToko.Modules.Accounting.AccountingCalendar.BusinessDate(data.Clock.UtcNow);
        var proofPath = Path.Combine(root, "tmp", "production", "invoice-browser-results", "purchase-order-inbound-" + Guid.NewGuid().ToString("N") + "-proof.json");
        var input = new { purchaseOrders = true, purchaseOrderInbound = true, proofPath,
            poolId = inbound.Data.PoolId.ToString(), poolRevision = poolBefore["revision"].AsInt64.ToString(), poolLabel,
            legacyProposalId = legacy[D.Proposals].AsBsonArray.Single()["_id"].AsInt64.ToString(), apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = issued[maker.UserId].OpaqueToken, makerToken = issued[maker.UserId].OpaqueToken, checkerToken = issued[checker.UserId].OpaqueToken,
            readerToken = issued[reader.UserId].OpaqueToken, outsiderToken = issued[outsider.UserId].OpaqueToken,
            makerId = maker.UserId.ToString(), checkerId = checker.UserId.ToString(), readerId = reader.UserId.ToString(), outsiderId = outsider.UserId.ToString(),
            organizationId = "100", shopId = "101", warehouseId = "102", channelId = "103", requestId = data.Request["requestId"].AsInt64.ToString(), requestDecisionId = data.RequestDecision["decisionId"].AsInt64.ToString(),
            supplierId = data.Supplier["supplierId"].AsInt64.ToString(), supplierVersionId = data.Supplier["versionId"].AsInt64.ToString(), supplierVersion = data.Supplier["version"].AsInt64.ToString(),
            supplierName = SupplierTestData.Details.DisplayName, supplierSubject = SupplierTestData.Details.SubjectReference,
            businessDate = date.ToString("yyyy-MM-dd"), dueDate = date.AddDays(30).ToString("yyyy-MM-dd"), validUntil = until.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            organizationName = "Synthetic cooperative", shopName = "Synthetic fulfillment shop", warehouseName = "Synthetic dispatch warehouse", channelName = "Synthetic retail channel",
            skuId = data.Procurement.Sku.Id.ToString(), definitionId = data.Procurement.Sku.CurrentDefinitionId!.Value.ToString(), definitionVersion = data.Procurement.Sku.DefinitionVersion!.Value.ToString(),
            skuCode = "PROC-PENCIL-12", familyName = "Pensil pengadaan", packageLabel = "Dus", specification = "Synthetic approved logo specification" };
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "purchase-order-inbound-" + Guid.NewGuid().ToString("N") + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Purchase-order browser child did not start");
        async Task ReadAsync(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) { lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); } } }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try { await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
            await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString()); Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_PURCHASE_ORDER_INBOUND_BROWSER_PASS", lines.ToString());
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser child log: " + logPath); }
        Assert.True(File.Exists(proofPath)); using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(proofPath));
        var observed = proof.RootElement; Assert.True(observed.GetProperty("localSyntheticOnly").GetBoolean());
        Assert.False(observed.GetProperty("hostedProviderAcceptance").GetBoolean()); Assert.True(observed.GetProperty("actualBrowserApiMongo").GetBoolean());
        var screenshots = observed.GetProperty("screenshotPaths").EnumerateArray().ToArray(); Assert.Equal(4, screenshots.Length);
        foreach (var screenshot in screenshots) { Assert.True(Path.IsPathFullyQualified(screenshot.GetString()!)); Assert.True(File.Exists(screenshot.GetString())); }
        var finalView = observed.GetProperty("finalProposal"); var plan = finalView.GetProperty("inboundPools");
        var proposalId = long.Parse(finalView.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var decisionId = long.Parse(finalView.GetProperty("decision").GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var proposal = await data.Rows(D.Proposals).Find(new BsonDocument("_id", proposalId)).SingleAsync();
        var decision = await data.Rows(D.Decisions).Find(new BsonDocument("_id", decisionId)).SingleAsync();
        var order = await data.Rows(D.Orders).Find(new BsonDocument("proposalId", proposalId)).SingleAsync();
        var allocation = await data.Rows(S.Allocations).Find(new BsonDocument("orderId", order["_id"])).SingleAsync();
        var selection = await data.Rows(PurchaseOrderInboundBindingBson.Selections).Find(new BsonDocument("_id", proposalId)).SingleAsync();
        var selectedDecision = await data.Rows(PurchaseOrderInboundBindingBson.Decisions).Find(new BsonDocument("_id", decisionId)).SingleAsync();
        Assert.Equal(maker.UserId, proposal["actorId"].AsInt64); Assert.Equal(checker.UserId, decision["actorId"].AsInt64);
        Assert.Equal("approve", decision["decision"]); Assert.Equal("approved", finalView.GetProperty("state").GetString());
        Assert.Equal(proposal["_id"].AsInt64.ToString(), plan.GetProperty("selectionId").GetString());
        Assert.Equal(selection["snapshotHash"].AsString, plan.GetProperty("selectionHash").GetString());
        Assert.Equal(decisionId.ToString(), plan.GetProperty("decisionId").GetString()); Assert.Equal("approve", plan.GetProperty("state").GetString());
        Assert.Equal(proposal["snapshotHash"], selection["proposalHash"]); Assert.Equal(decision["snapshotHash"], selectedDecision["decisionHash"]);
        Assert.Equal(selection["snapshotHash"], selectedDecision["selectionHash"]); Assert.Equal(order["_id"], selectedDecision["orderId"]);
        Assert.Equal(PurchaseOrderInboundBindingBson.StableSources(selection["receivingSources"].AsBsonArray),
            PurchaseOrderInboundBindingBson.StableSources(selectedDecision["receivingSources"].AsBsonArray));
        var selected = Assert.Single(selection["allocations"].AsBsonArray).AsBsonDocument;
        Assert.Equal(2, selected["packages"].AsInt32); Assert.Equal(inbound.Data.PoolId, selected["poolId"].AsInt64);
        Assert.Equal(poolBefore["revision"], selected["poolRevision"]); Assert.Equal(data.Procurement.Sku.Id.Value, selected["requestSkuId"].AsInt64);
        var wireAllocation = Assert.Single(plan.GetProperty("allocations").EnumerateArray());
        Assert.Equal(2, wireAllocation.GetProperty("packages").GetInt32()); Assert.Equal(inbound.Data.PoolId.ToString(), wireAllocation.GetProperty("poolId").GetString());
        Assert.Equal(selected["requestSkuId"].AsInt64.ToString(), wireAllocation.GetProperty("requestSkuId").GetString());
        Assert.Equal(selected["poolRevision"].AsInt64.ToString(), wireAllocation.GetProperty("poolRevision").GetString());
        foreach (var flag in new[] { "unplannedPackagesBound", "stockReserved", "physicalStockReleased" }) Assert.False(plan.GetProperty(flag).GetBoolean());
        Assert.Equal(data.Request["requestId"], order["requestId"]); Assert.Equal(data.RequestDecision["decisionId"], order["requestDecisionId"]);
        Assert.Equal(order["_id"], allocation["orderId"]); Assert.Equal(decision["snapshotHash"], allocation["approvalHash"]); Assert.Equal(2, allocation["packages"].AsInt32);
        Assert.Equal(5, (await data.Rows(S.Commitments).Find(new BsonDocument()).SingleAsync())["committedPackages"].AsInt32);
        Assert.Equal(observed.GetProperty("proposalKey").GetString(), proposal["requestKey"].AsString);
        Assert.Equal(observed.GetProperty("decisionKey").GetString(), decision["requestKey"].AsString);
        foreach (var row in new[] { proposal, decision }) {
            Assert.Equal(PurchaseOrderBson.Audit(row), await data.Rows("auditEvents").Find(new BsonDocument("_id", row["auditId"])).SingleAsync());
            var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).SingleAsync();
            Assert.Equal(row["bodyHash"], receipt["bodyHash"]); Assert.Equal(PurchaseOrderBson.Result(row), receipt["result"]);
            var effect = await data.Rows("outboxEvents").Find(new BsonDocument("sourceKey", PurchaseOrderBson.SourceKey(row))).SingleAsync();
            Assert.Equal(PurchaseOrderBson.Result(row), effect["payload"]); Assert.Equal(row["requestKey"], effect["requestKey"]);
            Assert.Equal(1, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument { { "actorId", row["actorId"] }, { "kind", row["operation"] } }));
            Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] } }));
        }
        foreach (var table in new[] { D.Proposals, D.Decisions, D.Orders, S.Allocations }) {
            Assert.Equal(legacy[table].AsBsonArray.Count + 1, await data.Rows(table).CountDocumentsAsync(new BsonDocument()));
            foreach (var row in legacy[table].AsBsonArray) Assert.Equal(row, await data.Rows(table).Find(new BsonDocument("_id", row["_id"])).SingleAsync());
        }
        var poolAfter = await data.Rows("inventoryPools").Find(new BsonDocument("_id", inbound.Data.PoolId)).SingleAsync();
        Assert.False(poolAfter["active"].AsBoolean); Assert.Equal(poolBefore["revision"], poolAfter["revision"]);
        Assert.True(poolAfter["fence"].AsInt64 > poolBefore["fence"].AsInt64); poolBefore.Remove("fence"); poolAfter.Remove("fence"); Assert.Equal(poolBefore, poolAfter);
        foreach (var element in unchangedCustody) Assert.Equal(element.Value, new BsonArray(await data.Rows(element.Name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        foreach (var element in original) { var rows = await data.Rows(element.Name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); Assert.Equal(element.Value, new BsonArray(rows)); }
        await data.NoFinancialOrReceiptEffectsAsync(); output.WriteLine("Actual inbound browser proof: " + proofPath);

    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
