using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Platform.Documents;

internal sealed partial class EvidenceService
{
    internal Task<SupplierDocumentExtractionView> ReadSupplierDocumentExtractionAsync(IClientSessionHandle tx, ActorContext actor,
        EvidenceTargetRef target, long expectedRevision, EvidenceRef reference, AuthorizationReadMetadata metadata,
        ISupplierDocumentOcrSource source, CancellationToken ct) => GuardAsync(tx, async () => {
        DateTimeOffset? deadline = null;
        var result = await WithReadTargetAsync(tx, actor, target, expectedRevision, reference, async (owner, token) => {
            if (target.Kind != EvidenceTargetKind.SupplierDocument || metadata.Purpose != "supplier-document-ocr-view" || !Text(metadata.CorrelationId, 128) ||
                owner.SupplierDocumentId is not { } submissionId || owner.SupplierDocumentReservationId is not { } reservationId) throw Invalid();
            var job = await Rows("outboxEvents").Find(tx, new BsonDocument {
                { "kind", SupplierDocumentExtractionRecords.Kind }, { "payload.submissionId", submissionId } }).SingleOrDefaultAsync(token)
                ?? throw SupplierDocumentExtractionRecords.Broken();
            // Derivatives retain current original-file eligibility and owner authority, including independently scoped readers.
            var original = await SupplierDocumentProcessingOriginalAsync(tx, source, job, clock.UtcNow.AddSeconds(5), token);
            if (original.Source.Evidence != reference || original.Source.ReservationId != reservationId || original.Source.DraftId != target.Id ||
                original.Source.DraftRevision != expectedRevision || original.Source.UploaderId != (owner.SupplierDocumentUploaderId ?? actor.UserId)) throw Invalid();
            var view = await new SupplierDocumentExtractionRecords(database).ReadViewAsync(tx, job, original.Source, original.Binding,
                actor.UserId, actor.GrantVersion, ids, clock, metadata.CorrelationId, token);
            deadline = original.Until; FinalCheck(deadline, token); return view;
        }, ct);
        FinalCheck(deadline, ct); return result;
    });
}
