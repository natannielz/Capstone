using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierDocumentService
{
    private static string Permission(string operation) => operation == B.MatchOperation ? Permissions.ProcurementSupplierDocumentMatch : Permissions.ProcurementSupplierDocumentRecord;
    private async Task<BsonDocument?> ExistingAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string operation, string key, CancellationToken ct) {
        _ = Query(actor, authority, Permission(operation)); _ = Query(actor, authority, Permissions.ProcurementRead);
        var table = operation == B.DraftOperation ? B.Drafts : operation == B.SubmitOperation ? B.Submissions : B.Matches;
        var row = await Rows(table).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) _ = ContextFor(actor, authority, tx, R.Location(row["location"]), Permission(operation));
        else foreach (var (name, field, keyField) in new[] { ("auditEvents", "kind", "requestKey"), ("outboxEvents", "kind", "requestKey"), ("idempotencyRecords", "operation", "key") })
            if (await Rows(name).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { field, operation }, { keyField, key } }).AnyAsync(ct)) throw B.Broken();
        return row;
    }
    private async Task<BsonDocument> IdentityStateAsync(Context context, SupplierDocumentInput input, CancellationToken ct) {
        var key = B.IdentityKey(input, context.Source!["proposal"]["input"]["supplierId"].AsInt64);
        var raw = await Rows(B.Identities).Find(context.Tx, new BsonDocument("identityKey", key)).SingleOrDefaultAsync(ct);
        long version = 0;
        if (raw is not null) {
            var first = await records.DraftAsync(context.Tx, raw["firstDraftId"].AsInt64, ct); var identity = await records.IdentityAsync(context.Tx, first, ct);
            version = identity["version"].AsInt64; context.ExtraFences.Add((B.Identities, identity));
        } else if (await Rows(B.Drafts).Find(context.Tx, new BsonDocument { { "supplierId", context.Source["proposal"]["input"]["supplierId"] }, { "input.kind", B.Kind(input.Kind) },
            { "input.documentReference", input.DocumentReference }, { "location.organizationId", input.Location.OrganizationId }, { "location.shopId", input.Location.ShopId }, { "location.channelId", input.Location.ChannelId } }).AnyAsync(ct)) throw B.Broken();
        return new() { { "identityKey", key }, { "identityVersion", version } };
    }
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, SupplierDocumentInput input, CancellationToken ct = default) {
        input = SupplierDocumentRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SourceContextAsync(actor, authority, tx, input, Permissions.ProcurementSupplierDocumentRecord, token);
            if (input.DocumentDate is { } date && date > AccountingCalendar.BusinessDate(Now)) throw B.Changed(); var state = await IdentityStateAsync(context, input, token);
            var result = new BsonDocument { { "input", B.Input(input) }, { "order", PurchaseOrderService.FundingOrderView(context.Source!) }, { "supplierId", context.Source!["proposal"]["input"]["supplierId"] },
                { "possibleDuplicate", state["identityVersion"].AsInt64 > 0 }, { "binding", B.Binding(actor.UserId, actor.GrantVersion, B.Input(input), context.Source, state) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> DraftAsync(ActorContext actor, SupplierDocumentInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = SupplierDocumentRules.Copy(input); Binding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Drafts, ct)).Value; var identityId = (await ids.NextAsync(B.Identities, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value;
        Context? context = null; BsonDocument? state = null; var body = B.DraftBody(input, binding);
        return await authorization.ExecuteCommandAsync(actor, B.DraftOperation, metadata, body, async (authority, tx, token) => {
            var existing = await ExistingAsync(actor, authority, tx, B.DraftOperation, metadata.IdempotencyKey, token);
            context = existing is null ? await SourceContextAsync(actor, authority, tx, input, Permissions.ProcurementSupplierDocumentRecord, token) : await DraftContextAsync(actor, authority, tx, existing["_id"].AsInt64, Permissions.ProcurementSupplierDocumentRecord, false, token);
            if (existing is null) { if (input.DocumentDate is { } date && date > AccountingCalendar.BusinessDate(Now)) throw B.Changed(); state = await IdentityStateAsync(context, input, token); SupplierDocumentRecords.Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, B.Input(input), context.Source!, state)); }
            return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); Current(active); var at = Now; var original = active.ExtraFences.SingleOrDefault(pair => pair.Table == B.Identities).Row;
            var identity = original is null ? new BsonDocument { { "_id", identityId }, { "schemaVersion", 1 }, { "identityKey", state!["identityKey"] }, { "organizationId", input.Location.OrganizationId },
                { "shopId", input.Location.ShopId }, { "channelId", input.Location.ChannelId }, { "supplierId", active.Source!["proposal"]["input"]["supplierId"] }, { "kind", B.Kind(input.Kind) }, { "reference", input.DocumentReference },
                { "version", 1L }, { "draftCount", 1L }, { "firstDraftId", id }, { "fence", 0L }, { "createdAt", at.UtcDateTime }, { "updatedAt", at.UtcDateTime } } : original.DeepClone().AsBsonDocument;
            if (original is not null) { identity["version"] = checked(identity["version"].AsInt64 + 1); identity["draftCount"] = identity["version"]; identity["updatedAt"] = at.UtcDateTime; }
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "draftId", id }, { "version", 1L }, { "location", R.Location(input.Location) }, { "supplierId", identity["supplierId"] },
                { "input", B.Input(input) }, { "source", active.Source }, { "identityId", identity["_id"] }, { "identityVersion", identity["version"] }, { "binding", binding },
                { "people", new BsonArray(active.People!.Evidence.Select(R.Person)) }, { "submittedDocumentId", BsonNull.Value }, { "fence", 0L } };
            R.Metadata(row, B.DraftOperation, audit, actor, metadata, body, at); row["snapshotHash"] = B.DraftHash(row);
            var capacity = B.Capacity(row["input"]["lines"].AsBsonArray); var head = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "draftId", id }, { "documentId", BsonNull.Value }, { "draftHash", row["snapshotHash"] },
                { "location", row["location"] }, { "version", 0L }, { "lines", capacity["lines"] }, { "reviewedInput", BsonNull.Value }, { "lastMatchId", BsonNull.Value }, { "lastHash", BsonNull.Value }, { "fence", 0L }, { "createdAt", at.UtcDateTime }, { "updatedAt", at.UtcDateTime } };
            if (original is null) await Rows(B.Identities).InsertOneAsync(tx, identity, cancellationToken: token); else if ((await Rows(B.Identities).ReplaceOneAsync(tx, original, identity, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
            await Rows(B.Drafts).InsertOneAsync(tx, row, cancellationToken: token); await Rows(B.Heads).InsertOneAsync(tx, head, cancellationToken: token); await EffectsAsync(tx, row, token);
            active.ExtraFences.RemoveAll(pair => pair.Table == B.Identities); active.Draft = row; active.History = new(row, null, head, identity, capacity, []); return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var active = context ?? throw B.Broken(); var actual = await records.HistoryAsync(tx, active.Draft!["_id"].AsInt64, token); active.History = actual; active.Draft = actual.Draft;
            SupplierDocumentRecords.Equal(active.Draft["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason)); await FenceAsync(active, token);
        }, validateCurrent: () => Current(context ?? throw B.Broken()));
    }
    public async Task<BsonDocument> PreviewSubmitAsync(ActorContext actor, long draftId, long expectedRevision, long reservationId, EvidenceRef reference, CancellationToken ct = default) {
        Revision(draftId, expectedRevision, reservationId); await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await DraftContextAsync(actor, authority, tx, draftId, Permissions.ProcurementSupplierDocumentRecord, true, token);
            var result = new BsonDocument { { "draft", B.DraftView(context.Draft!) }, { "binding", B.SubmitBinding(actor.UserId, actor.GrantVersion, context.Draft!["snapshotHash"].AsString, reservationId, reference) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    internal Task<BsonDocument> SubmitWithProofAsync(ActorContext actor, long draftId, long expectedRevision, long reservationId, EvidenceRef reference, string binding, AuthorizationCommandMetadata metadata,
        Func<IClientSessionHandle, long, ISupplierDocumentEvidenceUploadTarget, CancellationToken, Task<Action>> pin, CancellationToken ct) =>
        SubmitWithProofAsync(actor, draftId, expectedRevision, reservationId, reference, binding, metadata, null, pin, ct);
    internal async Task<BsonDocument> SubmitWithProofAsync(ActorContext actor, long draftId, long expectedRevision, long reservationId, EvidenceRef reference, string binding, AuthorizationCommandMetadata metadata,
        VerifiedPdfSubmissionOriginal? pdfOriginal,
        Func<IClientSessionHandle, long, ISupplierDocumentEvidenceUploadTarget, CancellationToken, Task<Action>> pin, CancellationToken ct) {
        Revision(draftId, expectedRevision, reservationId); Binding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Submissions, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value; var pinId = (await ids.NextAsync("evidencePins", ct)).Value;
        var body = B.SubmitBody(draftId, expectedRevision, reservationId, reference, binding); Context? context = null; BsonDocument? fresh = null; Action? finalPin = null;
        return await authorization.ExecuteCommandAsync(actor, B.SubmitOperation, metadata, body, async (authority, tx, token) => {
            fresh = null; finalPin = null; var existing = await ExistingAsync(actor, authority, tx, B.SubmitOperation, metadata.IdempotencyKey, token);
            context = await DraftContextAsync(actor, authority, tx, draftId, Permissions.ProcurementSupplierDocumentRecord, existing is null, token);
            if (existing is null) SupplierDocumentRecords.Equal(binding, B.SubmitBinding(actor.UserId, actor.GrantVersion, context.Draft!["snapshotHash"].AsString, reservationId, reference));
            return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); Current(active); var draft = active.Draft!; var at = Now;
            var actualOriginal = await new EvidenceSubmissionOriginalRecords(database).ReadAsync(tx,
                new(EvidenceTargetKind.SupplierDocument, draftId), expectedRevision, reservationId, reference, actor.UserId, token);
            if (actualOriginal is null ? pdfOriginal is not null : pdfOriginal is null) throw B.Broken();
            if (actualOriginal is not null) {
                pdfOriginal!.Require(actor, new(EvidenceTargetKind.SupplierDocument, draftId), expectedRevision, reservationId, reference, actualOriginal, clock);
                await PdfDocumentExtractionRecords.RequireStorageAsync(database, token);
            }
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "draftId", draftId }, { "version", 1L }, { "draftHash", draft["snapshotHash"] },
                { "location", draft["location"] }, { "supplierId", draft["supplierId"] }, { "input", draft["input"] }, { "source", draft["source"] }, { "identityId", draft["identityId"] },
                { "binding", binding }, { "people", new BsonArray(active.People!.Evidence.Select(R.Person)) }, { "reservationId", reservationId }, { "evidence", PaymentSubmissionBson.Reference(reference) }, { "pinId", pinId } };
            R.Metadata(row, B.SubmitOperation, audit, actor, metadata, body, at); await Rows(B.Submissions).InsertOneAsync(tx, row, cancellationToken: token); await EffectsAsync(tx, row, token);
            await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
                { "kind", actualOriginal is null ? "supplierdocument.ocr.requested" : PdfDocumentExtractionContract.SupplierJobKind },
                { "sourceKey", actualOriginal is null ? B.OcrKey(row) : B.DocumentOcrKey(row) }, { "payload", actualOriginal is null ? B.OcrPayload(row) : B.DocumentOcrPayload(row, actualOriginal) },
                { "createdAt", at.UtcDateTime }, { "dueAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            if ((await Rows(B.Drafts).UpdateOneAsync(tx, draft, Builders<BsonDocument>.Update.Set("submittedDocumentId", id), cancellationToken: token)).ModifiedCount != 1) throw B.Changed(); draft["submittedDocumentId"] = id;
            var head = active.History!.Head; if ((await Rows(B.Heads).UpdateOneAsync(tx, head, Builders<BsonDocument>.Update.Set("documentId", id), cancellationToken: token)).ModifiedCount != 1) throw B.Changed(); head["documentId"] = id;
            active.Submission = row; active.History = active.History with { Document = row }; fresh = row; return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var active = context ?? throw B.Broken(); var row = active.Submission ?? throw B.Broken(); SupplierDocumentRecords.Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            if (fresh is not null) {
                _ = await records.SubmissionAsync(tx, id, token, active.Draft, pendingPin: true); await records.OrphansAsync(tx, token);
                finalPin = await pin(tx, id, new SubmittingEvidenceTarget(this, active, id, row["snapshotHash"].AsString), token); await records.RequirePinAsync(tx, row, token);
            }
            await FenceAsync(active, token); finalPin?.Invoke();
        }, validateCurrent: () => { Current(context ?? throw B.Broken()); finalPin?.Invoke(); });
    }
    internal async Task<BsonDocument?> RecoverSubmitBodyAsync(ActorContext actor, long draftId, string key, string bodyHash, CancellationToken ct) {
        Key(key); Binding(bodyHash); await StorageAsync(ct); return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var row = await ExistingAsync(actor, authority, tx, B.SubmitOperation, key, token); if (row is null) { await FenceQueryAsync(actor, authority, tx, Permissions.ProcurementSupplierDocumentRecord, token); return null; }
            if (row["draftId"] != draftId || row["bodyHash"] != bodyHash) throw new PersistenceConflictException("idempotency_key_reused", "Idempotency key was used with a different request");
            var context = await DraftContextAsync(actor, authority, tx, draftId, Permissions.ProcurementSupplierDocumentRecord, false, token); SupplierDocumentRecords.Equal(context.Submission!["_id"], row["_id"]);
            await FenceAsync(context, token); return B.Result(context.Submission);
        }, ct);
    }
}
