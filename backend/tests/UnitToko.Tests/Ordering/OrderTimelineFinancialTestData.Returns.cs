using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Ordering;

internal sealed partial class OrderTimelineFinancialTestData
{
    private async Task PrepareReturnsAsync() {
        var entitlement = new BsonDocument { { "netRule", "tail_backout" }, { "componentRule", "manual_component_allocation" }, { "requiredIdentity", "matched" } };
        foreach (var field in new[] { "attributionProcedureReference", "taxTreatmentReference", "chargeTreatmentReference", "creditNoteProcedureReference", "operatingOwnerReference", "approvalReference" })
            entitlement[field] = "synthetic-approved-" + field;
        await PolicyAsync(ReturnEntitlementBson.Policies, entitlement);
        var book = await Rows("accountingBooks").Find(new BsonDocument()).SingleAsync();
        var chart = await Rows("accountingChartVersions").Find(new BsonDocument("_id", book["chartVersionId"])).SingleAsync();
        var liability = Assert.Single(chart["mappings"].AsBsonArray, row => row["mappingKey"] == "refund_liability");
        var financial = new BsonDocument { { "creditRule", "credit_original_components" }, { "reservationRule", "reserve_released_allocations" },
            { "independenceRule", "request_and_funding_makers" }, { "arMapping", "invoice_ar" }, { "refundLiabilityMapping", "refund_liability" },
            { "refundLiabilityAccountId", liability["accountId"] }, { "refundLiabilityAccountCode", liability["accountCode"] } };
        foreach (var field in new[] { "creditNoteProcedureReference", "taxTreatmentReference", "chargeTreatmentReference", "refundReservationProcedureReference", "operatingOwnerReference", "approvalReference" })
            financial[field] = "synthetic-approved-" + field;
        await PolicyAsync(ReturnFinancialApprovalBson.Policies, financial);
        Financial = new(Database, Unit, new(Database), Clock, Entitlements, Allocations);
        (CreditCaseId, CreditReturnId, CreditApprovalId, _) = await PrepareReturnAsync(Grouped.ReceiptA.Id.Value, "credit", "timeline-credit-return");
        (RefundCaseId, RefundReturnId, RefundApprovalId, refundAmount) = await PrepareReturnAsync(Grouped.ReceiptB.Id.Value, "refund", "timeline-refund-return");
        Assert.True(refundAmount > 0m);
    }

