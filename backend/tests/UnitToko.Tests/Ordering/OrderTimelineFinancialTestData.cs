using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Billing;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Procurement;

namespace UnitToko.Tests.Ordering;

/// <summary>Actual119 grouped invoice followed by genuine bank, evidence, payment,
/// return and settlement owners. Policies and reviewed external facts are explicit
/// synthetic inputs. No invoice admission, cash, balance, journal or outcome is seeded.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class OrderTimelineFinancialTestData(OrderTimelineGroupedTestData grouped) : IAsyncDisposable
{
    internal OrderTimelineGroupedTestData Grouped => grouped;
    internal InvoiceReleaseTestData Release => grouped.Release;
    internal IMongoDatabase Database => Release.Database;
    internal MongoUnitOfWork Unit => Release.Unit;
    internal UnitToko.Tests.Authorization.MutableAuthorizationClock Clock => Release.Clock;
    internal CatalogAuthoringTestData Catalog => Release.Dispatch.Catalog;
    internal ActorContext Buyer = null!, Reviewer = null!, Allocator = null!, Reverser = null!, ReturnMaker = null!, Approver = null!, PayoutMaker = null!, SettlementChecker = null!;
    internal PaymentDraftService Payments => Grouped.Payments;
    internal AfterSalesCaseService Cases => Grouped.Cases;
    internal BankReceiptTestData Bank = null!;
    internal PaymentEvidenceService Evidence = null!;
    internal PaymentVerificationService Verifications = null!;
    internal PaymentAllocationService Allocations = null!;
    internal ReturnEntitlementService Entitlements = null!;
    internal ReturnFinancialApprovalService Financial = null!;
    internal long DeclarationId, VerificationId, AllocationId, CorrectionId;
    internal long CreditCaseId, CreditReturnId, CreditApprovalId, RefundCaseId, RefundReturnId, RefundApprovalId, PayoutId, SettlementId;
    private long receiptId;
    private decimal allocatedAmount, reversedAmount, refundAmount;
    private BsonDocument scope = null!;
    private SupplierDocumentTestData Documents => Release.Dispatch.Stock.Valuation.Documents;
    private static byte[] Image => ImageDecoderTests.Png(13, 7);
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Explicit synthetic independent financial source review", "timeline-financial-source");

    internal static async Task<OrderTimelineFinancialTestData> CreateAsync(MongoFixture fixture, bool systemClock = false) {
        var data = new OrderTimelineFinancialTestData(await OrderTimelineGroupedTestData.CreateAsync(fixture, systemClock, includeCustomerCashMappings: true));
        try {
            await data.PrepareActorsAsync();
            await data.PreparePaymentAsync();
            await data.PrepareReturnsAsync();
            await data.PreparePayoutAsync();
            // Browser consumers receive a fresh real session; no finance grant is
            // borrowed for buyer outcomes and no fixture bank data is exposed.
            data.Buyer = await data.Release.Dispatch.IssueExistingSessionAsync(data.Buyer);
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }

    private async Task PrepareActorsAsync() {
        await Catalog.Base.AddGrantAsync(Release.Dispatch.Buyer, 852000, CatalogAuthoringTestData.Audience,
            [Permissions.OrderRead, Permissions.ShipmentRead, Permissions.InvoiceRead, Permissions.PaymentEvidence, Permissions.CaseRead, Permissions.CaseRequest]);
        Buyer = await Release.Dispatch.IssueExistingSessionAsync(Release.Dispatch.Buyer);
        string[] financeRead = [Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.CaseRead, Permissions.BankReceiptRead,
            Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead];
        Reviewer = await Catalog.ActorAsync(852001, CatalogAuthoringTestData.Shop, [.. financeRead, Permissions.PaymentVerify, Permissions.PaymentReviewCorrect, Permissions.PaymentReviewManage]);
        Allocator = await Catalog.ActorAsync(852002, CatalogAuthoringTestData.Shop, [.. financeRead, Permissions.PaymentAllocate]);
        Reverser = await Catalog.ActorAsync(852003, CatalogAuthoringTestData.Shop, [.. financeRead, Permissions.PaymentAllocate, Permissions.JournalReverse]);
        ReturnMaker = await Catalog.ActorAsync(852004, CatalogAuthoringTestData.Shop,
            Permissions.CaseRead, Permissions.InvoiceRead, Permissions.CaseManage, Permissions.StockReceive, Permissions.StockCount, Permissions.CreditRequest, Permissions.RefundRequest);
        Approver = await Catalog.ActorAsync(852005, CatalogAuthoringTestData.Shop, [.. financeRead, Permissions.CreditApprove, Permissions.RefundApprove]);
        PayoutMaker = await Catalog.ActorAsync(852006, CatalogAuthoringTestData.Shop, [.. financeRead, Permissions.DisbursementRecord]);
        SettlementChecker = await Catalog.ActorAsync(852007, CatalogAuthoringTestData.Shop, [.. financeRead, Permissions.DisbursementVerify]);
        await PersonTestData.SeedDistinctPeopleAsync(Database, Clock, Reviewer.UserId, Allocator.UserId, Reverser.UserId,
            ReturnMaker.UserId, Approver.UserId, PayoutMaker.UserId, SettlementChecker.UserId, 852008, 852009);
        Assert.False(await Rows("authorizationGrants").Find(new BsonDocument { { "userId", new BsonDocument("$in", new BsonArray {
            Reviewer.UserId, Allocator.UserId, Reverser.UserId, ReturnMaker.UserId, Approver.UserId, PayoutMaker.UserId, SettlementChecker.UserId }) },
            { "permissions", Permissions.OrderSubmit } }).AnyAsync());
        Entitlements = new(Database, Unit, new(Database), Clock, Cases, Release.Invoices);
    }

    private async Task PreparePaymentAsync() {
        var accounts = await ReceivingAccountTestData.AttachAsync(Catalog);
        var statements = await BankStatementTestData.AttachAsync(accounts);
        var total = Grouped.Invoice.Detail.Total.Value; Assert.True(total > 100m);
        var bankBytes = BankBytes("TIMELINE-CREDIT", total, credit: true);
        Bank = await BankReceiptTestData.AttachAsync(statements, original: bankBytes, preserveApprovedAccounting: true);
        receiptId = (await Bank.ConfirmAsync(await Bank.PreviewAsync(), "timeline-credit-confirmed",
            new("0000123456789", "TIMELINE-CREDIT", AccountingCalendar.BusinessDate(Clock.UtcNow), new(total))))["receiptId"].AsInt64;
        var receipt = await Rows(BankReceiptBson.Receipts).Find(new BsonDocument("_id", receiptId)).SingleAsync();
        scope = receipt["scope"].DeepClone().AsBsonDocument;
        Buyer = await Release.Dispatch.IssueExistingSessionAsync(Buyer);
        var draft = await Payments.SaveAsync(Buyer, null, 0, new([Grouped.Invoice.Id], new(total), AccountingCalendar.BusinessDate(Clock.UtcNow),
            "PRIVATE synthetic payer", "PRIVATE source bank reference", "PRIVATE source declaration"), Meta("timeline-payment-draft"));
        DeclarationId = draft["declarationId"].AsInt64;
        Evidence = new(Database, Unit, new(Database), Clock, Payments, Documents.Registration);
        var reservation = await Evidence.ReserveAsync(Buyer, DeclarationId, 1, Meta("timeline-payment-reserve"));
        using var image = new MemoryStream(Image);
        var uploaded = await Evidence.UploadAsync(Buyer, DeclarationId, reservation.ReservationId, 1, image, Meta("timeline-payment-upload"));
        var reference = uploaded.Evidence!.Reference;
        Assert.Equal("completed", await Documents.Runtime.RunOnceAsync());
        Assert.Equal(Image, Documents.Scanner.Bytes);
        await Evidence.SubmitAsync(Buyer, DeclarationId, 1, reservation.ReservationId, reference, Meta("timeline-payment-submit"));
        var processor = new PaymentOcrProcessor(Database, Unit, new(Database), Clock, Documents.Registration,
            PaymentOcrProcessorTests.Consumer, new PaymentOcrProcessorTests.FixtureEngine());
        await Rows("documentProcessingPolicies").InsertOneAsync(new BsonDocument {
            { "_id", (await new IdAllocator(Database).NextAsync("documentProcessingPolicies")).Value }, { "schemaVersion", 1 },
            { "consumerId", PaymentOcrProcessorTests.Consumer }, { "storeId", ProcurementInspectionTestData.StoreId }, { "pipelineId", processor.PipelineId },
            { "active", true }, { "revision", 1L }, { "fence", 0L }, { "approvedAt", Clock.UtcNow.AddMinutes(-1).UtcDateTime },
            { "expiresAt", Clock.UtcNow.AddHours(1).UtcDateTime }, { "hostAcceptanceRef", "synthetic-host-only" }, { "modelSelectionRef", "synthetic-model-only" } });
        Assert.Equal("completed", await processor.RunOnceAsync());
        Reviewer = await Release.Dispatch.IssueExistingSessionAsync(Reviewer);
        var extraction = await Evidence.ReadReviewExtractionAsync(Reviewer, DeclarationId, 1, reference, new("payment-ocr-view", "timeline-private-review"));
        var review = await Evidence.ReadCorrectionsAsync(Reviewer, DeclarationId, new(1, extraction.SubmissionId, reference, extraction.ResultId!.Value),
            new("payment-ocr-view", "timeline-private-correction"));
        var fields = review.Fields.Select(field => field.Field switch {
            "amount" => field with { Value = total.ToString("0.00", CultureInfo.InvariantCulture) },
            "currency" => field with { Value = "IDR" }, "account" => field with { Value = "0000123456789" }, _ => field }).ToImmutableArray();
        await Evidence.CorrectReviewAsync(Reviewer, DeclarationId, 0, review.Binding, fields, Meta("timeline-payment-corrected"));
        await PolicyAsync(PaymentVerificationBson.Policies, new() {
            { "payerVerificationProcedureReference", "synthetic-independent-payer-procedure" }, { "operatingOwnerReference", "synthetic-owner" },
            { "approvalReference", "synthetic-verification-approval" } });
        Verifications = new(Database, Unit, new(Database), Clock, Payments, Evidence, Bank.Service);
        var assignment = await Verifications.ReadReviewActionsAsync(Reviewer, DeclarationId);
        await Verifications.RecordReviewActionAsync(Reviewer, DeclarationId, assignment.ReviewRevision, new(assignment.Binding, "claim"), Meta("timeline-review-claimed"));
        var verification = await Verifications.PreviewAsync(Reviewer, DeclarationId, receiptId);
        VerificationId = (await Verifications.VerifyAsync(Reviewer, DeclarationId, receiptId,
            PaymentVerificationTestData.Input(verification), Meta("timeline-payment-verified")))["verificationId"].AsInt64;
        var allocationPolicy = await PolicyAsync(PaymentAllocationBson.Policies, new() {
            { "suspenseMapping", "unidentified_cash" }, { "arMapping", "invoice_ar" }, { "procedureReference", "synthetic-invoice-allocation" },
            { "operatingOwnerReference", "synthetic-owner" }, { "approvalReference", "synthetic-allocation-approval" } });
        var correctionPolicy = allocationPolicy.DeepClone().AsBsonDocument;
        correctionPolicy["_id"] = (await new IdAllocator(Database).NextAsync(PaymentAllocationCorrectionBson.Policies)).Value;
        correctionPolicy["correctionProcedureReference"] = "synthetic-independent-linked-reversal";
        await Rows(PaymentAllocationCorrectionBson.Policies).InsertOneAsync(correctionPolicy);
        Allocations = new(Database, Unit, new(Database), Clock, Verifications, Release.Invoices, Entitlements);
        Allocator = await Release.Dispatch.IssueExistingSessionAsync(Allocator);
        allocatedAmount = decimal.Floor(total * 25m) / 100m;
        reversedAmount = decimal.Floor(allocatedAmount * 10m) / 100m;
        var allocationInput = new PaymentAllocationInput(0, [new(Grouped.Invoice.Id, 0, new(allocatedAmount))]);
        var allocation = await Allocations.PreviewAsync(Allocator, VerificationId, allocationInput);
        AllocationId = (await Allocations.AllocateAsync(Allocator, VerificationId, allocation.Binding, allocationInput, Meta("timeline-allocation")))["allocationId"].AsInt64;
        Reverser = await Release.Dispatch.IssueExistingSessionAsync(Reverser);
        var correctionInput = new PaymentAllocationInput(1, [new(Grouped.Invoice.Id, 1, new(reversedAmount))]);
        var correction = await Allocations.PreviewCorrectionAsync(Reverser, AllocationId, correctionInput);
        CorrectionId = (await Allocations.CorrectAsync(Reverser, AllocationId, correction.Binding, correctionInput, Meta("timeline-linked-reversal")))["correctionId"].AsInt64;
    }

    private async Task<BsonDocument> PolicyAsync(string collection, BsonDocument fields) {
        var row = new BsonDocument { { "_id", (await new IdAllocator(Database).NextAsync(collection)).Value }, { "schemaVersion", 1 },
            { "revision", 1L }, { "fence", 0L }, { "scope", scope.DeepClone() }, { "active", true },
            { "approvedAt", Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt", Clock.UtcNow.AddDays(1).UtcDateTime } };
        foreach (var element in fields) row.Add(element.Name, element.Value.DeepClone());
        await Rows(collection).InsertOneAsync(row); return row;
    }
    private byte[] BankBytes(string id, decimal amount, bool credit) {
        var text = Encoding.UTF8.GetString(BankStatementTestData.Original(id, amount.ToString("0.00", CultureInfo.InvariantCulture)))
            .Replace("2026-09-27", AccountingCalendar.BusinessDate(Clock.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (!credit) text = text.Replace(",C,", ",D,", StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(text);
    }
    internal async Task<BsonDocument> BusinessAsync() {
        var result = await Grouped.BusinessAsync();
        foreach (var name in new[] { PaymentAllocationBson.Invoices, PaymentAllocationBson.Receipts, PaymentAllocationBson.Events,
            PaymentAllocationCorrectionBson.Events, PaymentVerificationBson.Decisions, ReturnEntitlementBson.Requests, ReturnEntitlementBson.Pools,
            ReturnFinancialApprovalBson.Events, RefundPayoutBson.Payouts, RefundSettlementBson.Events, "bankTransactionClaims" }.Distinct()) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) { row.Remove("fence"); if (name == "invoices") row.Remove("postingFence"); }
            result[name] = new BsonArray(rows);
        }
        foreach (var row in result[InvoiceBson.Invoices].AsBsonArray) row.AsBsonDocument.Remove("postingFence");
        return result;
    }
    public ValueTask DisposeAsync() => Grouped.DisposeAsync();
}
