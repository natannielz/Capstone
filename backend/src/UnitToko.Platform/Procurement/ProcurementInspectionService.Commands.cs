using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Procurement.ProcurementInspectionBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementInspectionService
{
    private async Task<BsonDocument?> ExistingAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string operation, string key, CancellationToken ct) {
        _ = Query(actor, authority, Permissions.ProcurementInspect); _ = Query(actor, authority, Permissions.ProcurementRead);
        var table = operation == B.DraftOperation ? B.Drafts : B.Submissions;
        var row = await Rows(table).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) _ = ContextFor(actor, authority, tx, R.Location(row["location"]), Permissions.ProcurementInspect);
        else foreach (var (name, field, keyField) in new[] { ("auditEvents", "kind", "requestKey"), ("outboxEvents", "kind", "requestKey"), ("idempotencyRecords", "operation", "key") })
            if (await Rows(name).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { field, operation }, { keyField, key } }).AnyAsync(ct)) throw B.Broken();
        return row;
    }
    private static BsonDocument PreviewView(ProcurementInspectionInput input, ProcurementInspectionHistory history) {
        var view = B.SourceView(history.Source); view.Add("input", B.Input(input)); view.Add("capacity", history.Capacity); return view;
    }
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, ProcurementInspectionInput input, CancellationToken ct = default) {
        input = ProcurementInspectionRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await IntakeContextAsync(actor, authority, tx, input.NoticeId, input.IntakeId, Permissions.ProcurementInspect, token);
            ProcurementInspectionRecords.Review(input, context.History!.Source, Now); _ = B.After(context.History.Capacity, input.InspectedContainers);
            var result = PreviewView(input, context.History); result.Add("binding", B.Binding(actor.UserId, actor.GrantVersion, input, context.History.Source, context.History.Capacity)); result.Add("observedAt", Now.UtcDateTime);
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> DraftAsync(ActorContext actor, ProcurementInspectionInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = ProcurementInspectionRules.Copy(input); Binding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Drafts, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value; var body = B.DraftBody(input, binding);
        Context? context = null; BsonDocument? fresh = null;
        return await authorization.ExecuteCommandAsync(actor, B.DraftOperation, metadata, body, async (authority, tx, token) => {
            fresh = null; var saved = await ExistingAsync(actor, authority, tx, B.DraftOperation, metadata.IdempotencyKey, token);
            context = saved is null ? await IntakeContextAsync(actor, authority, tx, input.NoticeId, input.IntakeId, Permissions.ProcurementInspect, token)
                : await DraftContextAsync(actor, authority, tx, saved["draftId"].AsInt64, Permissions.ProcurementInspect, false, token);
            if (saved is null) {
                ProcurementInspectionRecords.Review(input, context.History!.Source, Now); _ = B.After(context.History.Capacity, input.InspectedContainers);
                ProcurementInspectionRecords.Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, input, context.History.Source, context.History.Capacity));
            }
            return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); Current(active); var history = active.History!; var at = Now;
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "draftId", id }, { "version", 1L }, { "noticeId", input.NoticeId }, { "intakeId", input.IntakeId },
                { "location", R.Location(input.Location) }, { "source", history.Source }, { "input", B.Input(input) }, { "capacity", history.Capacity },
                { "binding", binding }, { "people", new BsonArray(active.People!.Evidence.Select(R.Person)) }, { "submittedInspectionId", BsonNull.Value }, { "fence", 0L } };
            R.Metadata(row, B.DraftOperation, audit, actor, metadata, body, at); row["snapshotHash"] = B.DraftHash(row);
            var head = history.Head;
            if (head is null) {
                head = new() { { "_id", input.IntakeId }, { "schemaVersion", 1 }, { "noticeId", input.NoticeId }, { "intakeId", input.IntakeId }, { "location", R.Location(input.Location) },
                    { "intakeHash", history.Source["intake"]["snapshotHash"] }, { "lastInspectionId", BsonNull.Value }, { "lastHash", BsonNull.Value }, { "createdAt", at.UtcDateTime }, { "updatedAt", at.UtcDateTime }, { "fence", 0L } };
                head.AddRange(history.Capacity); await Rows(B.Heads).InsertOneAsync(tx, head, cancellationToken: token);
            }
            await Rows(B.Drafts).InsertOneAsync(tx, row, cancellationToken: token); await EffectsAsync(tx, row, token);
            active.History = history with { Head = head, Drafts = [.. history.Drafts, row] }; active.Draft = row; fresh = row; return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var active = context ?? throw B.Broken(); var row = active.Draft ?? throw B.Broken();
            if (fresh is not null) {
                _ = await records.RowAsync(tx, B.Drafts, row["_id"].AsInt64, token); await records.DraftAsync(tx, row, active.History!.Source, token);
                _ = await records.RowAsync(tx, B.Heads, input.IntakeId, token); await records.OrphansAsync(tx, token);
            }
            ProcurementInspectionRecords.Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason)); await FenceAsync(active, token);
            if (fresh is not null) await new ProcurementStockSourceCoverage(database, ids, clock).RecordAsync(tx, "inspectionDraft", row, token);
        }, validateCurrent: () => Current(context ?? throw B.Broken()));
    }
    public async Task<BsonDocument> PreviewSubmitAsync(ActorContext actor, long draftId, long expectedRevision, long reservationId, EvidenceRef reference, CancellationToken ct = default) {
        Revision(draftId, expectedRevision, reservationId); if (reference is null) throw ProcurementInspectionRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await DraftContextAsync(actor, authority, tx, draftId, Permissions.ProcurementInspect, true, token); var history = context.History!; var draft = context.Draft!;
            _ = B.After(history.Capacity, draft["input"]["inspectedContainers"].AsInt32);
            var result = new BsonDocument { { "draft", B.DraftView(draft) }, { "capacity", history.Capacity },
                { "binding", B.SubmitBinding(actor.UserId, actor.GrantVersion, draft["snapshotHash"].AsString, reservationId, reference, history.Capacity) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    internal async Task<BsonDocument> SubmitWithProofAsync(ActorContext actor, long draftId, long expectedRevision, long reservationId, EvidenceRef reference, string binding,
        AuthorizationCommandMetadata metadata, Func<IClientSessionHandle, long, IProcurementInspectionEvidenceUploadTarget, CancellationToken, Task<Action>> pin, CancellationToken ct) {
        Revision(draftId, expectedRevision, reservationId); if (reference is null || pin is null) throw ProcurementInspectionRules.Invalid(); Binding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Submissions, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value; var pinId = (await ids.NextAsync("evidencePins", ct)).Value;
        var body = B.SubmitBody(draftId, expectedRevision, reservationId, reference, binding); Context? context = null; BsonDocument? fresh = null; Action? finalPin = null;
        return await authorization.ExecuteCommandAsync(actor, B.SubmitOperation, metadata, body, async (authority, tx, token) => {
            fresh = null; finalPin = null; var saved = await ExistingAsync(actor, authority, tx, B.SubmitOperation, metadata.IdempotencyKey, token);
            context = await DraftContextAsync(actor, authority, tx, draftId, Permissions.ProcurementInspect, saved is null, token);
            if (saved is null) {
                _ = B.After(context.History!.Capacity, context.Draft!["input"]["inspectedContainers"].AsInt32);
                ProcurementInspectionRecords.Equal(binding, B.SubmitBinding(actor.UserId, actor.GrantVersion, context.Draft["snapshotHash"].AsString, reservationId, reference, context.History.Capacity));
            }
            return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); Current(active); var history = active.History!; var draft = active.Draft!; var head = history.Head ?? throw B.Broken(); var at = Now;
            var after = B.After(history.Capacity, draft["input"]["inspectedContainers"].AsInt32);
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "draftId", draftId }, { "version", 1L }, { "draftHash", draft["snapshotHash"] },
                { "noticeId", draft["noticeId"] }, { "intakeId", draft["intakeId"] }, { "location", draft["location"] }, { "source", draft["source"] }, { "input", draft["input"] },
                { "before", history.Capacity }, { "after", after }, { "sequence", after["version"] }, { "previousInspectionId", head["lastInspectionId"] }, { "previousHash", head["lastHash"] },
                { "binding", binding }, { "people", new BsonArray(active.People!.Evidence.Select(R.Person)) }, { "state", "submitted_quarantine" },
                { "reservationId", reservationId }, { "evidence", PaymentSubmissionBson.Reference(reference) }, { "pinId", pinId } };
            R.Metadata(row, B.SubmitOperation, audit, actor, metadata, body, at);
            await Rows(B.Submissions).InsertOneAsync(tx, row, cancellationToken: token); await EffectsAsync(tx, row, token);
            if ((await Rows(B.Drafts).UpdateOneAsync(tx, draft, Builders<BsonDocument>.Update.Set("submittedInspectionId", id), cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
            draft["submittedInspectionId"] = id;
            var next = head.DeepClone().AsBsonDocument; foreach (var field in after) next[field.Name] = field.Value;
            next["lastInspectionId"] = id; next["lastHash"] = row["snapshotHash"]; next["updatedAt"] = at.UtcDateTime;
            if ((await Rows(B.Heads).ReplaceOneAsync(tx, head, next, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
            active.History = history with { Head = next, Capacity = after, Submissions = [.. history.Submissions, row] }; active.Submission = row; fresh = row; return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var active = context ?? throw B.Broken(); var row = active.Submission ?? throw B.Broken();
            ProcurementInspectionRecords.Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            if (fresh is not null) {
                _ = await records.RowAsync(tx, B.Submissions, id, token); _ = await records.RowAsync(tx, B.Heads, row["intakeId"].AsInt64, token);
                await records.SubmissionAsync(tx, row, active.Draft!, active.History!.Source, true, token, completedReceipt: true);
                await records.OrphansAsync(tx, token);
                finalPin = await pin(tx, id, new SubmittingEvidenceTarget(this, active, id, row["snapshotHash"].AsString), token);
                await records.RequirePinAsync(tx, row, token);
            }
            await FenceAsync(active, token); finalPin?.Invoke();
            if (fresh is not null) await new ProcurementStockSourceCoverage(database, ids, clock).RecordAsync(tx, "inspection", row, token);
        }, validateCurrent: () => { Current(context ?? throw B.Broken()); finalPin?.Invoke(); });
    }
    internal async Task<BsonDocument?> RecoverSubmitBodyAsync(ActorContext actor, long draftId, string key, string bodyHash, CancellationToken ct) {
        if (draftId <= 0) throw ProcurementInspectionRules.Invalid(); Key(key); Binding(bodyHash); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var row = await ExistingAsync(actor, authority, tx, B.SubmitOperation, key, token);
            if (row is null) { await FenceQueryAsync(actor, authority, tx, Permissions.ProcurementInspect, token); return null; }
            if (row["draftId"] != draftId || row["bodyHash"] != bodyHash) throw new PersistenceConflictException("idempotency_key_reused", "Idempotency key was used with a different request");
            var context = await DraftContextAsync(actor, authority, tx, draftId, Permissions.ProcurementInspect, false, token);
            if (context.Submission is null || context.Submission["_id"] != row["_id"]) throw B.Broken(); await FenceAsync(context, token); return B.Result(context.Submission);
        }, ct);
    }
}
