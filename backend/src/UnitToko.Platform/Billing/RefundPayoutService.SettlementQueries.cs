using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using D = UnitToko.Platform.Billing.RefundSettlementBson;
using M = UnitToko.Platform.Billing.PaymentAllocationBson;
using MR = UnitToko.Platform.Billing.PaymentAllocationRecords;
using P = UnitToko.Platform.Billing.RefundPayoutBson;
using R = UnitToko.Platform.Billing.RefundPayoutRecords;

namespace UnitToko.Platform.Billing;

public sealed partial class RefundPayoutService
{
    private async Task<BsonDocument?> SettlementKeyAsync(IClientSessionHandle tx, long actor, string key, CancellationToken ct) {
        var row = await records.Rows(D.Events).Find(tx, new BsonDocument { { "actorId", actor }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) return await RefundSettlementRecords.ReadAsync(database, tx, row["_id"].AsInt64, ct, clock);
        foreach (var (table, kind, field) in new[] { ("idempotencyRecords", "operation", "key"), ("auditEvents", "kind", "requestKey"), ("outboxEvents", "kind", "requestKey") })
            if (await records.Rows(table).Find(tx, new BsonDocument { { "actorId", actor }, { kind, D.Operation }, { field, key } }).AnyAsync(ct)) throw M.Broken();
        return null;
    }
    private async Task<BsonDocument?> SettlementSavedAsync(IClientSessionHandle tx, long payoutId, CancellationToken ct) {
        var row = await records.Rows(D.Events).Find(tx, new BsonDocument("payoutId", payoutId)).SingleOrDefaultAsync(ct);
        if (row is not null) return await RefundSettlementRecords.ReadAsync(database, tx, row["_id"].AsInt64, ct, clock);
        foreach (var (table, kind, payload) in new[] { ("idempotencyRecords", "operation", "result"), ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload") })
            if (await records.Rows(table).Find(tx, new BsonDocument { { kind, D.Operation }, { payload + ".payoutId", payoutId } }).AnyAsync(ct)) throw M.Broken();
        return null;
    }
    // Read/replay admission reconstructs the current money graph independently. A previously
    // claimed debit is read through the bank's historical owner, never recaptured as unclaimed.
    private async Task<(BsonDocument Payout, RefundPayoutApprovalSource Approval, Action Current)> AdmitSettlementReadAsync(
        ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long payoutId, BsonDocument? saved, CancellationToken ct, bool requireLivePolicy = false) {
        var payout = await records.PayoutAsync(tx, payoutId, false, ct); var resources = PaymentDraftBson.Scopes(payout["scopes"].AsBsonArray);
        _ = RequireSettlement(actor, authority, resources);
        var source = await approvals.PayoutSourceAsync(actor, authority, tx, payout["financialApprovalId"].AsInt64, ct);
        R.Equal(payout["approvalHash"], source.Approval["snapshotHash"]);
        R.Equal(payout["source"]["approval"], ApprovalSnapshot(source.ApprovedProof));
        var draft = await records.DraftAsync(tx, payout["draftId"].AsInt64, ct);
        _ = await BankReceiptService.ReadPayoutBankHistoryAsync(database, tx, payout["source"]["payingBank"].AsBsonDocument,
            new(draft["createdAt"].ToUniversalTime()), ct);
        RefundSettlementHistoricalBankSource? bank = null;
        if (saved is not null) {
            R.Equal(saved["payoutId"], payoutId); R.Equal(saved["payoutHash"], payout["snapshotHash"]);
            bank = await banks.SettlementHistorySourceAsync(actor, authority, tx, saved, ct);
            if (requireLivePolicy) {
                var live = await records.Rows(D.Policies).Find(tx, new BsonDocument("_id", saved["policy"]["_id"])).SingleOrDefaultAsync(ct) ?? throw M.Broken();
                R.Equal(M.Snapshot(live), saved["policy"]); D.PolicyAt(live, Now); await records.TouchAsync(tx, D.Policies, live, ct);
            }
        }
        var recorder = payout["recordedBy"].AsInt64; var importer = saved?["debit"]["importedBy"].AsInt64;
        var accounts = new[] { actor.UserId, recorder }.Concat(importer is { } id ? new[] { id } : []).Distinct().Order().ToArray();
        var people = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [.. accounts], ct);
        var verifier = people.Evidence.Single(person => person.UserId == actor.UserId);
        if (!PersonIndependencePolicy.AreIndependent(verifier, people.Evidence.Where(person => person.UserId != actor.UserId).ToArray()) || actor.UserId == recorder || actor.UserId == importer)
            throw new AuthorizationDeniedException("independent_person_required");
        var originalRecorder = payout["people"].AsBsonArray.Select(ReceivingAccountBson.Person).Single(person => person.UserId == recorder);
        R.Equal(people.Evidence.Single(person => person.UserId == recorder).PersonId ?? throw M.Broken(), originalRecorder.PersonId ?? throw M.Broken());
        if (saved is not null) foreach (var old in saved["people"].AsBsonArray.Select(ReceivingAccountBson.Person).Where(person => accounts.Contains(person.UserId)))
            R.Equal(people.Evidence.Single(person => person.UserId == old.UserId).PersonId ?? throw M.Broken(), old.PersonId ?? throw M.Broken());
        await new PersonAuthorizationFence(database, clock).TouchAsync(tx, people, ct);
        void Current() {
            _ = RequireSettlement(actor, authority, resources); source.RequireCurrent(); bank?.RequireCurrent(); people.RequireCurrent(tx, clock.UtcNow);
            if (requireLivePolicy && saved is not null) D.PolicyAt(saved["policy"].AsBsonDocument, Now); ct.ThrowIfCancellationRequested();
        }
        Current(); await source.FenceAsync(ct); if (bank is not null) await bank.FenceAsync(ct);
        await SettlementReadAuthorityFenceAsync(actor, authority, tx, resources, ct);
        var actual = await records.PayoutAsync(tx, payoutId, false, ct); R.Equal(M.Snapshot(actual), M.Snapshot(payout)); await records.TouchAsync(tx, P.Payouts, actual, ct);
        if (saved is not null) {
            var final = await RefundSettlementRecords.ReadAsync(database, tx, saved["_id"].AsInt64, ct, clock);
            R.Equal(M.Snapshot(final), M.Snapshot(saved)); await new MR(database, clock).TouchAsync(tx, D.Events, final, ct);
        }
        Current(); return (payout, source, Current);
    }
    private static BsonDocument SettlementView(BsonDocument payout, BsonDocument? saved, BsonDocument payingDetails) {
        var beneficiary = payout["source"]["beneficiary"]["details"]; var input = payout["input"];
        return new() { { "payout", P.Result(payout) }, { "caseId", payout["caseId"] }, { "returnId", payout["returnId"] }, { "invoiceId", payout["invoiceId"] },
            { "amount", input["amount"] }, { "draftRevision", payout["draftRevision"] }, { "draftId", payout["draftId"] }, { "reservationId", payout["reservationId"] },
            { "evidence", payout["evidence"].DeepClone() }, { "settlement", saved is null ? BsonNull.Value : D.Result(saved) },
            { "beneficiaryName", beneficiary["holderName"] }, { "beneficiaryBankCode", beneficiary["bankCode"] }, { "beneficiaryAccountSuffix", beneficiary["accountNumber"].AsString[^4..] },
            { "payingBankCode", payingDetails["bankCode"] }, { "payingAccountSuffix", payingDetails["accountNumber"].AsString[^4..] },
            { "transferDate", input["transferDate"] }, { "transferReference", input["transferReference"] },
            { "candidateInstructions", "Cocokkan debit bank yang belum digunakan, rekening penerima, nominal dan bukti asli; pemeriksa harus berbeda dari pencatat dan pengimpor bank." } };
    }
    public async Task<BsonDocument> ReadSettlementAsync(ActorContext actor, long payoutId, CancellationToken ct = default) {
        if (payoutId <= 0) throw RefundSettlementPolicy.Invalid(); await SettlementStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var saved = await SettlementSavedAsync(tx, payoutId, token); var read = await AdmitSettlementReadAsync(actor, authority, tx, payoutId, saved, token);
            var version = await new ReceivingAccountRecords(database).RowAsync(tx, ReceivingAccountBson.Versions, read.Payout["input"]["payingAccountVersionId"].AsInt64, token);
            var view = SettlementView(read.Payout, saved, version["details"].AsBsonDocument); read.Current(); return view;
        }, ct);
    }
    public async Task<RefundSettlementPreview> PreviewSettlementAsync(ActorContext actor, long payoutId, RefundSettlementInput input, CancellationToken ct = default) {
        input = RefundSettlementPolicy.Copy(input); if (payoutId <= 0) throw RefundSettlementPolicy.Invalid(); await SettlementStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var frame = await PrepareSettlementAsync(actor, authority, tx, payoutId, input, token);
            if (await SettlementSavedAsync(tx, payoutId, token) is not null) throw M.Changed("refund_settlement_already_confirmed");
            var debit = frame.Bank.Debit; var account = frame.Bank.BankSource.PayingBank.Account["details"];
            var view = SettlementView(frame.Payout, null, account.AsBsonDocument); view.Add("input", D.Input(input));
            view.Add("debit", new BsonDocument { { "bankImportId", debit["importId"] }, { "bankRowNumber", debit["rowNumber"] }, { "transactionId", debit["transactionId"] },
                { "bookedDate", debit["bookedDate"] }, { "amount", debit["amount"] }, { "accountDisplay", account["accountNumber"].AsString[^4..] }, { "importedBy", debit["importedBy"] } });
            view.Add("policy", new BsonDocument { { "bankDebitVerificationProcedureReference", frame.Policy["bankDebitVerificationProcedureReference"] },
                { "beneficiaryVerificationProcedureReference", frame.Policy["beneficiaryVerificationProcedureReference"] } });
            view.Add("receipts", new BsonArray(frame.Review["receipts"].AsBsonArray.Select(receipt => new BsonDocument { { "receiptId", receipt["receiptId"] },
                { "versionBefore", receipt["before"]["version"] }, { "versionAfter", receipt["version"] }, { "paidBefore", receipt["paidBefore"] }, { "paidAfter", receipt["paidAfter"] },
                { "pending", receipt["pending"] }, { "reservedForRefund", receipt["before"]["reservedForRefund"] }, { "available", receipt["available"] },
                { "received", receipt["receiptTotal"] }, { "allocated", receipt["before"]["allocated"] } })));
            var binding = D.Binding(frame.Payout, debit, frame.Policy, frame.Review["receipts"].AsBsonArray, frame.Review["releases"].AsBsonArray, input);
            await frame.Approval.FenceAsync(token); await frame.Bank.FenceAsync(token); await SettlementAuthorityFenceAsync(frame, token);
            SettlementCurrent(frame); return new RefundSettlementPreview(binding, view);
        }, ct);
    }
    private Task<BsonDocument?> RecoverSettlementCoreAsync(ActorContext actor, long payoutId, string key, string? bodyHash, CancellationToken ct, bool requireLivePolicy = false) =>
        authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var saved = await SettlementKeyAsync(tx, actor.UserId, key, token);
            var read = await AdmitSettlementReadAsync(actor, authority, tx, payoutId, saved, token, requireLivePolicy);
            if (saved is not null && bodyHash is not null && saved["bodyHash"] != bodyHash) throw M.Changed("idempotency_key_reused");
            read.Current(); return saved;
        }, ct);
    public async Task<BsonDocument?> RecoverSettlementAsync(ActorContext actor, long payoutId, string key, CancellationToken ct = default) {
        Metadata(new(key, "Recover bank-verified refund", "refund-settlement-recovery")); if (payoutId <= 0) throw RefundSettlementPolicy.Invalid(); await SettlementStorageAsync(ct);
        var row = await RecoverSettlementCoreAsync(actor, payoutId, key, null, ct); return row is null ? null : D.Result(row);
    }
}
