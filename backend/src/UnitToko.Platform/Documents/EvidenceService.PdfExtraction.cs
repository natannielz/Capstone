using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Platform.Documents;

internal sealed partial class EvidenceService
{
    internal Task<PdfDocumentExtractionView> ReadSupplierDocumentPdfExtractionAsync(IClientSessionHandle tx, ActorContext actor,
        EvidenceTargetRef target, long expectedRevision, EvidenceRef reference, AuthorizationReadMetadata metadata,
        ISupplierDocumentOcrSource source, CancellationToken ct) => GuardAsync(tx, async () => {
            DateTimeOffset? until = null;
            var result = await WithReadTargetAsync(tx, actor, target, expectedRevision, reference, async (owner, token) => {
                if (target.Kind != EvidenceTargetKind.SupplierDocument || metadata.Purpose != "supplier-document-pdf-ocr-view" || !Text(metadata.CorrelationId, 128) ||
                    owner.SupplierDocumentId is not { } submissionId || owner.SupplierDocumentReservationId is not { } reservationId) throw Invalid();
                var job = await Rows("outboxEvents").Find(tx, new BsonDocument { { "kind", PdfDocumentExtractionContract.SupplierJobKind }, { "payload.submissionId", submissionId } })
                    .SingleOrDefaultAsync(token) ?? throw PdfDocumentExtractionRecords.Broken();
                var original = await SupplierPdfDocumentProcessingOriginalAsync(tx, source, job, clock.UtcNow.AddSeconds(5), token);
                if (original.Source.Evidence != reference || original.Source.ReservationId != reservationId || original.Source.DraftId != target.Id ||
                    original.Source.DraftRevision != expectedRevision || original.Source.UploaderId != (owner.SupplierDocumentUploaderId ?? actor.UserId)) throw Invalid();
                var view = await new PdfDocumentExtractionRecords(database).ReadViewAsync(tx, job, "SupplierInvoice", original.Binding, original.Publication,
                    actor.UserId, actor.GrantVersion, ids, clock, metadata.Purpose, metadata.CorrelationId, token);
                until = original.Until; FinalCheck(until, token); return view with { SourceRevision = owner.Revision };
            }, ct);
            FinalCheck(until, ct); return result;
        });

    internal Task<EvidenceSubmissionFormatView> ReadSubmissionFormatAsync(IClientSessionHandle tx, ActorContext actor,
        EvidenceTargetRef target, long expectedRevision, EvidenceRef reference, AuthorizationReadMetadata metadata, string purpose, CancellationToken ct) =>
        GuardAsync(tx, () => WithReadTargetAsync(tx, actor, target, expectedRevision, reference, async (owner, token) => {
            if (metadata.Purpose != purpose || !Text(metadata.CorrelationId, 128)) throw Invalid();
            var reservationId = target.Kind == EvidenceTargetKind.PaymentDeclaration ? owner.SubmittedReservationId : owner.SupplierDocumentReservationId;
            var submissionId = target.Kind == EvidenceTargetKind.PaymentDeclaration ? owner.SubmittedSubmissionId : owner.SupplierDocumentId;
            if (target.Kind is not (EvidenceTargetKind.PaymentDeclaration or EvidenceTargetKind.SupplierDocument) || reservationId is not { } reserved || submissionId is null) throw Invalid();
            var bundle = await LoadAsync(tx, reserved, token);
            var original = await SubmissionOriginalAsync(tx, target, bundle.Reservation["admittedTargetRevision"].AsInt64, reserved, reference,
                target.Kind == EvidenceTargetKind.PaymentDeclaration ? owner.ReviewUploaderId ?? actor.UserId : owner.SupplierDocumentUploaderId ?? actor.UserId, token);
            // A supplemental PDF is a different association, never this primary image owner.
            if (target.Kind == EvidenceTargetKind.PaymentDeclaration && original is not null) throw Invalid();
            return new EvidenceSubmissionFormatView(target.Id, owner.Revision, reference, original is null ? "image" : "pdf");
        }, ct));
}
