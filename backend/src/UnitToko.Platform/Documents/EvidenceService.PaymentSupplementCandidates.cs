using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Platform.Documents;

internal sealed partial class EvidenceService
{
    // Like the existing image-correction consumer, this reads the immutable
    // derivative through the currently eligible, validated/pinned original bridge.
    // It does not open or claim to reread the original file.
    internal Task<Action> RequirePaymentSupplementCandidatesAsync(IClientSessionHandle tx, ActorContext actor,
        PaymentSupplementPrimary primary, long supplementId, ImmutableArray<PaymentSupplementReviewObservation> observations,
        IPaymentSupplementOcrSource source, DateTimeOffset requestDeadline, Action<DateTimeOffset> captureWindow,
        AuthorizationReadMetadata metadata, CancellationToken ct) =>
        GuardAsync(tx, async () => {
            var values = PaymentReviewCorrectionPolicy.CopySupplementaryObservations(observations)!.Value;
            var reference = values[0].Candidate.Evidence;
            if (values.Any(value => value.SupplementId != supplementId || value.SupplementRevision != 2 || value.Candidate.Evidence != reference) ||
                metadata.Purpose != "payment-review-supplement-pdf-ocr-view" || !Text(metadata.CorrelationId, 128)) throw Invalid();
            DateTimeOffset? until = null;
            await WithReadTargetAsync(tx, actor, new(EvidenceTargetKind.PaymentSupplement, supplementId), 2, reference, async (owner, token) => {
                if (owner.Revision != 2 || owner.SupplementId != supplementId || owner.SupplementReservationId is not { } reservationId ||
                    owner.SupplementUploaderId is not { } uploaderId || owner.OwnerApprovalUntil is not { } approvalUntil) throw Invalid();
                var job = await Rows("outboxEvents").Find(tx, new BsonDocument {
                    { "kind", PaymentSupplementBson.JobKind }, { "payload.supplementId", supplementId }
                }).SingleOrDefaultAsync(token) ?? throw PdfDocumentExtractionRecords.Broken();
                // The owning read adapter alone fences D06. Worker reads retain
                // their independent fence; no nested transaction or double fence.
                var original = await PaymentSupplementPdfOriginalAsync(tx, source, job, requestDeadline, false, token);
                if (original.Source.Primary != primary || original.Source.SupplementId != supplementId || original.Source.SupplementRevision != 2 ||
                    original.Source.ReservationId != reservationId || original.Source.UploaderId != uploaderId || original.Source.PdfEvidence != reference)
                    throw Invalid();
                until = original.Until < approvalUntil ? original.Until : approvalUntil;
                // Retain the first (or narrower) budget even if a later fence
                // conflicts and the owning transaction retries.
                captureWindow(until.Value);
                var records = new PdfDocumentExtractionRecords(database);
                // This exact existing result-read audit is mandatory and shares the
                // correction transaction, so a failed audit rolls everything back.
                var view = await records.ReadViewAsync(tx, job, PaymentSupplementBson.SourceKind, original.Binding, original.Publication,
                    actor.UserId, actor.GrantVersion, ids, clock, metadata.Purpose, metadata.CorrelationId, token);
                if (view.State != "succeeded" || view.Extraction is null) throw PdfDocumentExtractionRecords.Broken();
                foreach (var value in values)
                    _ = await records.RequireCandidateAsync(tx, job, PaymentSupplementBson.SourceKind, original.Binding, original.Publication, value.Candidate, token);
                FinalCheck(until, token); return true;
            }, ct);
            if (until is null) throw Invalid();
            void Check() { FinalCheck(until, ct); FinalCheck(requestDeadline, ct); }
            Check(); return (Action)Check;
        });
}
