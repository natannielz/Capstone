using System.Runtime.Versioning;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Billing;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using D = UnitToko.Tests.Billing.InvoiceReleaseTestData;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderTimelineOutcomeTests(MongoFixture fixture)
{
    private static async Task<InvoiceView> IssueAsync(D data) {
        await data.DecideAsync(await data.ProposeAsync());
        var preview = await data.Invoices.PreviewAsync(data.Finance, data.Selection);
        return await data.Invoices.IssueAsync(data.Finance, data.Selection, preview.Review, D.Meta("timeline-issued"));
    }

    [WindowsStorageFact(Skip = Pending.TransactionLatency)]
    public async Task Genuine_invoice_draft_is_not_bank_verified_and_current_creator_order_and_final_grants_are_required() {
        await using var data = await D.CreateAsync(fixture);
        var invoice = await IssueAsync(data); var buyer = data.Dispatch.Buyer;
        await data.Dispatch.Base.AddGrantAsync(buyer, 851001, CatalogAuthoringTestData.Audience, [Permissions.PaymentEvidence]);
        buyer = await data.Dispatch.IssueExistingSessionAsync(buyer);
        var payments = new PaymentDraftService(data.Database, data.Unit, new(data.Database), data.Clock, data.Invoices);
        var input = new PaymentDraftInput([invoice.Id], new(1m), AccountingCalendar.BusinessDate(data.Clock.UtcNow),
            "Synthetic payer PRIVATE", "PRIVATE-bank-reference", "PRIVATE-declaration-note");
        var saved = await payments.SaveAsync(buyer, null, 0, input, D.Meta("timeline-draft"));
        var declarationId = saved["declarationId"].AsInt64;
        var original = await data.ProtectedAsync();
        var first = await payments.ReadTimelineOutcomeAsync(buyer, data.Dispatch.OrderId, declarationId);
        Assert.Equal(invoice.Id.Value, (await payments.ReadAsync(buyer, declarationId)).Invoices.Single().InvoiceId.Value);
        Assert.Equal("draft", first.DeclarationState); Assert.Equal("not_submitted", first.ReviewState);
        Assert.Equal("0", first.ReviewRevision); Assert.Null(first.SubmissionId); Assert.Null(first.ReviewId);
        Assert.Null(first.CurrentAssigneeAccountId); Assert.Null(first.ReviewActorAccountId); Assert.Null(first.VerificationActorAccountId);
        Assert.Null(first.VerificationId); Assert.Null(first.VerifiedAt); Assert.Empty(first.InvoiceEvents);
        var reloaded = await new PaymentDraftService(data.Database, data.Unit, new(data.Database), data.Clock, data.Invoices)
            .ReadTimelineOutcomeAsync(buyer, data.Dispatch.OrderId, declarationId);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(reloaded)); Assert.Equal(original, await data.ProtectedAsync());
        AssertPrivate(first);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => payments.ReadTimelineOutcomeAsync(data.Outsider, data.Dispatch.OrderId, declarationId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => payments.ReadTimelineOutcomeAsync(buyer, long.MaxValue, declarationId));
        var revoked = false;
        payments.TimelineBeforeFence = async _ => {
            if (revoked) return; revoked = true;
            await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", buyer.UserId),
                Builders<BsonDocument>.Update.Pull("permissions", Permissions.PaymentEvidence));
        };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => payments.ReadTimelineOutcomeAsync(buyer, data.Dispatch.OrderId, declarationId));
        Assert.True(revoked); Assert.Equal(original, await data.ProtectedAsync());
        Assert.Equal(1, await data.Rows(PaymentDraftBson.Heads).CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact(Skip = Pending.TransactionLatency)]
    public async Task Genuine_case_events_follow_received_and_inspected_goods_without_exposing_private_notes_or_implying_refund() {
        await using var data = await D.CreateAsync(fixture);
        var invoice = await IssueAsync(data); var buyer = data.Dispatch.Buyer; var staff = data.Finance;
        await data.Dispatch.Base.AddGrantAsync(buyer, 851011, CatalogAuthoringTestData.Audience, [Permissions.CaseRead, Permissions.CaseRequest]);
        await data.Dispatch.Base.AddGrantAsync(staff, 851012, CatalogAuthoringTestData.Shop,
            [Permissions.CaseRead, Permissions.CaseManage, Permissions.StockReceive, Permissions.StockCount]);
        buyer = await data.Dispatch.IssueExistingSessionAsync(buyer); staff = await data.Dispatch.IssueExistingSessionAsync(staff);
        var cases = new AfterSalesCaseService(data.Database, data.Unit, new(data.Database), data.Clock, data.Invoices);
        var source = await cases.SourceAsync(buyer, invoice.Id.Value); var line = Assert.Single(source.Lines);
        var requested = await cases.RequestAsync(buyer, new(invoice.Id.Value, source.Binding, "damaged", [new(line.ReceiptId.Value, line.SkuId.Value, 1)]), D.Meta("timeline-case"));
        var id = requested["caseId"].AsInt64;
        var pending = await cases.ReadTimelineOutcomeAsync(buyer, data.Dispatch.OrderId, id);
        Assert.Equal("requested", pending.CaseState); Assert.Null(pending.DecisionId); Assert.Null(pending.DecisionActorAccountId); Assert.Empty(pending.Returns);
        var decision = await cases.ReadDecisionAsync(staff, id);
        await cases.DecideAsync(staff, id, 1, decision.Binding, new("authorize_return", "Kembalikan untuk pemeriksaan",
            [new(line.ReceiptId.Value, line.SkuId.Value, 1)]), D.Meta("timeline-decision"));
        var intake = await cases.ReadReturnsAsync(staff, id, true);
        var received = await cases.ReceiveReturnAsync(staff, id, 0, intake.Binding,
            new("Diterima dalam karantina", [new(line.ReceiptId.Value, line.SkuId.Value, 1, "opened")]), D.Meta("timeline-intake"));
        var returnId = received["returnId"].AsInt64;
        var inspection = await cases.ReadInspectionAsync(staff, id, returnId, true);
        await cases.InspectReturnAsync(staff, id, returnId, 0, inspection.Binding, new("Barang telah diperiksa",
            [new(line.ReceiptId.Value, line.SkuId.Value, 1, "matched", "opened", "PRIVATE identity inspection", "PRIVATE lot label")]),
            D.Meta("timeline-inspection") with { Reason = "PRIVATE inspector rationale" });
        var resolution = await cases.ReadResolutionAsync(staff, id, returnId, "replacement");
        await cases.RequestResolutionAsync(staff, id, returnId, 0, resolution.Binding, new("replacement", "Ajukan penggantian"), D.Meta("timeline-replacement"));
        var before = await data.ProtectedAsync();
        var result = await cases.ReadTimelineOutcomeAsync(buyer, data.Dispatch.OrderId, id);
        Assert.Equal("return_authorized", result.CaseState); Assert.Equal("Kembalikan untuk pemeriksaan", result.PublicExplanation);
        Assert.Equal(staff.UserId, result.DecisionActorAccountId!.Value.Value);
        var returned = Assert.Single(result.Returns); Assert.Equal(returnId, returned.ReturnId.Value);
        Assert.Equal(new[] { "received", "inspected", "replacement_requested" }.Order(), returned.Events.Select(item => item.Kind).Order());
        Assert.All(returned.Events, item => Assert.Equal(returnId, item.EventId.Value));
        Assert.All(returned.Events, item => Assert.Equal(staff.UserId, item.ActorAccountId.Value));
        Assert.DoesNotContain(returned.Events, item => item.Kind is "credit_approved" or "refund_approved" or "refund_settled" or "payout_recorded");
        AssertPrivate(result); Assert.Equal(before, await data.ProtectedAsync());
        var reload = await new AfterSalesCaseService(data.Database, data.Unit, new(data.Database), data.Clock, data.Invoices)
            .ReadTimelineOutcomeAsync(buyer, data.Dispatch.OrderId, id);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(reload));
        await RefusedAsync(() => cases.ReadTimelineOutcomeAsync(data.Outsider, data.Dispatch.OrderId, id));
        await RefusedAsync(() => cases.ReadTimelineOutcomeAsync(buyer, long.MaxValue, id));
        var original = await data.Rows(ReturnInspectionRecords.Collection).Find(new BsonDocument("_id", returnId)).SingleAsync();
        await data.Rows(ReturnInspectionRecords.Collection).DeleteOneAsync(new BsonDocument("_id", returnId));
        await RefusedAsync(() => cases.ReadTimelineOutcomeAsync(buyer, data.Dispatch.OrderId, id));
        await data.Rows(ReturnInspectionRecords.Collection).InsertOneAsync(original);
        var once = false;
        cases.TimelineBeforeFence = async _ => {
            if (once) return; once = true;
            await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", buyer.UserId), Builders<BsonDocument>.Update.Pull("permissions", Permissions.CaseRead));
        };
        await RefusedAsync(() => cases.ReadTimelineOutcomeAsync(buyer, data.Dispatch.OrderId, id));
        Assert.True(once); Assert.Equal(before, await data.ProtectedAsync());
    }

    private static async Task RefusedAsync(Func<Task> read) {
        var error = await Record.ExceptionAsync(read);
        Assert.True(error is AuthorizationDeniedException or PersistenceConflictException,
            "Expected typed owner/fence refusal, received: " + (error?.ToString() ?? "successful outcome"));
    }
    private static void AssertPrivate(object result) {
        var json = JsonSerializer.Serialize(result);
        foreach (var secret in new[] { "PRIVATE", "payerName", "bankReference", "accountNumber", "evidence", "resolutions", "people", "actorId", "assigneeId", "observedLotLabel", "observedIdentity" })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsStorageFact(Skip = Pending.TransactionLatency)]
    public async Task Genuine_grouped_invoice_financial_events_are_verified_private_scoped_and_do_not_claim_whole_order_settlement() {
        await using var data = await OrderTimelineFinancialTestData.CreateAsync(fixture);
        var grouped = data.Grouped; var before = await data.BusinessAsync();
        var payment = await data.Payments.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderA, data.DeclarationId);
        Assert.Equal("submitted", payment.DeclarationState); Assert.Equal("assigned", payment.ReviewState);
        Assert.Equal("1", payment.ReviewRevision); Assert.NotNull(payment.ReviewId); Assert.NotNull(payment.ReviewRecordedAt);
        Assert.Equal(data.Reviewer.UserId, payment.CurrentAssigneeAccountId!.Value.Value);
        Assert.Equal(data.Reviewer.UserId, payment.ReviewActorAccountId!.Value.Value);
        Assert.Equal(data.Reviewer.UserId, payment.VerificationActorAccountId!.Value.Value);
        Assert.Equal(data.VerificationId, payment.VerificationId!.Value.Value); Assert.NotNull(payment.VerifiedAt);
        Assert.Equal(2, payment.InvoiceEvents.Length);
        Assert.All(payment.InvoiceEvents, item => Assert.Equal(grouped.Invoice.Id, item.InvoiceId));
        var allocation = Assert.Single(payment.InvoiceEvents, item => item.Kind == "allocation");
        var reversal = Assert.Single(payment.InvoiceEvents, item => item.Kind == "reversal");
        Assert.Equal(data.AllocationId, allocation.EventId.Value); Assert.Null(allocation.OriginalAllocationId);
        Assert.Equal(data.CorrectionId, reversal.EventId.Value); Assert.Equal(data.AllocationId, reversal.OriginalAllocationId!.Value.Value);
        Assert.Equal(data.Allocator.UserId, allocation.ActorAccountId.Value); Assert.Equal(data.Reverser.UserId, reversal.ActorAccountId.Value);
        var forB = await data.Payments.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderB, data.DeclarationId);
        Assert.Equal(JsonSerializer.Serialize(payment with { OrderId = new(grouped.OrderB) }), JsonSerializer.Serialize(forB));
        // The same verified invoice event may concern both constituent orders;
        // this projection intentionally exposes no allocated order amount/balance.
        var json = JsonSerializer.Serialize(payment);
        foreach (var field in new[] { "Remaining", "Balance", "Paid", "Settled", "Amount" })
            Assert.DoesNotContain("\"" + field + "\"", json, StringComparison.OrdinalIgnoreCase);
        AssertPrivate(payment);

        var credit = await data.Cases.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderA, data.CreditCaseId);
        var creditReturn = Assert.Single(credit.Returns);
        Assert.Equal(data.CreditReturnId, creditReturn.ReturnId.Value);
        Assert.Equal(new[] { "received", "inspected", "credit_requested", "entitlement_requested", "credit_approved" }.Order(),
            creditReturn.Events.Select(item => item.Kind).Order());
        Assert.Equal(data.CreditApprovalId, Assert.Single(creditReturn.Events, item => item.Kind == "credit_approved").EventId.Value);
        Assert.Equal(data.ReturnMaker.UserId, credit.DecisionActorAccountId!.Value.Value);
        Assert.Equal(data.Approver.UserId, Assert.Single(creditReturn.Events, item => item.Kind == "credit_approved").ActorAccountId.Value);
        var refund = await data.Cases.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderB, data.RefundCaseId);
        var refundReturn = Assert.Single(refund.Returns);
        Assert.Equal(data.RefundReturnId, refundReturn.ReturnId.Value);
        Assert.Equal(new[] { "received", "inspected", "refund_requested", "entitlement_requested", "refund_approved", "payout_recorded", "refund_settled" }.Order(),
            refundReturn.Events.Select(item => item.Kind).Order());
        Assert.Equal(data.RefundApprovalId, Assert.Single(refundReturn.Events, item => item.Kind == "refund_approved").EventId.Value);
        Assert.Equal(data.PayoutId, Assert.Single(refundReturn.Events, item => item.Kind == "payout_recorded").EventId.Value);
        Assert.Equal(data.SettlementId, Assert.Single(refundReturn.Events, item => item.Kind == "refund_settled").EventId.Value);
        Assert.Equal(data.PayoutMaker.UserId, Assert.Single(refundReturn.Events, item => item.Kind == "payout_recorded").ActorAccountId.Value);
        Assert.Equal(data.SettlementChecker.UserId, Assert.Single(refundReturn.Events, item => item.Kind == "refund_settled").ActorAccountId.Value);
        AssertPrivate(credit); AssertPrivate(refund);
        // Sharing an invoice does not make a case concerning receiptA belong to B.
        await RefusedAsync(() => data.Cases.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderB, data.CreditCaseId));
        await RefusedAsync(() => data.Cases.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderA, data.RefundCaseId));
        await RefusedAsync(() => data.Payments.ReadTimelineOutcomeAsync(data.Release.Outsider, grouped.OrderA, data.DeclarationId));
        await RefusedAsync(() => data.Cases.ReadTimelineOutcomeAsync(data.Release.Outsider, grouped.OrderB, data.RefundCaseId));

        var payments = new PaymentDraftService(data.Database, data.Unit, new(data.Database), data.Clock, data.Release.Invoices);
        var cases = new AfterSalesCaseService(data.Database, data.Unit, new(data.Database), data.Clock, data.Release.Invoices);
        Assert.Equal(JsonSerializer.Serialize(payment), JsonSerializer.Serialize(await payments.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderA, data.DeclarationId)));
        Assert.Equal(JsonSerializer.Serialize(refund), JsonSerializer.Serialize(await cases.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderB, data.RefundCaseId)));
        Assert.Equal(before, await data.BusinessAsync());

        // Removing real retained evidence cannot downgrade a committed outcome to
        // pending or quietly omit an event. Only this test restores its own row.
        async Task RemovedAsync(string collection, BsonDocument filter, Func<Task> read) {
            var row = await data.Rows(collection).Find(filter).SingleAsync();
            await data.Rows(collection).DeleteOneAsync(new BsonDocument("_id", row["_id"]));
            try { await RefusedAsync(read); }
            finally { await data.Rows(collection).InsertOneAsync(row); }
        }
        await RemovedAsync("paymentReviewActions", new("declarationId", data.DeclarationId),
            async () => { _ = await payments.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderA, data.DeclarationId); });
        await RemovedAsync("evidencePins", new() { { "associationKind", "PaymentSubmission" }, { "declarationId", data.DeclarationId } },
            async () => { _ = await payments.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderA, data.DeclarationId); });
        await RemovedAsync("documentExtractionResults", new("binding.declarationId", data.DeclarationId),
            async () => { _ = await payments.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderA, data.DeclarationId); });
        await RemovedAsync(PaymentAllocationCorrectionBson.Events, new("_id", data.CorrectionId),
            async () => { _ = await payments.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderA, data.DeclarationId); });
        await RemovedAsync(ReturnFinancialApprovalBson.Events, new("_id", data.RefundApprovalId),
            async () => { _ = await cases.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderB, data.RefundCaseId); });
        await RemovedAsync(RefundSettlementBson.Events, new("_id", data.SettlementId),
            async () => { _ = await cases.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderB, data.RefundCaseId); });
        await RemovedAsync("bankTransactionClaims", new("direction", "debit"),
            async () => { _ = await cases.ReadTimelineOutcomeAsync(data.Buyer, grouped.OrderB, data.RefundCaseId); });
        Assert.Equal(before, await data.BusinessAsync());
        Assert.False(await data.Rows("authorizationGrants").Find(new BsonDocument { { "userId", data.Buyer.UserId },
            { "permissions", new BsonDocument("$in", new BsonArray { Permissions.PaymentReviewRead, Permissions.BankReceiptRead, Permissions.DisbursementVerify }) } }).AnyAsync());
    }
}
