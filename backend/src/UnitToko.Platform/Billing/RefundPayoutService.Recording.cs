using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Billing.RefundPayoutBson;
using R = UnitToko.Platform.Billing.RefundPayoutRecords;

namespace UnitToko.Platform.Billing;

public sealed partial class RefundPayoutService
{
    private async Task<(BsonDocument Draft, Frame Frame)> DraftFrameAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long draftId, CancellationToken ct, bool documentAccess = false) {
        var draft = await records.DraftAsync(tx, draftId, ct);
        if (draft["userId"] != actor.UserId) throw new AuthorizationDeniedException("refund_payout_unavailable");
        // Documents admits this actor's private draft under current source/ownership rules.
        // Financial preview, recording and recovery always reconstruct the complete money proof.
        var approval = documentAccess ? await approvals.PayoutEvidenceSourceAsync(actor, authority, tx, draft["financialApprovalId"].AsInt64, ct) : null;
        var frame = await PrepareAsync(actor, authority, tx, draft["financialApprovalId"].AsInt64, B.Input(draft["input"].AsBsonDocument), ct, approval);
        R.Equal(draft["source"], frame.Source); return (draft, frame);
    }
    public async Task<BsonDocument> ReadDraftAsync(ActorContext actor, long draftId, CancellationToken ct = default) {
        if (draftId <= 0) throw RefundPayoutPolicy.Invalid(); await StorageAsync(ct); await PaymentAllocationStorage.RequireSettlementsAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var (draft, frame) = await DraftFrameAsync(actor, authority, tx, draftId, token);
            var payout = await SavedAsync(frame, token); if (payout is not null && payout["draftId"] != draft["_id"]) payout = null;
            var view = View(frame, payout); view.Add("draft", B.DraftResult(draft));
            var settlement = payout is null ? null : await SettlementSavedAsync(tx, payout["_id"].AsInt64, token);
            view["settlement"] = settlement is null ? BsonNull.Value : RefundSettlementBson.Result(settlement);
            view.Add("evidence", payout is null ? BsonNull.Value : payout["evidence"].DeepClone());
            view.Add("reservationId", payout is null ? BsonNull.Value : payout["reservationId"]);
            await records.TouchAsync(tx, B.Drafts, draft, token); await FenceAsync(frame, token); return view;
        }, ct);
    }
    public async Task<RefundPayoutDraftPreview> PreviewRecordAsync(ActorContext actor, long draftId, long expectedRevision, long reservationId, EvidenceRef reference, CancellationToken ct = default) {
        RecordInput(draftId, expectedRevision, reservationId, reference); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var (draft, frame) = await DraftFrameAsync(actor, authority, tx, draftId, token);
            if (!draft["recordedPayoutId"].IsBsonNull || await SavedAsync(frame, token) is not null) throw B.Changed("refund_payout_already_recorded");
            var view = View(frame); view.Add("draft", B.DraftResult(draft)); view.Add("reservationId", reservationId); view.Add("evidence", PaymentSubmissionBson.Reference(reference));
            await records.TouchAsync(tx, B.Drafts, draft, token); await FenceAsync(frame, token);
            return new RefundPayoutDraftPreview(view, B.RecordBinding(draft["snapshotHash"].AsString, expectedRevision, reservationId, reference));
        }, ct);
    }
    private static void RecordInput(long draftId, long revision, long reservationId, EvidenceRef reference) {
        if (draftId <= 0 || revision != 1 || reservationId <= 0 || reference is null) throw RefundPayoutPolicy.Invalid();
    }
    internal async Task<BsonDocument> RecordVerifiedAsync(ActorContext actor, long draftId, long expectedRevision, long reservationId, EvidenceRef reference, string binding,
        AuthorizationCommandMetadata metadata, Func<IClientSessionHandle, long, CancellationToken, Task<Action>> pin, CancellationToken ct) {
        Metadata(metadata); RecordInput(draftId, expectedRevision, reservationId, reference);
        if (binding is null || binding.Length != 64) throw RefundPayoutPolicy.Invalid(); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Payouts, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value; var pinId = (await ids.NextAsync("evidencePins", ct)).Value;
        var body = B.RecordBody(draftId, expectedRevision, reservationId, reference, binding); var bodyHash = AuthorizationStore.CommandHash(body, metadata.Reason);
        Frame? frame = null; BsonDocument? draft = null, existing = null; Action? pinFinal = null;
        return await authorization.ExecuteCommandAsync(actor, B.Operation, metadata, body, async (authority, tx, token) => {
            (draft, frame) = await DraftFrameAsync(actor, authority, tx, draftId, token); pinFinal = null;
            existing = await records.ForKeyAsync(tx, actor.UserId, B.Operation, metadata.IdempotencyKey, token);
            return Command(frame);
        }, async (tx, token) => {
            if (existing is not null) throw B.Broken(); var current = frame!; var exactDraft = draft!;
            R.Equal(binding, B.RecordBinding(exactDraft["snapshotHash"].AsString, expectedRevision, reservationId, reference));
            if (!exactDraft["recordedPayoutId"].IsBsonNull || await SavedAsync(current, token) is not null) throw B.Changed("refund_payout_already_recorded");
            await FenceAsync(current, token);
            var now = Now;
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "operation", B.Operation }, { "state", "recorded_unverified" },
                { "draftId", draftId }, { "draftHash", exactDraft["snapshotHash"] }, { "draftRevision", expectedRevision }, { "binding", binding }, { "financialApprovalId", exactDraft["financialApprovalId"] },
                { "approvalHash", exactDraft["approvalHash"] }, { "caseId", exactDraft["caseId"] }, { "returnId", exactDraft["returnId"] }, { "invoiceId", exactDraft["invoiceId"] },
                { "party", exactDraft["party"] }, { "scope", exactDraft["scope"] }, { "scopes", exactDraft["scopes"] }, { "source", exactDraft["source"] }, { "input", exactDraft["input"] },
                { "people", new BsonArray(current.People.Evidence.OrderBy(p => p.UserId).Select(ReceivingAccountBson.Person)) }, { "evidence", PaymentSubmissionBson.Reference(reference) },
                { "reservationId", reservationId }, { "pinId", pinId }, { "recordedBy", actor.UserId }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                { "recordedAt", now.UtcDateTime }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", bodyHash }, { "fence", 0L }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
            row.Add("snapshotHash", B.Hash(row));
            await records.Rows(B.Payouts).InsertOneAsync(tx, row, cancellationToken: token);
            await EffectsAsync(tx, row, B.Result(row), B.Audit(row), now, actor.UserId, token);
            pinFinal = await pin(tx, id, token);
            var finalDraft = await records.DraftAsync(tx, draftId, token);
            R.Equal(finalDraft["snapshotHash"], exactDraft["snapshotHash"]);
            if (!finalDraft["recordedPayoutId"].IsBsonNull || (await records.Rows(B.Drafts).UpdateOneAsync(tx, finalDraft, Builders<BsonDocument>.Update.Set("recordedPayoutId", id).Inc("fence", 1L), cancellationToken: token)).ModifiedCount != 1) throw R.Changed();
            return B.Result(row);
        }, ct, async (_, tx, token) => {
            var row = await records.ForKeyAsync(tx, actor.UserId, B.Operation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            R.Equal(row["bodyHash"], bodyHash); R.Equal(row["draftId"], draftId); R.Equal(row["source"], frame!.Source);
            if (existing is not null) await FenceAsync(frame, token);
            Current(frame); pinFinal?.Invoke();
        }, validateCurrent: () => { Current(frame!); pinFinal?.Invoke(); });
    }
    internal async Task<BsonDocument?> RecoverRecordBodyAsync(ActorContext actor, long draftId, string key, string? bodyHash, CancellationToken ct) {
        if (draftId <= 0 || !ReceivingAccountPolicy.Text(key, 128) || key.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.' or ':'))) throw RefundPayoutPolicy.Invalid();
        await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var (draft, frame) = await DraftFrameAsync(actor, authority, tx, draftId, token);
            var row = await records.ForKeyAsync(tx, actor.UserId, B.Operation, key, token);
            if (row is not null) {
                R.Equal(row["draftId"], draftId); R.Equal(row["source"], frame.Source);
                if (bodyHash is not null && row["bodyHash"] != bodyHash) throw new PersistenceConflictException("idempotency_key_reused", "The payout key belongs to another exact request");
            }
            await records.TouchAsync(tx, B.Drafts, draft, token); await FenceAsync(frame, token); return row is null ? null : B.Result(row);
        }, ct);
    }
    public Task<BsonDocument?> RecoverRecordAsync(ActorContext actor, long draftId, string key, CancellationToken ct = default) => RecoverRecordBodyAsync(actor, draftId, key, null, ct);
}
