using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Platform.Documents;

internal sealed partial class EvidenceService
{
    internal Task<PaymentExtractionView> ReadPaymentExtractionAsync(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target,
        long expectedRevision, EvidenceRef reference, AuthorizationReadMetadata metadata, IPaymentOcrSource source, CancellationToken ct) => GuardAsync(tx, () =>
        WithReadTargetAsync(tx, actor, target, expectedRevision, reference, async (owner, token) => {
            if (metadata.Purpose != "payment-ocr-view" || string.IsNullOrWhiteSpace(metadata.CorrelationId) || metadata.CorrelationId.Length > 128 || metadata.CorrelationId.Any(char.IsControl) ||
                owner.SubmittedSubmissionId is not { } submissionId || owner.SubmittedReservationId is not { } reservationId) throw Invalid();
            var job = await Rows("outboxEvents").Find(tx, new BsonDocument { { "kind", "payment.ocr.requested" }, { "payload.submissionId", submissionId } }).SingleOrDefaultAsync(token)
                ?? throw ExtractionRecords.Broken();
            // This never opens the original. It applies the current owning access, original eligibility and exact pin checks to its private derivative.
            var original = await ProcessingOriginalAsync(tx, source, job, clock.UtcNow.AddSeconds(5), token);
            if (original.Source.Evidence != reference || original.Source.ReservationId != reservationId || original.Source.DeclarationId != target.Id || original.Source.UploaderId != actor.UserId)
                throw Invalid();
            var row = await new ExtractionRecords(database).ResultAsync(tx, job, token);
            if (row is not null) {
                var historical = row["binding"].DeepClone().AsBsonDocument; var current = original.Binding.DeepClone().AsBsonDocument;
                // Approvals may renew while immutable evidence stays identical. Eligibility above checks the new approvals.
                foreach (var key in new[] { "readPolicyId", "readPolicyRevision", "validationPolicyId", "validationPolicyRevision" }) { historical.Remove(key); current.Remove(key); }
                if (!historical.Equals(current)) throw ExtractionRecords.Broken();
            }
            var state = row is not null ? "succeeded" : job["status"].AsString switch { "pending" => "queued", "leased" => "running", "retry" => "retry", "dead" => "dead", _ => throw ExtractionRecords.Broken() };
            await Rows("documentExtractionReadAudits").InsertOneAsync(tx, new BsonDocument { { "_id", (await ids.NextAsync("documentExtractionReadAudits", token)).Value },
                { "schemaVersion", 1 }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "outboxId", job["_id"] }, { "submissionId", submissionId },
                { "reference", ReadReference(reference) }, { "resultId", row is null ? BsonNull.Value : row["_id"] }, { "resultHash", row is null ? BsonNull.Value : row["resultHash"] },
                { "state", state }, { "purpose", metadata.Purpose }, { "correlationId", metadata.CorrelationId }, { "createdAt", clock.UtcNow.UtcDateTime } }, cancellationToken: token);
            FinalCheck(original.Until, token);
            return new PaymentExtractionView(target.Id, submissionId, reference, state, row?["_id"].AsInt64, row is null ? null : Date(row["createdAt"]), row is null ? null : ExtractionRecords.Extract(row));
        }, ct));
}
