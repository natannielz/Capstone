using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using D = UnitToko.Tests.Billing.InvoiceReleaseTestData;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderTimelineFenceTests(MongoFixture fixture)
{
    [WindowsStorageFact(Skip = Pending.TransactionLatency)]
    public async Task Genuine_financial_capture_refuses_warmed_missing_bank_proofs_and_added_effects_in_nonempty_and_empty_memberships() {
        // One genuine119 financial producer graph is shared across the dependency
        // and membership cases. Deliberate corruption is never a positive source.
        await using var data = await OrderTimelineFinancialTestData.CreateAsync(fixture);
        var payment = await data.Payments.ReadTimelineOutcomeAsync(data.Buyer, data.Grouped.OrderA, data.DeclarationId);
        Assert.NotNull(payment.VerifiedAt);
        var refund = await data.Cases.ReadTimelineOutcomeAsync(data.Buyer, data.Grouped.OrderB, data.RefundCaseId);
        Assert.Contains(Assert.Single(refund.Returns).Events, item => item.Kind == "refund_settled");
        var credit = await data.Cases.ReadTimelineOutcomeAsync(data.Buyer, data.Grouped.OrderA, data.CreditCaseId);
        Assert.DoesNotContain(Assert.Single(credit.Returns).Events, item => item.Kind is "payout_recorded" or "refund_settled");
        var before = await data.BusinessAsync();

        var verified = await data.Rows(PaymentVerificationBson.Decisions).Find(new BsonDocument("_id", data.VerificationId)).SingleAsync();
        var receipt = await data.Rows(BankReceiptBson.Receipts).Find(new BsonDocument("_id", verified["receiptId"])).SingleAsync();
        var bankAnchor = await data.Rows(BankReceiptBson.Accounts).Find(new BsonDocument("physicalIdentity", receipt["physicalIdentity"])).SingleAsync();
        await RefuseDeleteAsync(BankReceiptBson.Accounts, bankAnchor, paymentRead: true, data.Grouped.OrderA, data.DeclarationId);

        var settlement = await data.Rows(RefundSettlementBson.Events).Find(new BsonDocument("_id", data.SettlementId)).SingleAsync();
        var settlementAudit = await data.Rows("accountingAuditEvents").Find(new BsonDocument("eventKey",
            "accounting:journal:" + settlement["journalId"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture))).SingleAsync();
        await RefuseDeleteAsync("accountingAuditEvents", settlementAudit, paymentRead: false, data.Grouped.OrderB, data.RefundCaseId);

        // Existing membership: preserve the genuine action and all its original
        // effects, then insert an additional matching audit after the capture.
        var review = await data.Rows(PaymentReviewActionRecords.Versions).Find(new BsonDocument("declarationId", data.DeclarationId)).SingleAsync();
        var extraReviewEffect = (await data.Rows("auditEvents").Find(new BsonDocument("_id", review["auditId"])).SingleAsync()).DeepClone().AsBsonDocument;
        extraReviewEffect["_id"] = (await new IdAllocator(data.Database).NextAsync("auditEvents")).Value;
        await RefuseInsertAsync(extraReviewEffect, paymentRead: true, data.Grouped.OrderA, data.DeclarationId);
        await RefuseInsertAsync(extraReviewEffect, paymentRead: true, data.Grouped.OrderA, data.DeclarationId, outsideSnapshot: true);

        // Empty membership: a credit approval has no payout owner or payout effects.
        // This schema-shaped negative row deliberately claims that approval without
        // an actual payout; the proof must not keep reporting the earlier empty set.
        var payout = await data.Rows(RefundPayoutBson.Payouts).Find(new BsonDocument("_id", data.PayoutId)).SingleAsync();
        var orphanPayoutEffect = (await data.Rows("auditEvents").Find(new BsonDocument("_id", payout["auditId"])).SingleAsync()).DeepClone().AsBsonDocument;
        orphanPayoutEffect["_id"] = (await new IdAllocator(data.Database).NextAsync("auditEvents")).Value;
        orphanPayoutEffect["result"]["approvalId"] = data.CreditApprovalId;
        Assert.Equal(0, await data.Rows(RefundPayoutBson.Payouts).CountDocumentsAsync(new BsonDocument("financialApprovalId", data.CreditApprovalId)));
        Assert.Equal(0, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument {
            { "kind", RefundPayoutBson.Operation }, { "result.approvalId", data.CreditApprovalId } }));
        await RefuseInsertAsync(orphanPayoutEffect, paymentRead: false, data.Grouped.OrderA, data.CreditCaseId);
        await RefuseInsertAsync(orphanPayoutEffect, paymentRead: false, data.Grouped.OrderA, data.CreditCaseId, outsideSnapshot: true);
        Assert.Equal(before, await data.BusinessAsync());

        async Task RefuseDeleteAsync(string collection, BsonDocument original, bool paymentRead, long orderId, long ownerId) {
            var changed = false;
            SetHook(paymentRead, async tx => {
                Assert.False(changed); changed = true;
                Assert.Equal(1, (await data.Rows(collection).DeleteOneAsync(tx, new BsonDocument("_id", original["_id"]))).DeletedCount);
            });
            try {
                await RefuseHistoryAsync(Read(paymentRead, orderId, ownerId)); Assert.True(changed);
                // Deletion happened in the exact caller transaction; denial must
                // roll it back, along with all source touches from this read.
                Assert.Equal(original, await data.Rows(collection).Find(new BsonDocument("_id", original["_id"])).SingleAsync());
                Assert.Equal(before, await data.BusinessAsync());
            } finally { SetHook(paymentRead, null); }
        }
        async Task RefuseInsertAsync(BsonDocument extra, bool paymentRead, long orderId, long ownerId, bool outsideSnapshot = false) {
            var changed = false;
            SetHook(paymentRead, async tx => {
                Assert.False(changed); changed = true;
                // Both cases matter: snapshot replay detects this caller's own
                // changed graph, while committed replay detects a new row which
                // the already-open Mongo snapshot cannot see.
                if (outsideSnapshot) await data.Rows("auditEvents").InsertOneAsync(extra);
                else await data.Rows("auditEvents").InsertOneAsync(tx, extra);
            });
            try {
                await RefuseHistoryAsync(Read(paymentRead, orderId, ownerId)); Assert.True(changed);
                Assert.Equal(outsideSnapshot ? 1 : 0, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("_id", extra["_id"])));
                Assert.Equal(before, await data.BusinessAsync());
            } finally {
                SetHook(paymentRead, null);
                if (outsideSnapshot && changed)
                    Assert.Equal(1, (await data.Rows("auditEvents").DeleteOneAsync(extra)).DeletedCount);
            }
            Assert.Equal(0, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("_id", extra["_id"])));
        }
        void SetHook(bool paymentRead, Func<IClientSessionHandle, Task>? hook) {
            if (paymentRead) data.Payments.TimelineBeforeFence = hook;
            else data.Cases.TimelineBeforeFence = hook;
        }
        Func<Task> Read(bool paymentRead, long orderId, long ownerId) => paymentRead
            ? async () => { _ = await data.Payments.ReadTimelineOutcomeAsync(data.Buyer, orderId, ownerId); }
            : async () => { _ = await data.Cases.ReadTimelineOutcomeAsync(data.Buyer, orderId, ownerId); };
    }

    [WindowsStorageFact]
    public async Task Genuine_invoice_read_expiring_after_history_fence_is_refused_while_payment_evidence_and_session_remain_current() {
        await using var data = await D.CreateAsync(fixture);
        await data.DecideAsync(await data.ProposeAsync());
        var preview = await data.Invoices.PreviewAsync(data.Finance, data.Selection);
        var invoice = await data.Invoices.IssueAsync(data.Finance, data.Selection, preview.Review, D.Meta("timeline-expiry-invoice"));
        var buyer = data.Dispatch.Buyer;
        await data.Dispatch.Base.AddGrantAsync(buyer, 853100, CatalogAuthoringTestData.Audience, [Permissions.PaymentEvidence]);
        // All original invoice grants remain structurally intact except for this
        // permission. The one replacement grant has an independently short expiry.
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", buyer.UserId),
            Builders<BsonDocument>.Update.Pull("permissions", Permissions.InvoiceRead));
        await data.Dispatch.Base.AddGrantAsync(buyer, 853101, CatalogAuthoringTestData.Audience, [Permissions.InvoiceRead]);
        var systemClock = data.Clock.UseSystemClock; var initial = data.Clock.UtcNow;
        data.Clock.UseSystemClock = false; data.Clock.UtcNow = initial;
        var expiresAt = initial.AddSeconds(1);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 853101L),
            Builders<BsonDocument>.Update.Set("expiresAt", expiresAt.UtcDateTime));
        buyer = await data.Dispatch.IssueExistingSessionAsync(buyer);
        var payments = new PaymentDraftService(data.Database, data.Unit, new(data.Database), data.Clock, data.Invoices);
        var saved = await payments.SaveAsync(buyer, null, 0, new PaymentDraftInput([invoice.Id], new(1m),
            AccountingCalendar.BusinessDate(initial), "Synthetic payer", null, null), D.Meta("timeline-expiry-draft"));
        var declarationId = saved["declarationId"].AsInt64;
        var authority = await data.Dispatch.Base.Store.ReadCurrentAsync(buyer);
        Assert.NotEmpty(ScopePolicy.Compile(buyer, authority, Permissions.InvoiceRead, initial).Clauses);
        Assert.NotEmpty(ScopePolicy.Compile(buyer, authority, Permissions.PaymentEvidence, initial).Clauses);
        Assert.Equal("draft", (await payments.ReadTimelineOutcomeAsync(buyer, data.Dispatch.OrderId, declarationId)).DeclarationState);
        var before = await data.ProtectedAsync(); var reached = false;
        payments.TimelineAfterHistoryFence = () => {
            Assert.False(reached); reached = true;
            data.Clock.UtcNow = expiresAt;
            Assert.Equal(expiresAt, data.Clock.UtcNow);
            Assert.Empty(ScopePolicy.Compile(buyer, authority, Permissions.InvoiceRead, data.Clock.UtcNow).Clauses);
            Assert.NotEmpty(ScopePolicy.Compile(buyer, authority, Permissions.PaymentEvidence, data.Clock.UtcNow).Clauses);
            return Task.CompletedTask;
        };
        try {
            var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(async () => {
                _ = await payments.ReadTimelineOutcomeAsync(buyer, data.Dispatch.OrderId, declarationId);
            });
            Assert.Equal("permission_denied", denied.Code);
            Assert.True(reached);
            // This ordinary declaration read needs PaymentEvidence, but must not
            // be used here: it also re-authenticates invoice sources. Inspect the
            // actual current authority/session independently instead.
            var live = await data.Dispatch.Base.Store.ReadCurrentAsync(buyer);
            Assert.NotEmpty(ScopePolicy.Compile(buyer, live, Permissions.PaymentEvidence, data.Clock.UtcNow).Clauses);
            Assert.Empty(ScopePolicy.Compile(buyer, live, Permissions.InvoiceRead, data.Clock.UtcNow).Clauses);
            Assert.Equal(before, await data.ProtectedAsync());
        } finally {
            payments.TimelineAfterHistoryFence = null;
            data.Clock.UtcNow = initial; data.Clock.UseSystemClock = systemClock;
        }
    }

    private static async Task RefuseHistoryAsync(Func<Task> read) {
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(read);
        Assert.Equal("order_timeline_history_unavailable", error.Code);
    }
}