    private async Task<(long CaseId, long ReturnId, long ApprovalId, decimal Refund)> PrepareReturnAsync(long receiptId, string action, string key) {
        ReturnMaker = await Release.Dispatch.IssueExistingSessionAsync(ReturnMaker);
        Approver = await Release.Dispatch.IssueExistingSessionAsync(Approver);
        Buyer = await Release.Dispatch.IssueExistingSessionAsync(Buyer);
        var source = await Cases.SourceAsync(Buyer, Grouped.Invoice.Id.Value);
        var line = Assert.Single(source.Lines, item => item.ReceiptId.Value == receiptId);
        var requested = await Cases.RequestAsync(Buyer, new(Grouped.Invoice.Id.Value, source.Binding, "damaged", [new(receiptId, line.SkuId.Value, 1)]), Meta(key + "-case"));
        var caseId = requested["caseId"].AsInt64;
        var decision = await Cases.ReadDecisionAsync(ReturnMaker, caseId);
        await Cases.DecideAsync(ReturnMaker, caseId, 1, decision.Binding,
            new("authorize_return", "Pengembalian diterima untuk pemeriksaan", [new(receiptId, line.SkuId.Value, 1)]), Meta(key + "-decision"));
        var intake = await Cases.ReadReturnsAsync(ReturnMaker, caseId, true);
        var received = await Cases.ReceiveReturnAsync(ReturnMaker, caseId, 0, intake.Binding,
            new("Barang diterima dalam karantina", [new(receiptId, line.SkuId.Value, 1, "opened")]), Meta(key + "-intake"));
        var returnId = received["returnId"].AsInt64;
        var inspected = await Cases.ReadInspectionAsync(ReturnMaker, caseId, returnId, true);
        await Cases.InspectReturnAsync(ReturnMaker, caseId, returnId, 0, inspected.Binding,
            new("Pemeriksaan selesai", [new(receiptId, line.SkuId.Value, 1, "matched", "opened", "PRIVATE physical identity", "PRIVATE physical lot")]), Meta(key + "-inspect"));
        var resolution = await Cases.ReadResolutionAsync(ReturnMaker, caseId, returnId, action);
        await Cases.RequestResolutionAsync(ReturnMaker, caseId, returnId, 0, resolution.Binding, new(action, "Permintaan hasil keuangan retur"), Meta(key + "-resolution"));
        var input = new ReturnEntitlementInput([new(receiptId, line.SkuId.Value, new(0m), new(0m))]);
        var entitlement = await Entitlements.PreviewAsync(ReturnMaker, caseId, returnId, action, input);
        await Entitlements.RequestAsync(ReturnMaker, caseId, returnId, action, entitlement.View.Pool["version"].AsInt64,
            entitlement.Binding, input, Meta(key + "-entitlement"));
        Approver = await Release.Dispatch.IssueExistingSessionAsync(Approver);
        var balances = await Allocations.ReadAsync(Approver, VerificationId, [Grouped.Invoice.Id.Value]);
        var invoiceVersion = balances.Invoices[0]["balance"]["version"].AsInt64;
        var receiptVersion = balances.ReceiptBalance["version"].AsInt64;
        // Human-entered fixture release is bounded by the actually computed
        // inspected entitlement and remaining genuine allocation, never a default.
        var refund = action == "refund" ? decimal.Floor(Math.Min(entitlement.Result.Net.Value / 2m, (allocatedAmount - reversedAmount) / 4m) * 100m) / 100m : 0m;
        var approvalInput = new ReturnFinancialApprovalInput(invoiceVersion,
            action == "refund" ? [new(new(AllocationId), receiptVersion, new(refund))] : []);
        var approved = await Financial.PreviewAsync(Approver, caseId, returnId, action, approvalInput);
        var approval = await Financial.ApproveAsync(Approver, caseId, returnId, action, approved.Binding, approvalInput, Meta(key + "-approved"));
        return (caseId, returnId, approval["financialApprovalId"].AsInt64, refund);
    }

