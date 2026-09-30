using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Billing;

public sealed record PaymentFollowUpView(long DeclarationId, long DeclarationRevision, long SubmissionId, EvidenceRef Evidence,
    long ReviewRevision, string State, DateTimeOffset SubmittedAt, DateTimeOffset? RecordedAt, PaymentNotificationStatus Notification);
public sealed partial class PaymentDraftService
{
    public async Task<PaymentFollowUpView> ReadFollowUpAsync(ActorContext actor, long id, long revision, CancellationToken ct = default) {
        if (id <= 0 || revision <= 0) throw PaymentDraftBson.Invalid(); await StorageAsync(ct);
        await InventoryRepository.RequireStorageAsync(database, PaymentReviewActionRecords.Versions, PaymentReviewActionSchemas.Validator(), PaymentReviewActionSchemas.Key(), ct);
        await PaymentReviewNotifications.RequireStorageAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var head = await HeadAsync(actor, authority, tx, id, token);
            if (head["state"] != "submitted" || head["revision"] != revision) throw PaymentDraftRecords.Changed();
            var version = await records.VersionAsync(tx, head["versionId"].AsInt64, token);
            var submission = await new PaymentSubmissionRecords(database).ReadAsync(tx, head["submissionId"].AsInt64, token);
            var latest = await new PaymentReviewActionRecords(database).LatestAsync(tx, id, PaymentDraftBson.SubmittedSource(head, version, submission), token);
            // Buyer follow-up exposes no internal reasons, staff identities or bank/payer records, and makes no financial-status claim.
            var view = new PaymentFollowUpView(id, revision, submission["_id"].AsInt64, PaymentSubmissionBson.Reference(submission["evidence"].AsBsonDocument),
                latest?["revision"].AsInt64 ?? 0, latest?["state"].AsString ?? "pending", new(submission["submittedAt"].ToUniversalTime()),
                latest is null ? null : new(latest["recordedAt"].ToUniversalTime()), await PaymentReviewNotifications.ReadAsync(database, tx, latest, token));
            await records.Rows("auditEvents").InsertOneAsync(tx, new BsonDocument { { "_id", (await ids.NextAsync("auditEvents", token)).Value },
                { "schemaVersion", 1 }, { "kind", "billing.payment.follow-up.read" }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                { "declarationId", id }, { "declarationRevision", revision }, { "reviewRevision", view.ReviewRevision }, { "createdAt", Now.UtcDateTime } }, cancellationToken: token);
            await records.TouchAsync(tx, head, token); await FenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, token); return view;
        }, ct);
    }
    internal IMongoDatabase VerificationDatabase => database;
    internal Task VerificationStorageAsync(CancellationToken ct) => CorrectionStorageAsync(ct);
    internal async Task<BsonDocument> VerificationReviewAsync(IClientSessionHandle tx, PaymentExtractionView extraction, long revision, CancellationToken ct) {
        if (extraction.State != "succeeded" || extraction.ResultId is not { } extractionId) throw PaymentVerificationBson.Changed("payment_verification_ocr_required");
        var binding = new PaymentCorrectionBinding(revision, extraction.SubmissionId, extraction.Evidence, extractionId);
        var original = Original(extraction, binding);
        var history = await new PaymentCorrectionRecords(database).HistoryAsync(tx, extraction.DeclarationId, binding, PaymentCorrectionBson.Fields(original), ct);
        await new PaymentCorrectionRecords(database).TouchAsync(tx, history.Head, ct);
        var latest = history.Recent.FirstOrDefault();
        return new() { { "binding", PaymentCorrectionBson.Binding(binding) }, { "revision", history.Head?["revision"] ?? new BsonInt64(0) },
            { "versionId", latest?["_id"] ?? BsonNull.Value }, { "versionHash", latest?["snapshotHash"] ?? BsonNull.Value },
            { "originalFields", PaymentCorrectionBson.Fields(original) }, { "fields", latest?["fields"].DeepClone() ?? PaymentCorrectionBson.Fields(original) } };
    }
    internal async Task<BsonDocument> VerificationSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long id, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw PaymentDraftBson.Invalid();
        var head = await ReviewHeadAsync(actor, authority, tx, id, ct);
        var version = await records.VersionAsync(tx, head["versionId"].AsInt64, ct);
        var submission = await new PaymentSubmissionRecords(database).ReadAsync(tx, head["submissionId"].AsInt64, ct);
        await records.TouchAsync(tx, head, ct); await ReviewFenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, ct);
        return PaymentDraftBson.SubmittedSource(head, version, submission);
    }
}
