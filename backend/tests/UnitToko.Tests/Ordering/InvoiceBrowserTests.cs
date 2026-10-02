using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class InvoiceBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public Task Browser_issues_received_goods_recovers_lost_response_and_downloads_private_invoice() => RunAsync(false);
    [LocalStockBrowserFact]
    public Task Browser_links_finalized_sale_case_recovers_original_request_and_preserves_sale() => RunAsync(true);
    [LocalStockBrowserFact]
    public Task Browser_independently_decides_return_recovers_lost_decision_and_shows_private_buyer_explanation() => RunAsync(true, true);
    [LocalStockBrowserFact]
    public Task Browser_receives_inspects_and_requests_return_resolution_recovers_lost_replies_and_preserves_pack_stock_and_money() => RunAsync(true, true, true);
    [LocalStockBrowserFact]
    public Task Browser_requests_exact_return_entitlement_recovers_original_and_preserves_invoice_cash_and_quarantine() => RunAsync(true, true, true, true);
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_independent_Finance_credits_and_reserves_original_funding_recovers_exact_lost_approval_without_resend_or_cash_transfer() {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The configured native evidence and browser fixture requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await ReturnFinancialApprovalTests.Data.CreateAsync(fixture, "refund");
        var source = data.Source; var invoice = source.Payment.Invoice; var order = invoice.Order; var catalog = order.Prices.Catalog;
        // Like the existing invoice/bank browser fixtures, seed the actual actor's persisted
        // synthetic account profile; the production identity/session endpoint remains intact.
        await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", data.Actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
            { "displayName", "Independent Finance checker fixture" }, { "verifiedEmail", "finance-checker@example.invalid" },
            { "verifiedAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime } });
        var original = await data.OriginalAsync();
        var stock = new Dictionary<string, List<BsonDocument>>();
        foreach (var table in new[] { "stockBalances", "lots", "movements", "reservations" }) {
            var rows = await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var stockRow in rows) { stockRow.Remove("fence"); stockRow.Remove("postingFence"); } stock[table] = rows;
        }
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(catalog.Sessions);
            services.RemoveAll<InvoiceService>(); services.AddSingleton(invoice.Service);
            services.RemoveAll<AfterSalesCaseService>(); services.AddSingleton(new AfterSalesCaseService(data.Database, new(catalog.Client), new(data.Database), data.Clock, invoice.Service));
            services.RemoveAll<ReturnEntitlementService>(); services.AddSingleton(source.Service);
            services.RemoveAll<PaymentAllocationService>(); services.AddSingleton(data.Allocations);
            services.RemoveAll<ReturnFinancialApprovalService>(); services.AddSingleton(data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko financial approval rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = catalog.Issued[data.Actor.UserId].OpaqueToken, buyerToken = catalog.Issued[order.Buyer.UserId].OpaqueToken,
            courierToken = catalog.Issued[invoice.Dispatch.Courier.UserId].OpaqueToken, makerToken = catalog.Issued[source.Actor.UserId].OpaqueToken,
            financeId = data.Actor.UserId.ToString(), makerId = source.Actor.UserId.ToString(), buyerId = order.Buyer.UserId.ToString(), financialApproval = true,
            caseId = source.CaseId.ToString(), returnId = source.ReturnId.ToString(), invoiceId = data.InvoiceId.ToString(), action = source.Action,
            originalAllocationId = data.Input.Releases[0].OriginalAllocationId.Value.ToString(), expectedInvoiceVersion = data.Input.ExpectedInvoiceVersion.ToString(), expectedReceiptVersion = data.Input.Releases[0].ExpectedReceiptVersion.ToString() };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Financial approval browser child did not start");
        try {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); output.WriteLine(await stdout); output.WriteLine(await stderr);
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("REAL_RETURN_FINANCIAL_APPROVAL_BROWSER_PASS", await stdout);
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        var row = await data.Rows(ReturnFinancialApprovalBson.Events).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Actor.UserId, row["actorId"].AsInt64); Assert.NotEqual(source.Actor.UserId, row["actorId"].AsInt64);
        Assert.Equal(source.CaseId, row["caseId"].AsInt64); Assert.Equal(source.ReturnId, row["returnId"].AsInt64);
        Assert.Equal(1000.02m, row["credit"].AsDecimal); Assert.Equal(1000m, row["reservedRefund"].AsDecimal);
        Assert.StartsWith("fa." + source.CaseId + "." + source.ReturnId + ".refund.", row["requestKey"].AsString);
        var head = await data.Rows(PaymentAllocationBson.Invoices).Find(new BsonDocument("_id", data.InvoiceId)).SingleAsync();
        Assert.Equal(2L, head["version"].AsInt64); Assert.Equal(0m, head["paid"].AsDecimal); Assert.Equal(1000.02m, head["credited"].AsDecimal);
        var receipt = await data.Rows(PaymentAllocationBson.Receipts).Find(new BsonDocument()).SingleAsync(); Assert.Equal(2L, receipt["version"].AsInt64);
        Assert.Equal(0m, receipt["allocated"].AsDecimal); Assert.Equal(1000m, receipt["reservedForRefund"].AsDecimal); Assert.Equal(3000m, receipt["total"].AsDecimal - receipt["allocated"].AsDecimal - receipt["reservedForRefund"].AsDecimal);
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", "billing.return-financial")));
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") }) Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, ReturnFinancialApprovalBson.Operation)));
        await data.AssertOriginalAsync(original);
        foreach (var table in stock.Keys) { var rows = await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var current in rows) { current.Remove("fence"); current.Remove("postingFence"); } Assert.Equal(stock[table], rows); }
    }
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_records_exact_refund_private_evidence_recovers_lost_reply_without_resend_or_money_transfer() {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This private evidence/browser fixture requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await RefundPayoutTests.Data.CreateAsync(fixture, createDraft: false);
        var source = data.Source; var catalog = data.Catalog; var invoice = source.Allocation.Source.Read.Payment.Invoice;
        // Persist the real synthetic users' current profiles before the HTTP host loads their
        // already-issued real opaque sessions. No alternate session endpoint or actor is used.
        foreach (var (actor, display) in new[] { (data.Actor, "Independent refund evidence recorder fixture"), (source.Approver, "Independent refund approver fixture") })
            if (!await data.Rows("accountProfiles").Find(new BsonDocument("_id", actor.UserId)).AnyAsync())
                await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                    { "displayName", display }, { "verifiedEmail", "refund-" + actor.UserId + "@example.invalid" },
                    { "verifiedAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime } });
        var protectedTables = new[] { PaymentAllocationBson.Invoices, PaymentAllocationBson.Receipts, PaymentAllocationBson.Events,
            ReturnEntitlementBson.Requests, ReturnEntitlementBson.Pools, ReturnFinancialApprovalBson.Events, BankReceiptBson.Receipts, BankReceiptBson.Accounts,
            "journalEntries", "stockBalances", "lots", "movements", "reservations", "stockMovements", "invoices", "paymentSubmissions" }.Distinct().ToArray();
        async Task<BsonDocument> ProtectedAsync() {
            var snapshot = new BsonDocument();
            foreach (var table in protectedTables) {
                var rows = await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
                foreach (var row in rows) { row.Remove("fence"); if (table == "invoices") row.Remove("postingFence"); }
                // Keep original snapshot hashes, money, quantities and versions in the assertion.
                snapshot[table] = new BsonArray(rows);
            }
            return snapshot;
        }
        var original = await ProtectedAsync();
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(catalog.Sessions);
            services.RemoveAll<InvoiceService>(); services.AddSingleton(invoice.Service);
            services.RemoveAll<AfterSalesCaseService>(); services.AddSingleton(source.Cases);
            services.RemoveAll<ReturnEntitlementService>(); services.AddSingleton(source.Entitlements);
            services.RemoveAll<PaymentAllocationService>(); services.AddSingleton(source.Allocation.Service);
            services.RemoveAll<ReturnFinancialApprovalService>(); services.AddSingleton(source.Service);
            services.RemoveAll<RefundPayoutService>(); services.AddSingleton(data.Service);
            services.RemoveAll<RefundPayoutEvidenceService>(); services.AddSingleton(data.Evidence);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko refund evidence rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var artifacts = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(artifacts);
        var run = "refund-payout-" + Guid.NewGuid().ToString("N"); var imagePath = Path.Combine(artifacts, run + ".png"); var validationReadyPath = Path.Combine(artifacts, run + "-validated.txt");
        await File.WriteAllBytesAsync(imagePath, data.Bytes);
        var input = new { apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = catalog.Issued[data.Actor.UserId].OpaqueToken, recorderToken = catalog.Issued[data.Actor.UserId].OpaqueToken,
            approverToken = catalog.Issued[source.Approver.UserId].OpaqueToken, recorderId = data.Actor.UserId.ToString(), approverId = source.Approver.UserId.ToString(),
            caseId = source.CaseId.ToString(), returnId = source.ReturnId.ToString(), invoiceId = source.Allocation.InvoiceId.ToString(), approvalId = data.ApprovalId.ToString(),
            transferDate = data.Input.TransferDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), transferReference = data.Input.TransferReference,
            reservedRefund = data.Input.Amount.ToString(), imagePath, validationReadyPath, refundPayout = true };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Refund payout browser child did not start");
        var text = new StringBuilder(); var validationStarted = false; var stdout = ReadOutputAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            var exit = process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            var first = await Task.WhenAny(exit, stdout); if (first == stdout && stdout.IsFaulted) await stdout;
            await exit; var result = await stdout;
            Assert.True(process.ExitCode == 0, result + await stderr); Assert.Contains("REAL_REFUND_PAYOUT_BROWSER_PASS", result); Assert.True(validationStarted);
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            try { await stdout; } catch (Exception error) { text.AppendLine("NATIVE_WORKER_OR_CHILD_OUTPUT_FAILED " + error); }
            var failure = await stderr; output.WriteLine(text.ToString()); output.WriteLine(failure);
            await File.WriteAllTextAsync(Path.Combine(artifacts, run + "-native-child.log"), text + Environment.NewLine + failure);
        }
        async Task<string> ReadOutputAsync() {
            while (await process.StandardOutput.ReadLineAsync() is { } line) {
                text.AppendLine(line);
                if (line == "VALIDATE_REFUND_PAYOUT_EVIDENCE_READY") {
                    Assert.False(validationStarted); validationStarted = true;
                    await data.ValidateAsync(); await File.WriteAllTextAsync(validationReadyPath, "Actual native validation processor completed");
                }
            }
            return text.ToString();
        }
        Assert.Equal(original, await ProtectedAsync());
        var draft = await data.Rows(RefundPayoutBson.Drafts).Find(new BsonDocument()).SingleAsync();
        var payout = await data.Rows(RefundPayoutBson.Payouts).Find(new BsonDocument()).SingleAsync();
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "RefundPayout")).SingleAsync();
        Assert.Equal(data.Actor.UserId, payout["recordedBy"].AsInt64); Assert.NotEqual(source.Approver.UserId, payout["recordedBy"].AsInt64);
        Assert.Equal(data.ApprovalId, payout["financialApprovalId"].AsInt64); Assert.Equal("recorded_unverified", payout["state"]);
        Assert.Equal(600m, payout["input"]["amount"].AsDecimal); Assert.Equal(payout["_id"], draft["recordedPayoutId"]);
        Assert.Equal(payout["pinId"], pin["_id"]); Assert.Equal(payout["_id"], pin["associationId"]); Assert.Equal(draft["_id"], pin["refundPayoutDraftId"]);
        Assert.Equal(payout["snapshotHash"], pin["associationHash"]); Assert.Equal(payout["evidence"], pin["reference"]);
        Assert.Equal(1, await data.Rows("evidencePinAudits").CountDocumentsAsync(new BsonDocument("pinId", pin["_id"])));
        foreach (var operation in new[] { RefundPayoutBson.DraftOperation, RefundPayoutBson.Operation })
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, operation)));
        Assert.StartsWith("rpd." + data.ApprovalId + ".", draft["requestKey"].AsString); Assert.StartsWith("rpr." + draft["_id"].AsInt64 + ".", payout["requestKey"].AsString);
        Assert.Equal(RefundPayoutBson.Result(payout), await data.Service.RecoverRecordAsync(data.Actor, draft["_id"].AsInt64, payout["requestKey"].AsString));
        var leases = await data.Rows("evidenceReadLeases").Find(new BsonDocument("target.kind", "RefundPayout")).ToListAsync();
        Assert.NotEmpty(leases); Assert.All(leases, row => Assert.Equal("released", row["state"]));
        Assert.Equal(original, await ProtectedAsync());
    }
    private async Task RunAsync(bool afterSales, bool caseDecision = false, bool caseReturn = false, bool financialRequest = false) {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await InvoiceData.CreateAsync(fixture); var order = data.Order;
        if (afterSales) await order.Prices.Catalog.Base.AddGrantAsync(order.Buyer, 99810, CatalogAuthoringTestData.Audience, [Permissions.CaseRead, Permissions.CaseRequest]);
        if (caseDecision) {
            await order.Prices.Catalog.Base.AddGrantAsync(data.Finance, 99830, CatalogAuthoringTestData.Shop, [Permissions.CaseRead, Permissions.CaseManage]);
            foreach (var person in new[] { order.Buyer.UserId, data.Finance.UserId }) if (!await data.Rows("personRecords").Find(new BsonDocument("_id", PersonTestData.PersonId(person))).AnyAsync())
                await PersonTestData.SeedDistinctPeopleAsync(order.Database, order.Prices.Catalog.Clock, person);
        }
        if (caseReturn) await order.Prices.Catalog.Base.AddGrantAsync(data.Finance, 99831, CatalogAuthoringTestData.Shop, [Permissions.StockReceive, Permissions.StockCount]);
        if (financialRequest) {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This rehearsal requires the configured Windows browser and evidence runtime");
            await order.Prices.Catalog.Base.AddGrantAsync(data.Finance, 99833, CatalogAuthoringTestData.Shop, [Permissions.CreditRequest]);
            await ReturnEntitlementTests.SeedPolicyAsync(data);
        }
        var originalStock = await data.Rows("stockBalances").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); Assert.NotEmpty(originalStock);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
            services.RemoveAll<InvoiceService>(); services.AddSingleton(data.Service);
            if (afterSales) {
                var caseOwner = new AfterSalesCaseService(order.Database, new(order.Prices.Catalog.Client), new(order.Database), order.Prices.Catalog.Clock, data.Service);
                services.RemoveAll<AfterSalesCaseService>(); services.AddSingleton(caseOwner);
                if (financialRequest) {
                    services.RemoveAll<ReturnEntitlementService>(); services.AddSingleton(new ReturnEntitlementService(order.Database, new(order.Prices.Catalog.Client), new(order.Database), order.Prices.Catalog.Clock, caseOwner, data.Service));
                }
            }
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko invoice rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = order.Prices.Catalog.Issued[data.Finance.UserId].OpaqueToken, buyerToken = order.Prices.Catalog.Issued[order.Buyer.UserId].OpaqueToken,
            courierToken = order.Prices.Catalog.Issued[data.Dispatch.Courier.UserId].OpaqueToken, financeId = data.Finance.UserId.ToString(), buyerId = order.Buyer.UserId.ToString(), receiptId = data.Receipt.Id.ToString(), afterSales, caseDecision, caseReturn, financialRequest };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser child did not start");
        try {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(financialRequest ? 5 : 3)); output.WriteLine(await stdout); output.WriteLine(await stderr);
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("REAL_INVOICE_BROWSER_PASS", await stdout);
            if (afterSales) Assert.Contains("REAL_CASE_BROWSER_PASS", await stdout);
            if (caseDecision) Assert.Contains("REAL_CASE_DECISION_BROWSER_PASS", await stdout);
            if (caseReturn) Assert.Contains("REAL_RETURN_QUARANTINE_BROWSER_PASS", await stdout);
            if (caseReturn) Assert.Contains("REAL_RETURN_INSPECTION_BROWSER_PASS", await stdout);
            if (caseReturn) Assert.Contains("REAL_RETURN_RESOLUTION_BROWSER_PASS", await stdout);
            if (financialRequest) Assert.Contains("REAL_RETURN_ENTITLEMENT_BROWSER_PASS", await stdout);
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        Assert.Equal(1, await data.Count(InvoiceBson.Invoices)); Assert.Equal(1, await data.Count(InvoiceBson.Allocations)); Assert.Equal(1, await data.Count("journalEntries"));
        var row = await data.Rows(InvoiceBson.Invoices).Find(new BsonDocument()).SingleAsync(); Assert.Equal(6L, row["lines"][0]["acceptedBaseUnits"].AsInt64);
        Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", InvoiceBson.Operation)));
        Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", InvoiceBson.Operation)));
        Assert.Equal(originalStock, await data.Rows("stockBalances").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        if (caseReturn) {
            var intake = await data.Rows("returnQuarantineReceipts").Find(new BsonDocument()).SingleAsync(); Assert.Equal("quarantined", intake["state"]); Assert.Equal("unverified", intake["lotIdentity"]);
            Assert.Equal(1L, intake["input"]["lines"][0]["receivedBaseUnits"].AsInt64); Assert.Equal("opened", intake["input"]["lines"][0]["observedCondition"]);
            Assert.Equal(12, intake["source"]["stock"][0]["baseUnitsPerPackage"].AsInt32); Assert.Equal(data.Finance.UserId, intake["actorId"]);
            Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "fulfillment.case.return_receive"))); Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.case.return_receive")));
            var inspection = await data.Rows("returnInspections").Find(new BsonDocument()).SingleAsync(); Assert.Equal(intake["_id"], inspection["_id"]); Assert.Equal("inspected_quarantine", inspection["state"]); Assert.Equal("unverified", inspection["lotIdentity"]);
            Assert.Equal("matched", inspection["input"]["lines"][0]["identity"]); Assert.Equal("opened", inspection["input"]["lines"][0]["condition"]); Assert.Equal(1L, inspection["input"]["lines"][0]["inspectedBaseUnits"].AsInt64);
            Assert.Equal("LOT-PRIVAT", inspection["input"]["lines"][0]["observedLotLabel"]); Assert.Equal(intake["snapshotHash"], inspection["source"]["returnHash"]);
            Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "fulfillment.case.return_inspect"))); Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.case.return_inspect")));
            var proposal = await data.Rows("returnResolutionRequests").Find(new BsonDocument()).SingleAsync(); Assert.Equal(inspection["_id"], proposal["_id"]); Assert.Equal("resolution_requested", proposal["state"]);
            Assert.Equal(financialRequest ? "credit" : "replacement", proposal["input"]["action"]); Assert.Equal(inspection["snapshotHash"], proposal["source"]["inspectionHash"]); Assert.Equal(1L, proposal["source"]["lines"][0]["inspectedBaseUnits"].AsInt64);
            Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "fulfillment.case.resolution_request"))); Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.case.resolution_request")));
            if (financialRequest) {
                var request = await data.Rows(ReturnEntitlementBson.Requests).Find(new BsonDocument()).SingleAsync();
                Assert.Equal(inspection["_id"], request["_id"]); Assert.Equal(inspection["snapshotHash"], request["source"]["inspectionHash"]); Assert.Equal(row["snapshotHash"], request["source"]["invoice"]["snapshotHash"]);
                Assert.Equal(1000.02m, request["credit"].AsDecimal); Assert.Equal(1L, request["lines"][0]["attributedBaseUnits"].AsInt64);
                var pool = await data.Rows(ReturnEntitlementBson.Pools).Find(new BsonDocument()).SingleAsync(); Assert.Equal(1L, pool["version"].AsInt64); Assert.Equal(1000.02m, pool["lines"][0]["net"].AsDecimal);
                foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") }) Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, ReturnEntitlementBson.Operation)));
                Assert.Equal(0, await data.Rows(PaymentAllocationBson.Invoices).CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows(PaymentAllocationBson.Receipts).CountDocumentsAsync(new BsonDocument()));
            }
        }
        if (afterSales) {
            Assert.Equal(1, await data.Count("afterSalesCases")); var complaint = await data.Rows("afterSalesCases").Find(new BsonDocument()).SingleAsync();
            Assert.Equal(row["_id"], complaint["input"]["invoiceId"]); Assert.Equal(1L, complaint["input"]["lines"][0]["reportedBaseUnits"].AsInt64);
            Assert.Equal(row["snapshotHash"], complaint["source"]["invoiceHash"]); Assert.Equal(6L, complaint["source"]["lines"][0]["acceptedBaseUnits"].AsInt64);
            Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "fulfillment.case.request")));
            Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.case.request")));
            if (caseDecision) {
                var decision = await data.Rows("afterSalesCaseDecisions").Find(new BsonDocument()).SingleAsync(); Assert.Equal(complaint["_id"], decision["_id"]);
                Assert.Equal("return_authorized", decision["state"]); Assert.Equal(data.Finance.UserId, decision["actorId"]); Assert.Equal(order.Buyer.UserId, decision["source"]["requestedBy"]);
                Assert.Equal(1L, decision["input"]["lines"][0]["reportedBaseUnits"].AsInt64);
                Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "fulfillment.case.decide")));
                Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.case.decide")));
                Assert.Equal(1L, row["postingFence"].AsInt64);
            }
        }
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
