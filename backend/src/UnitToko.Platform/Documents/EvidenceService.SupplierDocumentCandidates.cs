using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Platform.Documents;

internal sealed partial class EvidenceService
{
    // Same owner/transaction and exact existing result-read audit as the PDF
    // viewer. This consumes a validated/pinned derivative, without file I/O.
    internal Task<Action> RequireSupplierDocumentCandidatesAsync(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target,
        long expectedRevision, EvidenceRef reference, IReadOnlyList<PdfDocumentCandidateReference> candidates, ISupplierDocumentOcrSource source,
        DateTimeOffset requestDeadline, Action<DateTimeOffset> captureWindow, AuthorizationReadMetadata metadata, CancellationToken ct) =>
        GuardAsync(tx, async () => {
            if (candidates.Count is < 1 or > 8 || candidates.Any(value => value is null || value.Evidence != reference) ||
                metadata.Purpose != "supplier-document-pdf-ocr-view" || !Text(metadata.CorrelationId, 128)) throw Invalid();
            DateTimeOffset? until = null;
            await WithReadTargetAsync(tx, actor, target, expectedRevision, reference, async (owner, token) => {
                if (target.Kind != EvidenceTargetKind.SupplierDocument || expectedRevision != 1 || owner.Revision != 1 ||
                    owner.SupplierDocumentId is not { } submissionId || owner.SupplierDocumentReservationId is not { } reservationId ||
                    owner.SupplierDocumentUploaderId is not { } uploaderId) throw Invalid();
                var job = await Rows("outboxEvents").Find(tx, new BsonDocument {
                    { "kind", PdfDocumentExtractionContract.SupplierJobKind }, { "payload.submissionId", submissionId }
                }).SingleOrDefaultAsync(token) ?? throw PdfDocumentExtractionRecords.Broken();
                var original = await SupplierPdfDocumentProcessingOriginalAsync(tx, source, job, requestDeadline, token);
                if (original.Source.SubmissionId != submissionId || original.Source.DraftId != target.Id || original.Source.DraftRevision != 1 ||
                    original.Source.ReservationId != reservationId || original.Source.UploaderId != uploaderId || original.Source.Evidence != reference) throw Invalid();
                until = original.Until; captureWindow(until.Value);
                var records = new PdfDocumentExtractionRecords(database);
                var view = await records.ReadViewAsync(tx, job, "SupplierInvoice", original.Binding, original.Publication,
                    actor.UserId, actor.GrantVersion, ids, clock, metadata.Purpose, metadata.CorrelationId, token);
                if (view.State != "succeeded" || view.Extraction is null) throw PdfDocumentExtractionRecords.Broken();
                foreach (var candidate in candidates)
                    _ = await records.RequireCandidateAsync(tx, job, "SupplierInvoice", original.Binding, original.Publication, candidate, token);
                FinalCheck(until, token); return true;
            }, ct);
            if (until is null) throw Invalid();
            void Check() { FinalCheck(until, ct); FinalCheck(requestDeadline, ct); }
            Check(); return (Action)Check;
        });
}
