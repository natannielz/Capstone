using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using D = UnitToko.Platform.Billing.RefundSettlementBson;

namespace UnitToko.Tests.Ordering;

public sealed partial class RefundSettlementTests
{
    [WindowsStorageFact]
    public async Task Wrong_real_debit_amount_incoming_credit_and_inactive_policy_cannot_confirm_a_recorded_refund() {
        await using var data = await Data.CreateAsync(fixture);
        var smaller = data.Input with { BankImportId = await data.ImportDebitAsync("SETTLEMENT-SMALLER", "settlement-smaller-import", 599.99m) };
        var credit = data.Input with { BankImportId = await data.ImportDebitAsync("SETTLEMENT-CREDIT", "settlement-credit-import", credit: true) };
        var state = await data.SettlementStateAsync(); var permanent = await data.PermanentAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => data.PreviewAsync(input: smaller));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.PreviewAsync(input: credit));
        var preview = await data.PreviewAsync();
        var policy = await data.Rows(D.Policies).Find(new BsonDocument()).SingleAsync();
        await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", policy["_id"]), Builders<BsonDocument>.Update.Set("active", false));
        try {
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.VerifyAsync(preview, "settlement-inactive-policy"));
            Assert.Equal("refund_settlement_policy_unavailable", error.Code);
            Assert.Equal(state, await data.SettlementStateAsync()); Assert.Equal(permanent, await data.PermanentAsync());
        } finally { await data.Rows(D.Policies).ReplaceOneAsync(new BsonDocument("_id", policy["_id"]), policy); }
        var saved = await data.VerifyAsync(preview, "settlement-inactive-policy");
        Assert.Equal(saved, await data.Service.RecoverSettlementAsync(data.Verifier, data.PayoutId, "settlement-inactive-policy"));
    }

    [WindowsStorageFact]
    public async Task Settlement_and_normal_allocation_race_on_the_actual_shared_receipt_version_without_releasing_blocked_funds() {
        await using var data = await Data.CreateAsync(fixture); var allocation = data.Payout.Source.Allocation;
        var cash = await data.Payout.Source.CashAsync(); var originalAllocation = await data.Payout.Source.OriginalAsync();
        var payoutHash = data.PayoutRecord["snapshotHash"]; var settlement = await data.PreviewAsync();
        var input = new PaymentAllocationInput(2, [new(new(allocation.InvoiceId), 2, new(200m))]);
        var preview = await allocation.Service.PreviewAsync(allocation.Actor, allocation.VerificationId, input);
        var results = await Task.WhenAll(Record.ExceptionAsync(() => data.VerifyAsync(settlement, "settlement-shared-receipt-race")),
            Record.ExceptionAsync(() => allocation.Service.AllocateAsync(allocation.Actor, allocation.VerificationId, preview.Binding, input, Data.Meta("allocation-shared-receipt-race"))));
        Assert.Single(results, error => error is null); Assert.Single(results, error => error is not null);
        var settled = results[0] is null; var balances = await data.Payout.Source.ReadBalancesAsync();
        Assert.Equal(3L, balances.ReceiptBalance["version"].AsInt64);
        Assert.Equal(settled ? 1400m : 1600m, balances.ReceiptBalance["allocated"].AsDecimal);
        Assert.Equal(600m, balances.ReceiptBalance["reservedForRefund"].AsDecimal);
        Assert.Equal(settled ? 2000m : 1800m, 4000m - balances.ReceiptBalance["allocated"].AsDecimal - balances.ReceiptBalance["reservedForRefund"].AsDecimal);
        Assert.Equal(settled ? 2L : 3L, balances.Invoices[0]["balance"]["version"].AsInt64);
        Assert.Equal(settled ? 1400m : 1600m, balances.Invoices[0]["balance"]["paid"].AsDecimal);
        Assert.Equal(1000.02m, balances.Invoices[0]["balance"]["credited"].AsDecimal);
        Assert.Equal(2, await data.Rows(B.Events).CountDocumentsAsync(new BsonDocument()) + await data.Rows(D.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(settled ? 1 : 0, await data.Rows(BankTransactionClaims.Collection).CountDocumentsAsync(new BsonDocument("direction", "debit")));
        Assert.Equal(cash, await data.Payout.Source.CashAsync()); Assert.Equal(originalAllocation, await data.Payout.Source.OriginalAsync());
        Assert.Equal(payoutHash, (await data.Rows(RefundPayoutBson.Payouts).Find(new BsonDocument("_id", data.PayoutId)).SingleAsync())["snapshotHash"]);
    }

    [WindowsStorageFact]
    public async Task Incoming_credit_and_actual_refund_debit_cannot_consume_the_same_canonical_bank_transaction() {
        await using var data = await Data.CreateAsync(fixture); var debit = await data.PreviewAsync();
        var creditImport = await data.ImportDebitAsync("SETTLEMENT-0001", "settlement-cross-direction-credit", credit: true);
        var credit = await data.Bank.PreviewAsync(creditImport);
        var imported = await data.Rows(BankStatementBson.Imports).Find(new BsonDocument("_id", creditImport)).SingleAsync();
        var observed = new BankReceiptObservation(imported["account"]["details"]["accountNumber"].AsString, "SETTLEMENT-0001", data.Payout.Input.TransferDate, new(600m));
        var results = await Task.WhenAll(Record.ExceptionAsync(() => data.VerifyAsync(debit, "settlement-cross-direction-debit")),
            Record.ExceptionAsync(() => data.Bank.ConfirmAsync(credit, "settlement-cross-direction-credit-confirm", observed)));
        Assert.Single(results, error => error is null); Assert.Single(results, error => error is not null);
        var canonical = imported["preview"]["rows"][0]["identityCandidate"];
        var claim = await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument("canonicalIdentity", canonical)).SingleAsync();
        var settled = results[0] is null; Assert.Equal(settled ? "debit" : "credit", claim["direction"].AsString);
        Assert.Equal(settled ? 1 : 0, await data.Rows(D.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(settled ? 1 : 2, await data.Rows(BankReceiptBson.Receipts).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(settled ? 1 : 0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", D.Operation)));
        Assert.Equal(settled ? 1 : 2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", "billing.bank-receipt")));
        var balance = await data.Payout.Source.ReadBalancesAsync(); Assert.Equal(settled ? 3L : 2L, balance.ReceiptBalance["version"].AsInt64);
        Assert.Equal(1400m, balance.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(600m, balance.ReceiptBalance["reservedForRefund"].AsDecimal);
        Assert.Equal(2L, balance.Invoices[0]["balance"]["version"].AsInt64); Assert.Equal(1400m, balance.Invoices[0]["balance"]["paid"].AsDecimal);
    }

    [WindowsStorageFact]
    public async Task Coherent_deleted_settlement_and_restored_original_head_cannot_hide_its_real_outgoing_cash_journal() {
        await using var data = await Data.CreateAsync(fixture);
        var incomingImport = await data.ImportDebitAsync("SETTLEMENT-0001", "settlement-orphan-incoming-import", credit: true);
        var incoming = await data.Bank.PreviewAsync(incomingImport);
        var imported = await data.Rows(BankStatementBson.Imports).Find(new BsonDocument("_id", incomingImport)).SingleAsync();
        var observed = new BankReceiptObservation(imported["account"]["details"]["accountNumber"].AsString, "SETTLEMENT-0001", data.Payout.Input.TransferDate, new(600m));
        var originalHead = await data.Rows(B.Receipts).Find(new BsonDocument()).SingleAsync(); var preview = await data.PreviewAsync();
        const string key = "settlement-orphan-outgoing-journal"; var saved = await data.VerifyAsync(preview, key);
        var row = await data.Rows(D.Events).Find(new BsonDocument("_id", saved["settlementId"])).SingleAsync();
        var originalAudit = await data.Rows("auditEvents").Find(new BsonDocument("_id", row["auditId"])).SingleAsync();
        var originalEffect = await data.Rows("outboxEvents").Find(new BsonDocument("sourceKey", D.SourceKey(row))).SingleAsync();
        var originalOperation = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", data.Verifier.UserId }, { "operation", D.Operation }, { "key", key } }).SingleAsync();
        var originalJournal = await data.Rows("journalEntries").Find(new BsonDocument("_id", row["journalId"])).SingleAsync();
        // Remove the actual command graph and restore the actual pre-event head. Its real
        // finalized outgoing journal remains; no fabricated source or weaker validator.
        Assert.Equal(1, (await data.Rows(D.Events).DeleteOneAsync(new BsonDocument("_id", row["_id"]))).DeletedCount);
        Assert.Equal(1, (await data.Rows(BankTransactionClaims.Collection).DeleteOneAsync(new BsonDocument("_id", row["canonicalIdentity"]))).DeletedCount);
        await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", row["auditId"]));
        await data.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("sourceKey", D.SourceKey(row)));
        await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "actorId", data.Verifier.UserId }, { "operation", D.Operation }, { "key", key } });
        Assert.Equal(1, (await data.Rows(B.Receipts).ReplaceOneAsync(new BsonDocument("_id", originalHead["_id"]), originalHead)).ModifiedCount);
        var damaged = await data.SettlementStateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Payout.Source.ReadBalancesAsync());
        var reader = data.Payout.Source.Allocation.Source.Reader;
        var canonicalInvoice = (await data.Rows(ReturnFinancialApprovalBson.Events).Find(new BsonDocument("_id", data.Payout.ApprovalId)).SingleAsync())["invoice"].AsBsonDocument;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new AuthorizationStore(data.Database, new(data.Catalog.Client), data.Clock).ReadAsync(reader,
            (authority, tx, ct) => data.AllocationService.FinancialSourcesAsync(reader, authority, tx, canonicalInvoice, [], ct), default));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverSettlementAsync(data.Verifier, data.PayoutId, key));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.PreviewAsync());
        Assert.Equal(damaged, await data.SettlementStateAsync());
        // Each genuine surviving outgoing record independently blocks both directions
        // at the bank owner, before any new command's provisional receipt is inserted.
        await data.Rows("journalEntries").DeleteOneAsync(new BsonDocument("_id", originalJournal["_id"]));
        foreach (var (table, actual) in new[] { ("journalEntries", originalJournal), ("auditEvents", originalAudit),
            ("outboxEvents", originalEffect), ("idempotencyRecords", originalOperation) }) {
            await data.Rows(table).InsertOneAsync(actual);
            try {
                var orphan = await data.SettlementStateAsync();
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Bank.PreviewAsync(incomingImport));
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Bank.ConfirmAsync(incoming, "settlement-orphan-incoming-denied", observed));
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new AuthorizationStore(data.Database, new(data.Catalog.Client), data.Clock).ReadAsync(data.Verifier,
                    async (authority, tx, ct) => {
                        var captured = await data.Bank.Service.CaptureSettlementClaimAsync(data.Verifier, authority, tx, data.Payout.Input.PayingAccountId,
                            data.Payout.Input.PayingAccountVersionId, data.Input.BankImportId, data.Input.BankRowNumber, ct);
                        await captured.FenceAsync(ct); captured.RequireCurrent(); return true;
                    }, default));
                Assert.Equal(orphan, await data.SettlementStateAsync());
            } finally { await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", actual["_id"])); }
        }
        await data.Rows("journalEntries").InsertOneAsync(originalJournal);
        Assert.Equal(damaged, await data.SettlementStateAsync());
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", D.Operation)));
    }
}
