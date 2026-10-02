using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class RefundPayoutSourceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Actual_refund_and_paying_bank_sources_use_read_grants_and_preserve_cash_journals_and_money_heads() {
        await using var data = await ApprovedAsync(); var catalog = data.Allocation.Source.Read.Catalog;
        var actor = data.Allocation.Source.Reader; var saved = await data.Rows(ReturnFinancialApprovalBson.Events).Find(new BsonDocument()).SingleAsync();
        var heads = await MoneyHeadsAsync(data); var journals = new BsonArray(await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var cash = await data.CashAsync(); var bank = data.Allocation.Source.Bank; var selection = bank.Statements.Selection;
        var actual = await new AuthorizationStore(data.Database, new(catalog.Client), data.Clock).ReadAsync(actor, async (authority, tx, ct) => {
            var refund = await data.Service.PayoutSourceAsync(actor, authority, tx, saved["_id"].AsInt64, ct);
            var paying = await bank.Service.PayoutBankSourceAsync(actor, authority, tx, selection.AccountId, selection.AccountVersionId, ct);
            await refund.FenceAsync(ct); await paying.FenceAsync(ct); refund.RequireCurrent(); paying.RequireCurrent();
            Assert.Equal(saved["actorId"].AsInt64, refund.ApprovedBy.UserId); Assert.NotNull(refund.ApprovedBy.PersonId);
            Assert.Equal(600m, refund.Approval["reservedRefund"].AsDecimal); Assert.Equal(807L, refund.LiabilityAccount["accountId"].AsInt64);
            Assert.Equal("A2103", refund.LiabilityAccount["accountCode"].AsString); Assert.Equal(saved["snapshotHash"], refund.Approval["snapshotHash"]);
            Assert.Equal(805L, paying.CashAccount["accountId"].AsInt64); Assert.Equal("A1102", paying.CashAccount["accountCode"].AsString);
            Assert.Equal(paying.Account["physicalIdentity"], paying.Anchor["physicalIdentity"]); Assert.Equal(paying.FirstReceipt["journalId"], paying.CashJournal["_id"]);
            Assert.Equal(paying.Account["scope"], paying.Scope); Assert.Equal(paying.Anchor["identityNamespace"].AsString, paying.IdentityNamespace);
            Assert.DoesNotContain(authority.Grants.SelectMany(grant => grant.Permissions), permission => permission is Permissions.CreditApprove or Permissions.RefundApprove or Permissions.BankReceiptConfirm);
            return refund.Approval["_id"].AsInt64;
        }, default);
        Assert.Equal(saved["_id"].AsInt64, actual); Assert.Equal(heads, await MoneyHeadsAsync(data)); Assert.Equal(cash, await data.CashAsync());
        Assert.Equal(journals, new BsonArray(await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        foreach (var name in new[] { "refundPayoutDrafts", "refundPayouts" }) Assert.Equal(0, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Removing_current_reader_grant_after_source_capture_denies_the_final_refund_fence() {
        await using var data = await ApprovedAsync(); var catalog = data.Allocation.Source.Read.Catalog; var actor = data.Allocation.Source.Reader;
        var approval = await data.Rows(ReturnFinancialApprovalBson.Events).Find(new BsonDocument()).SingleAsync(); var heads = await MoneyHeadsAsync(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new AuthorizationStore(data.Database, new(catalog.Client), data.Clock).ReadAsync(actor, async (authority, tx, ct) => {
            var source = await data.Service.PayoutSourceAsync(actor, authority, tx, approval["_id"].AsInt64, ct);
            var changed = await data.Rows("authorizationGrants").UpdateManyAsync(tx, new BsonDocument("userId", actor.UserId), Builders<BsonDocument>.Update.Pull("permissions", Permissions.CaseRead), cancellationToken: ct);
            Assert.True(changed.ModifiedCount > 0); await source.FenceAsync(ct); return true;
        }, default));
        Assert.Equal(heads, await MoneyHeadsAsync(data)); Assert.Equal(0, await data.Rows("refundPayouts").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Actual_request_seed_cannot_be_reused_with_equal_claims_new_authority_another_transaction_or_changed_invoice() {
        await using var data = await ApprovedAsync(); var catalog = data.Allocation.Source.Read.Catalog; var actor = data.Allocation.Source.Reader;
        var heads = await MoneyHeadsAsync(data); var cash = await data.CashAsync();
        await new AuthorizationStore(data.Database, new(catalog.Client), data.Clock).ReadAsync(actor, async (authority, tx, ct) => {
            var seed = await data.Entitlements.ApprovalSourceAsync(actor, authority, tx, data.CaseId, data.ReturnId, "refund", false, ct);
            var owner = data.Allocation.Service;
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => owner.FinancialSourcesAsync(actor with { }, authority, tx,
                seed.Invoice.MoneySource, [data.AllocationId], seed, ct));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => owner.FinancialSourcesAsync(actor, authority with { }, tx,
                seed.Invoice.MoneySource, [data.AllocationId], seed, ct));
            using (var other = await catalog.Client.StartSessionAsync(cancellationToken: ct)) {
                other.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
                try {
                    await Assert.ThrowsAsync<AuthorizationDeniedException>(() => owner.FinancialSourcesAsync(actor, authority, other,
                        seed.Invoice.MoneySource, [data.AllocationId], seed, ct));
                } finally { await other.AbortTransactionAsync(ct); }
            }
            var changedInvoice = seed.Invoice.MoneySource.DeepClone().AsBsonDocument;
            changedInvoice["snapshotHash"] = new string(changedInvoice["snapshotHash"].AsString[0] == 'a' ? 'b' : 'a', 64);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => owner.FinancialSourcesAsync(actor, authority, tx,
                changedInvoice, [data.AllocationId], seed, ct));
            seed.RequireContext(actor, authority, tx); await seed.FenceReadAsync(ct); seed.RequireCurrent(); return true;
        }, default);
        Assert.Equal(heads, await MoneyHeadsAsync(data)); Assert.Equal(cash, await data.CashAsync());
        Assert.Equal(0, await data.Rows("refundPayouts").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageTheory]
    [InlineData("deleted-event")] [InlineData("orphan-allocation-journal")] [InlineData("orphan-financial-journal")]
    [InlineData("deleted-shipment")] [InlineData("deleted-return-custody")]
    public async Task Same_transaction_monetary_history_tampering_after_capture_denies_final_fence_and_rolls_back(string fault) {
        await using var data = await ApprovedAsync(); var catalog = data.Allocation.Source.Read.Catalog; var actor = data.Allocation.Source.Reader;
        var approval = await data.Rows(ReturnFinancialApprovalBson.Events).Find(new BsonDocument()).SingleAsync();
        var original = await data.Rows(B.Events).Find(new BsonDocument("_id", data.AllocationId)).SingleAsync();
        var heads = await MoneyHeadsAsync(data); var cash = await data.CashAsync();
        var journals = new BsonArray(await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var invoice = await data.Rows("invoices").Find(new BsonDocument("_id", approval["invoiceId"])).SingleAsync();
        var shipment = await data.Rows("shipments").Find(new BsonDocument("_id", invoice["lines"][0]["shipmentId"])).SingleAsync();
        var custody = await data.Rows("returnQuarantineReceipts").Find(new BsonDocument("_id", data.ReturnId)).SingleAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new AuthorizationStore(data.Database, new(catalog.Client), data.Clock).ReadAsync(actor, async (authority, tx, ct) => {
            var source = await data.Service.PayoutSourceAsync(actor, authority, tx, approval["_id"].AsInt64, ct);
            if (fault == "deleted-event")
                Assert.Equal(1, (await data.Rows(B.Events).DeleteOneAsync(tx, new BsonDocument("_id", original["_id"]), cancellationToken: ct)).DeletedCount);
            else if (fault == "deleted-shipment" || fault == "deleted-return-custody") {
                var table = fault == "deleted-shipment" ? "shipments" : "returnQuarantineReceipts";
                var row = fault == "deleted-shipment" ? shipment : custody;
                Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(tx, new BsonDocument("_id", row["_id"]), cancellationToken: ct)).DeletedCount);
            }
            else {
                var template = journals.Single(journal => journal["_id"] == (fault == "orphan-allocation-journal" ? original["journalId"] : approval["journalId"])).AsBsonDocument;
                var actual = AccountingBson.ReadDraft(template);
                var orphan = JournalDraft.FromResolved(new(actual.Source.Namespace, 998001L, actual.Source.EventKind, actual.Source.Occurrence),
                    actual.SourceRevision, actual.Book, actual.Dimensions, actual.Date, actual.ChartId, actual.ChartVersion, actual.Lines, actual.ReversalOf);
                var row = AccountingBson.Journal(998001L, template["bookId"].AsInt64, orphan, actor,
                    new("Synthetic same-transaction orphan integrity assertion", "payout-source-tamper"), data.Clock.UtcNow);
                await data.Rows("journalEntries").InsertOneAsync(tx, row, cancellationToken: ct);
            }
            await source.FenceAsync(ct); return true;
        }, default));
        Assert.Equal(original, await data.Rows(B.Events).Find(new BsonDocument("_id", data.AllocationId)).SingleAsync());
        Assert.Equal(heads, await MoneyHeadsAsync(data)); Assert.Equal(cash, await data.CashAsync());
        Assert.Equal(journals, new BsonArray(await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Equal(shipment, await data.Rows("shipments").Find(new BsonDocument("_id", shipment["_id"])).SingleAsync());
        Assert.Equal(custody, await data.Rows("returnQuarantineReceipts").Find(new BsonDocument("_id", custody["_id"])).SingleAsync());
        Assert.Equal(0, await data.Rows("refundPayouts").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Credit_only_approval_cannot_be_a_refund_payout_source() {
        await using var data = await ReturnFinancialMixedHistoryTests.Data.CreateAsync(fixture, "credit"); var input = data.Input(1);
        var preview = await data.Service.PreviewAsync(data.Approver, data.CaseId, data.ReturnId, "credit", input);
        var approval = await data.Service.ApproveAsync(data.Approver, data.CaseId, data.ReturnId, "credit", preview.Binding, input, ReturnFinancialMixedHistoryTests.Data.Meta("payout-source-credit"));
        var catalog = data.Allocation.Source.Read.Catalog; var actor = data.Allocation.Source.Reader;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new AuthorizationStore(data.Database, new(catalog.Client), data.Clock).ReadAsync(actor,
            (authority, tx, ct) => data.Service.PayoutSourceAsync(actor, authority, tx, approval["financialApprovalId"].AsInt64, ct), default));
    }

    [Fact]
    public async Task Missing_first_cash_journal_or_wrong_scope_cannot_supply_a_paying_bank() {
        await using var data = await BankReceiptTestData.CreateAsync(fixture); var confirmed = await data.ConfirmAsync(await data.PreviewAsync());
        var catalog = data.Statements.Accounts.Catalog; var selection = data.Statements.Selection; var database = catalog.Database;
        var reads = new AuthorizationStore(database, new(catalog.Client), catalog.Clock);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => reads.ReadAsync(data.Other,
            (authority, tx, ct) => data.Service.PayoutBankSourceAsync(data.Other, authority, tx, selection.AccountId, selection.AccountVersionId, ct), default));
        var receipt = await data.Rows(BankReceiptBson.Receipts).Find(new BsonDocument("_id", confirmed["receiptId"])).SingleAsync();
        var deleted = await data.Rows("journalEntries").DeleteOneAsync(new BsonDocument("_id", receipt["journalId"])); Assert.Equal(1, deleted.DeletedCount);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => reads.ReadAsync(data.Reader,
            (authority, tx, ct) => data.Service.PayoutBankSourceAsync(data.Reader, authority, tx, selection.AccountId, selection.AccountVersionId, ct), default));
    }

    private async Task<ReturnFinancialMixedHistoryTests.Data> ApprovedAsync() {
        var data = await ReturnFinancialMixedHistoryTests.Data.CreateAsync(fixture);
        try {
            var input = data.Input(1, 600m); var preview = await data.Service.PreviewAsync(data.Approver, data.CaseId, data.ReturnId, "refund", input);
            await data.Service.ApproveAsync(data.Approver, data.CaseId, data.ReturnId, "refund", preview.Binding, input, ReturnFinancialMixedHistoryTests.Data.Meta("payout-source-approved"));
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    private static async Task<BsonDocument> MoneyHeadsAsync(ReturnFinancialMixedHistoryTests.Data data) => new() {
        { "receipts", new BsonArray(await data.Rows(B.Receipts).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) },
        { "invoices", new BsonArray(await data.Rows(B.Invoices).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) }
    };
}
