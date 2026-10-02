using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using D = UnitToko.Platform.Billing.RefundSettlementBson;
using M = UnitToko.Platform.Billing.PaymentAllocationBson;
using MR = UnitToko.Platform.Billing.PaymentAllocationRecords;
using R = UnitToko.Platform.Billing.RefundPayoutRecords;

namespace UnitToko.Platform.Billing;

public sealed partial class RefundPayoutService
{
    private CommandAuthorization SettlementCommand(ActorContext actor, AuthorizationSnapshot authority, RefundPayoutApprovalSource approval, BsonDocument payout, long importer) {
        var resources = PaymentDraftBson.Scopes(approval.Invoice["scopes"].AsBsonArray); _ = RequireSettlement(actor, authority, resources);
        var people = new ApprovalRequest(1, 1, RecordedBy: payout["recordedBy"].AsInt64, SubmittedBy: importer);
        return new(ScopePolicy.Authorize(actor, authority, Permissions.DisbursementVerify, resources[0], clock.UtcNow, people),
            [.. resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory)).Distinct()], Permissions.DisbursementVerify, new(resources[0], people));
    }
    public async Task<BsonDocument> VerifySettlementAsync(ActorContext actor, long payoutId, RefundSettlementInput input, string binding,
        bool attested, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = RefundSettlementPolicy.Copy(input); Metadata(metadata);
        if (payoutId <= 0 || !attested || binding is not { Length: 64 } || binding.Any(value => !char.IsAsciiHexDigitLower(value))) throw RefundSettlementPolicy.Invalid();
        await SettlementStorageAsync(ct); var body = D.Body(payoutId, binding, input, attested); var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        var recovered = await RecoverSettlementCoreAsync(actor, payoutId, metadata.IdempotencyKey, hash, ct, requireLivePolicy: true);
        if (recovered is not null) return D.Result(recovered);
        var id = (await ids.NextAsync(M.Events, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        SettlementFrame? frame = null; Action? finalCurrent = null; BsonDocument? replay = null;
        return await authorization.ExecuteCommandAsync(actor, D.Operation, metadata, body, async (authority, tx, token) => {
            frame = null; finalCurrent = null; replay = await SettlementKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, token);
            if (replay is not null) {
                var read = await AdmitSettlementReadAsync(actor, authority, tx, payoutId, replay, token, requireLivePolicy: true);
                finalCurrent = read.Current;
                return SettlementCommand(actor, authority, read.Approval, read.Payout, replay["debit"]["importedBy"].AsInt64);
            }
            frame = await PrepareSettlementAsync(actor, authority, tx, payoutId, input, token);
            finalCurrent = () => SettlementCurrent(frame);
            return SettlementCommand(actor, authority, frame.Approval, frame.Payout, frame.Bank.Debit["importedBy"].AsInt64);
        }, async (tx, token) => {
            var current = frame ?? throw M.Broken(); if (replay is not null) throw M.Broken();
            if (await SettlementSavedAsync(tx, payoutId, token) is not null) throw M.Changed("refund_settlement_already_confirmed");
            R.Equal(binding, D.Binding(current.Payout, current.Bank.Debit, current.Policy, current.Review["receipts"].AsBsonArray, current.Review["releases"].AsBsonArray, input));
            // Complete captured BEFORE money/source fences before advancing this command's
            // receipt heads. The final phase obtains an independent AFTER reconstruction.
            await current.Approval.FenceAsync(token); await current.Bank.FenceAsync(token); await SettlementAuthorityFenceAsync(current, token);
            var at = Now; var payout = current.Payout; var review = current.Review;
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "operation", D.Operation }, { "payoutId", payoutId }, { "payoutHash", payout["snapshotHash"] },
                { "financialApprovalId", payout["financialApprovalId"] }, { "approvalHash", payout["approvalHash"] }, { "caseId", payout["caseId"] }, { "returnId", payout["returnId"] }, { "invoiceId", payout["invoiceId"] },
                { "party", payout["party"].DeepClone() }, { "invoice", current.Approval.Invoice.DeepClone() }, { "amount", review["amount"] }, { "canonicalIdentity", review["canonicalIdentity"] },
                { "debit", current.Bank.Debit.DeepClone() }, { "input", D.Input(input) }, { "policy", M.Snapshot(current.Policy) }, { "binding", binding }, { "attested", true },
                { "receipts", review["receipts"].DeepClone() }, { "releases", review["releases"].DeepClone() },
                { "people", new BsonArray(current.People.Evidence.OrderBy(person => person.UserId).Select(ReceivingAccountBson.Person)) },
                { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "recordedAt", at.UtcDateTime }, { "postingDate", InvoiceBson.Date(AccountingCalendar.BusinessDate(at)) },
                { "requestKey", metadata.IdempotencyKey }, { "bodyHash", hash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId },
                { "fence", 0L }, { "postingFence", 0L }, { "journalId", BsonNull.Value }, { "journalFingerprint", BsonNull.Value }, { "snapshotHash", BsonNull.Value },
                { "liabilityAccount", current.Approval.LiabilityAccount.DeepClone() }, { "cashAccount", current.Bank.BankSource.PayingBank.CashAccount.DeepClone() } };
            await records.Rows(D.Events).InsertOneAsync(tx, row, cancellationToken: token);
            var journal = await new PostJournal(database, clock, [new RefundSettlementPostingSource(database, row)]).PostAsync(tx, actor,
                RefundSettlementPostingSource.Source(id), 1, new(metadata.Reason, metadata.CorrelationId), token);
            var actualJournal = await records.Rows("journalEntries").Find(tx, new BsonDocument("_id", journal.Id)).SingleOrDefaultAsync(token) ?? throw M.Broken();
            RefundSettlementPostingSource.RequireAccounts(row, AccountingBson.ReadDraft(actualJournal));
            var pending = row.DeepClone().AsBsonDocument; pending["postingFence"] = 1L;
            row["journalId"] = journal.Id; row["journalFingerprint"] = journal.Fingerprint; row["postingFence"] = 1L; row["snapshotHash"] = D.Hash(row);
            if ((await records.Rows(D.Events).ReplaceOneAsync(tx, pending, row, cancellationToken: token)).ModifiedCount != 1) throw M.Changed();
            var money = new MR(database, clock);
            foreach (var receipt in row["receipts"].AsBsonArray)
                await money.SaveHeadAsync(tx, true, receipt["receiptId"].AsInt64, current.Approval.ReceiptHeads[receipt["receiptId"].AsInt64], row, receipt["receiptTotal"], receipt["receiptHash"], token);
            await records.Rows("auditEvents").InsertOneAsync(tx, D.Audit(row), cancellationToken: token);
            await records.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", D.Operation }, { "sourceKey", D.SourceKey(row) },
                { "actorId", actor.UserId }, { "requestKey", metadata.IdempotencyKey }, { "payload", D.Result(row) }, { "createdAt", at.UtcDateTime }, { "dueAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            await current.Bank.ClaimAsync(row, token); SettlementCurrent(current); return D.Result(row);
        }, ct, async (authority, tx, token) => {
            var saved = await SettlementKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, token) ?? throw M.Broken();
            R.Equal(saved["bodyHash"], hash); R.Equal(saved["payoutId"], payoutId);
            var read = await AdmitSettlementReadAsync(actor, authority, tx, payoutId, saved, token, requireLivePolicy: true);
            if (frame is not null) {
                R.Equal(read.Approval.InvoiceBalance, frame.Approval.InvoiceBalance);
                foreach (var receipt in saved["receipts"].AsBsonArray) {
                    var actual = read.Approval.Receipts.Single(value => value["receiptId"] == receipt["receiptId"]);
                    R.Equal(actual["before"], MR.After(saved, true, receipt["receiptId"].AsInt64));
                    R.Equal(actual["receiptTotal"], receipt["receiptTotal"]); R.Equal(actual["receiptHash"], receipt["receiptHash"]);
                }
                SettlementCurrent(frame);
            }
            finalCurrent = read.Current; finalCurrent();
        }, validateCurrent: () => { finalCurrent?.Invoke(); if (frame is not null) SettlementCurrent(frame); });
    }
}
