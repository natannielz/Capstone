using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierDocumentEvidenceService
{
    public async Task<PdfDocumentExtractionView> ReadPdfExtractionAsync(ActorContext actor, long draftId, long expectedRevision,
        EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        await StorageAsync(ct); await PdfDocumentExtractionRecords.RequireStorageAsync(database, ct);
        return await unit.ExecuteAsync((tx, token) => evidence.ReadSupplierDocumentPdfExtractionAsync(tx, actor, Target(draftId), expectedRevision,
            reference, metadata, new SupplierDocumentOcrSource(documents), token), ct);
    }
    public async Task<EvidenceSubmissionFormatView> ReadSubmissionFormatAsync(ActorContext actor, long draftId, long expectedRevision,
        EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await unit.ExecuteAsync((tx, token) => evidence.ReadSubmissionFormatAsync(tx, actor, Target(draftId), expectedRevision, reference,
            metadata, "supplier-document-evidence-format-view", token), ct);
    }
}
