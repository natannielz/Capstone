using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Journeys;

/// <summary>What golden stage A2 (invoice release to allocated payment) needs beyond the order world: the two finance personas with
/// only their grants, the private evidence store behind the payment evidence service, and named fixture actions the browser calls
/// at the moment a step needs an external fact (the bank's credit for the issued invoice, the background validation and OCR runs).
/// Policies and reviewed external facts are synthetic inputs; no invoice, payment, cash or allocation is seeded.</summary>
[SupportedOSPlatform("windows")]
internal static class GoldenInvoiceWorld
{
    internal static async Task<GoldenJourneyBrowserTests.WorldExtension> PrepareAsync(GenuineFiniteDispatchTestData data, Dictionary<string, ActorContext> actors)
    {
        var documents = data.Stock.Valuation.Documents; var shop = CatalogAuthoringTestData.Shop;
        await data.Base.AddGrantAsync(data.Buyer, 719103, CatalogAuthoringTestData.Audience, [Permissions.InvoiceRead, Permissions.PaymentEvidence]);
        string[] release = [Permissions.InvoiceRead, Permissions.InvoiceReleaseRead];
        string[] bank = [Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead];
        // F-007 (owner decision 1 Nov): keuangan-tagihan allocates what pemeriksa-pembayaran verified; neither holds the other's side.
        // The bank reads: reading or allocating a verified payment checks its bank receipt (BankReceiptService.Required).
        actors["keuangan-tagihan"] = await data.Base.AddActorAsync(719120, shop, [.. release, Permissions.InvoiceReleasePropose, Permissions.InvoiceIssue,
            Permissions.PaymentReviewRead, Permissions.PaymentAllocate, .. bank]);
        actors["pemeriksa-pembayaran"] = await data.Base.AddActorAsync(719121, shop, [.. release, Permissions.InvoiceReleaseApprove, Permissions.PaymentReviewRead,
            Permissions.PaymentReviewCorrect, Permissions.PaymentVerify, .. bank]);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, actors["keuangan-tagihan"].UserId, actors["pemeriksa-pembayaran"].UserId);
        PaymentOcrProcessor? ocr = null;

        async Task<object?> Change(string action) {
            switch (action) {
                case "release-inputs": {
                    var date = AccountingCalendar.BusinessDate(data.Clock.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var minute = DateTimeOffset.FromUnixTimeSeconds(data.Clock.UtcNow.ToUnixTimeSeconds() / 60 * 60);
                    string Wib(DateTimeOffset value) => value.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                    return new { periodFrom = date, periodTo = date, validFromWib = Wib(minute.AddMinutes(-1)), validUntilWib = Wib(minute.AddHours(1)) };
                }
                case "payment-world": return await PaymentWorldAsync();
                case "validate-evidence": return new { state = await documents.Runtime.RunOnceAsync() };
                case "process-ocr": return new { state = await (ocr ?? throw new InvalidOperationException("Prepare the payment world first")).RunOnceAsync() };
                default: return null;
            }
        }

        // The bank's credit exists only once the invoice has a total: it is staged, observed and confirmed through the real bank owners.
        async Task<object> PaymentWorldAsync() {
            var invoice = await data.Rows(InvoiceBson.Invoices).Find(new BsonDocument()).SingleAsync();
            var total = Decimal128.ToDecimal(invoice["totals"]["total"].AsDecimal128); Assert.True(total > 0m);
            var date = AccountingCalendar.BusinessDate(data.Clock.UtcNow);
            var csv = Encoding.UTF8.GetString(BankStatementTestData.Original("GOLDEN-CREDIT", total.ToString("0.00", CultureInfo.InvariantCulture)))
                .Replace("2026-09-27", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var statements = await BankStatementTestData.AttachAsync(await ReceivingAccountTestData.AttachAsync(data.Catalog));
            var credit = await BankReceiptTestData.AttachAsync(statements, original: Encoding.UTF8.GetBytes(csv), preserveApprovedAccounting: true);
            var receiptId = (await credit.ConfirmAsync(await credit.PreviewAsync(), "golden-credit-confirmed", new("0000123456789", "GOLDEN-CREDIT", date, new(total))))["receiptId"].AsInt64;
            var scope = (await data.Rows(BankReceiptBson.Receipts).Find(new BsonDocument("_id", receiptId)).SingleAsync())["scope"];
            async Task Policy(string collection, BsonDocument fields) {
                var row = new BsonDocument { { "_id", (await new IdAllocator(data.Database).NextAsync(collection)).Value }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L },
                    { "scope", scope.DeepClone() }, { "active", true }, { "approvedAt", data.Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt", data.Clock.UtcNow.AddDays(1).UtcDateTime } };
                foreach (var element in fields) row.Add(element.Name, element.Value.DeepClone());
                await data.Rows(collection).InsertOneAsync(row);
            }
            await Policy(PaymentVerificationBson.Policies, new() { { "payerVerificationProcedureReference", "synthetic-independent-payer-procedure" },
                { "operatingOwnerReference", "synthetic-owner" }, { "approvalReference", "synthetic-verification-approval" } });
            await Policy(PaymentAllocationBson.Policies, new() { { "suspenseMapping", "unidentified_cash" }, { "arMapping", "invoice_ar" },
                { "procedureReference", "synthetic-invoice-allocation" }, { "operatingOwnerReference", "synthetic-owner" }, { "approvalReference", "synthetic-allocation-approval" } });
            ocr = new PaymentOcrProcessor(data.Database, data.Unit, new(data.Database), data.Clock, documents.Registration, PaymentOcrProcessorTests.Consumer, new PaymentOcrProcessorTests.FixtureEngine());
            await data.Rows("documentProcessingPolicies").InsertOneAsync(new BsonDocument {
                { "_id", (await new IdAllocator(data.Database).NextAsync("documentProcessingPolicies")).Value }, { "schemaVersion", 1 },
                { "consumerId", PaymentOcrProcessorTests.Consumer }, { "storeId", UnitToko.Tests.Procurement.ProcurementInspectionTestData.StoreId }, { "pipelineId", ocr.PipelineId },
                { "active", true }, { "revision", 1L }, { "fence", 0L }, { "approvedAt", data.Clock.UtcNow.AddMinutes(-1).UtcDateTime },
                { "expiresAt", data.Clock.UtcNow.AddHours(1).UtcDateTime }, { "hostAcceptanceRef", "synthetic-host-only" }, { "modelSelectionRef", "synthetic-model-only" } });
            return new { receiptId = receiptId.ToString(CultureInfo.InvariantCulture), total = total.ToString("0.00", CultureInfo.InvariantCulture),
                businessDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), account = "0000123456789", imageBase64 = Convert.ToBase64String(ImageDecoderTests.Png(13, 7)) };
        }

        return new(services => {
            // Payment images are stored in the same private evidence store the order world already registered.
            services.RemoveAll<PaymentEvidenceService>();
            services.AddSingleton(provider => new PaymentEvidenceService(data.Database, data.Unit, new(data.Database), data.Clock, provider.GetRequiredService<PaymentDraftService>(), documents.Registration));
        }, Change);
    }
}