    private async Task PreparePayoutAsync() {
        PayoutMaker = await Release.Dispatch.IssueExistingSessionAsync(PayoutMaker);
        SettlementChecker = await Release.Dispatch.IssueExistingSessionAsync(SettlementChecker);
        var bank = Bank.Statements.Selection;
        var payoutPolicy = new BsonDocument { { "rule", RefundPayoutPolicy.ExactReservedRefund }, { "payingAccountId", bank.AccountId },
            { "payingAccountVersionId", bank.AccountVersionId } };
        foreach (var field in new[] { "beneficiaryVerificationProcedureReference", "payoutRecordingProcedureReference", "operatingOwnerReference", "approvalReference" })
            payoutPolicy[field] = "synthetic-test-only-" + field;
        await PolicyAsync(RefundPayoutBson.Policies, payoutPolicy);
        var people = await Unit.ExecuteAsync((tx, ct) => new PersonRegistryService(Database, Clock).ResolveProofsAsync(tx, [852008L, 852009L], ct));
        var details = new ReceivingAccountDetails("BENEFICIARY", "009876543210", "PRIVATE synthetic branch beneficiary", "IDR");
        var approval = await Rows(ReturnFinancialApprovalBson.Events).Find(new BsonDocument("_id", RefundApprovalId)).SingleAsync();
        var beneficiary = new BsonDocument { { "_id", (await new IdAllocator(Database).NextAsync(RefundPayoutBson.Beneficiaries)).Value },
            { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L }, { "scope", scope.DeepClone() }, { "buyingOrganizationId", approval["party"]["buyingOrganizationId"] },
            { "details", ReceivingAccountBson.Details(details) }, { "physicalIdentity", ReceivingAccountBson.PhysicalIdentity(details) },
            { "method", "authenticated-bank-record" }, { "proposedBy", 852008L }, { "verifiedBy", 852009L }, { "approvedBy", 852009L },
            { "people", new BsonArray(people.Evidence.OrderBy(person => person.UserId).Select(ReceivingAccountBson.Person)) },
            { "active", true }, { "approvedAt", Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt", Clock.UtcNow.AddDays(1).UtcDateTime } };
        foreach (var field in new[] { "authorizationReference", "trustedSourceReference", "trustedChannelReference", "operatingOwnerReference", "approvalReference" })
            beneficiary[field] = "synthetic-test-only-" + field;
        beneficiary["snapshotHash"] = RefundPayoutBson.Hash(beneficiary); await Rows(RefundPayoutBson.Beneficiaries).InsertOneAsync(beneficiary);
        var service = new RefundPayoutService(Database, Unit, new(Database), Clock, Financial, Bank.Service, Cases);
        var evidence = new RefundPayoutEvidenceService(Database, Unit, new(Database), Clock, service, Documents.Registration);
        var input = new RefundPayoutInput(beneficiary["_id"].AsInt64, 1, bank.AccountId, bank.AccountVersionId,
            new(refundAmount), AccountingCalendar.BusinessDate(Clock.UtcNow), "PRIVATE-SYNTHETIC-REFUND-TRANSFER");
        var preview = await service.PreviewDraftAsync(PayoutMaker, RefundApprovalId, input);
        var draft = await service.CreateDraftAsync(PayoutMaker, RefundApprovalId, preview.Binding, input, Meta("timeline-payout-draft"));
        var draftId = draft["draftId"].AsInt64;
        var reservation = await evidence.ReserveAsync(PayoutMaker, draftId, 1, Meta("timeline-payout-reserve"));
        using var image = new MemoryStream(Image);
        var uploaded = await evidence.UploadAsync(PayoutMaker, draftId, reservation.ReservationId, 1, image, Meta("timeline-payout-upload"));
        Assert.Equal("completed", await Documents.Runtime.RunOnceAsync()); Assert.Equal(Image, Documents.Scanner.Bytes);
        var recorded = await service.PreviewRecordAsync(PayoutMaker, draftId, 1, reservation.ReservationId, uploaded.Evidence!.Reference);
        PayoutId = (await evidence.RecordAsync(PayoutMaker, draftId, 1, reservation.ReservationId, uploaded.Evidence.Reference,
            recorded.Binding, Meta("timeline-payout-recorded")))["payoutId"].AsInt64;
        var actualPayout = await Rows(RefundPayoutBson.Payouts).Find(new BsonDocument("_id", PayoutId)).SingleAsync();
        var importer = await Release.Dispatch.IssueExistingSessionAsync(Bank.Statements.Maker);
        var debitBytes = BankBytes("TIMELINE-REFUND-DEBIT", refundAmount, credit: false);
        var debitPreview = await Bank.Statements.Service.PreviewAsync(importer, Bank.Statements.Selection, debitBytes);
        var imported = await Bank.Statements.Service.StageAsync(importer, Bank.Statements.Selection, debitBytes, "synthetic-refund-bank.csv",
            debitPreview.Binding, "synthetic-private-statement-reference", true, Meta("timeline-refund-bank-debit"));
        var settlementPolicy = new BsonDocument { { "rule", "independent_exact_bank_debit" } };
        foreach (var field in new[] { "bankDebitVerificationProcedureReference", "beneficiaryVerificationProcedureReference", "operatingOwnerReference", "approvalReference" })
            settlementPolicy[field] = "synthetic-test-only-" + field;
        Assert.Equal(scope, actualPayout["source"]["payingBank"]["scope"]);
        await PolicyAsync(RefundSettlementBson.Policies, settlementPolicy);
        var settlementInput = new RefundSettlementInput(imported["importId"].AsInt64, 2,
            "synthetic-independent-beneficiary-bank-record", "synthetic-independent-transfer-debit-record");
        SettlementChecker = await Release.Dispatch.IssueExistingSessionAsync(SettlementChecker);
        var settlement = await service.PreviewSettlementAsync(SettlementChecker, PayoutId, settlementInput);
        SettlementId = (await service.VerifySettlementAsync(SettlementChecker, PayoutId, settlementInput, settlement.Binding, true,
            Meta("timeline-refund-settled")))["settlementId"].AsInt64;
    }
}
