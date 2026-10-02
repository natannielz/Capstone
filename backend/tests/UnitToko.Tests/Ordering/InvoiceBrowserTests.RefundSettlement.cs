using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
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
using UnitToko.Platform.Identity;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using D = UnitToko.Platform.Billing.RefundSettlementBson;

namespace UnitToko.Tests.Ordering;

public sealed partial class InvoiceBrowserTests
{
    [LocalStockBrowserFact]
    [SupportedOSPlatform("windows")]
    public async Task Browser_independently_reviews_private_refund_matches_bank_debit_and_recovers_exact_lost_settlement_without_resend() {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The actual private evidence and browser fixture requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await RefundSettlementTests.Data.CreateAsync(fixture);
        var payout = data.Payout; var source = payout.Source; var catalog = data.Catalog; var invoice = source.Allocation.Source.Read.Payment.Invoice;
        foreach (var (actor, display) in new[] { (data.Verifier, "Independent refund debit verifier fixture"), (payout.Actor, "Refund evidence recorder fixture") })
            if (!await data.Rows("accountProfiles").Find(new BsonDocument("_id", actor.UserId)).AnyAsync())
                await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                    { "displayName", display }, { "verifiedEmail", "refund-debit-" + actor.UserId + "@example.invalid" },
                    { "verifiedAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime } });
        var before = await payout.Source.ReadBalancesAsync(); var permanent = await data.PermanentAsync();
        var preview = await data.PreviewAsync(); Assert.False(await data.Rows(D.Events).Find(new BsonDocument()).AnyAsync());
        var expected = preview.View["receipts"].AsBsonArray.Select(receipt => new {
            receiptId = receipt["receiptId"].AsInt64.ToString(), paid = InvoiceBson.Money(receipt["paidAfter"]).ToString(),
            pending = InvoiceBson.Money(receipt["pending"]).ToString(), available = InvoiceBson.Money(receipt["available"]).ToString()
        }).ToArray();
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
            services.RemoveAll<RefundPayoutEvidenceService>(); services.AddSingleton(payout.Evidence);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko refund debit rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new {
            apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = catalog.Issued[data.Verifier.UserId].OpaqueToken, verifierToken = catalog.Issued[data.Verifier.UserId].OpaqueToken,
            recorderToken = catalog.Issued[payout.Actor.UserId].OpaqueToken, verifierId = data.Verifier.UserId.ToString(), recorderId = payout.Actor.UserId.ToString(),
            importerId = data.Importer.UserId.ToString(), caseId = source.CaseId.ToString(), returnId = source.ReturnId.ToString(), invoiceId = source.Allocation.InvoiceId.ToString(),
            payoutId = data.PayoutId.ToString(), reservationId = payout.Reservation.ReservationId.ToString(), amount = payout.Input.Amount.ToString(),
            evidence = new { id = payout.Reference.Id.ToString(), version = payout.Reference.Version.ToString(), sha256 = payout.Reference.Sha256 },
            bankImportId = data.Input.BankImportId.ToString(), bankRowNumber = data.Input.BankRowNumber,
            beneficiaryReference = data.Input.BeneficiaryVerificationReference, transferReference = data.Input.TransferVerificationReference,
            receipts = expected, refundSettlement = true
        };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Refund settlement browser child did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("REAL_REFUND_SETTLEMENT_BROWSER_PASS", await stdout);
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            var text = await stdout + Environment.NewLine + await stderr; output.WriteLine(text);
            var artifacts = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(artifacts);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "refund-settlement-" + Guid.NewGuid().ToString("N") + "-native-child.log"), text);
        }
        Assert.Equal(permanent, await data.PermanentAsync());
        var row = await data.Rows(D.Events).Find(new BsonDocument()).SingleAsync(); Assert.Equal(data.Verifier.UserId, row["actorId"].AsInt64);
        Assert.Equal(data.PayoutId, row["payoutId"].AsInt64); Assert.Equal(data.PayoutRecord["snapshotHash"], row["payoutHash"]);
        Assert.Equal(data.Input.BankImportId, row["debit"]["importId"].AsInt64); Assert.Equal(data.Input.BankRowNumber, row["debit"]["rowNumber"].AsInt32);
        Assert.Equal(payout.Input.Amount.Value, row["amount"].AsDecimal); Assert.StartsWith("rsv." + data.PayoutId + ".", row["requestKey"].AsString);
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("source.namespace", D.Operation)).SingleAsync(); Assert.Equal(row["journalId"], journal["_id"]);
        Assert.Equal(BankTransactionClaims.Debit(row), await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument("direction", "debit")).SingleAsync());
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, D.Operation)));
        var after = await payout.Source.ReadBalancesAsync(); Assert.Equal(before.Invoices[0]["balance"], after.Invoices[0]["balance"]);
        Assert.Equal(before.ReceiptBalance["allocated"], after.ReceiptBalance["allocated"]); Assert.Equal(before.ReceiptBalance["reservedForRefund"], after.ReceiptBalance["reservedForRefund"]);
        Assert.Equal(before.ReceiptBalance["version"].AsInt64 + 1, after.ReceiptBalance["version"].AsInt64);
        Assert.Equal(row["_id"], after.ReceiptBalance["lastEventId"]); Assert.Equal(row["snapshotHash"], after.ReceiptBalance["lastEventHash"]);
        var receipt = Assert.Single(row["receipts"].AsBsonArray); Assert.Equal(600m, receipt["paidAfter"].AsDecimal); Assert.Equal(0m, receipt["pending"].AsDecimal); Assert.Equal(2000m, receipt["available"].AsDecimal);
        Assert.Equal("recorded_unverified", (await data.Rows(RefundPayoutBson.Payouts).Find(new BsonDocument("_id", data.PayoutId)).SingleAsync())["state"]);
        var lease = await data.Rows("evidenceReadLeases").Find(new BsonDocument("actor.userId", data.Verifier.UserId)).SingleAsync();
        Assert.Equal("released", lease["state"]); Assert.Equal(payout.DraftId, lease["target"]["id"].AsInt64);
        Assert.Equal(permanent, await data.PermanentAsync());
    }
}
