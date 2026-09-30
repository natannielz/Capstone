using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentDraftService
{
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
        return new() { { "declarationId", head["_id"] }, { "revision", head["revision"] }, { "versionId", version["_id"] },
            { "versionHash", version["snapshotHash"] }, { "submissionId", submission["_id"] }, { "submissionHash", submission["snapshotHash"] },
            { "recordedBy", submission["recordedBy"] }, { "uploaderId", submission["uploaderId"] }, { "reservationId", submission["reservationId"] },
            { "evidence", submission["evidence"].DeepClone() }, { "input", version["input"].DeepClone() }, { "invoices", version["invoices"].DeepClone() },
            { "party", head["party"].DeepClone() }, { "scopes", head["scopes"].DeepClone() } };
    }
}
