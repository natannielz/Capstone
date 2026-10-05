using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentSupplementEvidenceService
{
    public Task<PaymentSupplementOcrView> ReadPdfExtractionAsync(ActorContext actor, long declarationId, long supplementId,
        long ownRevision, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) =>
        ReadPdfExtractionAsync(actor, declarationId, supplementId, ownRevision, reference, metadata, false, ct);

    public Task<PaymentSupplementOcrView> ReadReviewPdfExtractionAsync(ActorContext actor, long declarationId, long supplementId,
        long ownRevision, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) =>
        ReadPdfExtractionAsync(actor, declarationId, supplementId, ownRevision, reference, metadata, true, ct);

    private async Task<PaymentSupplementOcrView> ReadPdfExtractionAsync(ActorContext actor, long declarationId, long supplementId,
        long ownRevision, EvidenceRef reference, AuthorizationReadMetadata metadata, bool review, CancellationToken ct)
    {
        if (ownRevision != 2) throw PaymentDraftRecords.Changed();
        PaymentSupplementRules.Reference(reference);
        await StorageAsync(ct);
        var evidence = For(declarationId, supplementId, ownRevision, review: review);
        return await unit.ExecuteAsync((tx, token) => evidence.ReadPaymentSupplementPdfExtractionAsync(tx, actor,
            Target(supplementId), ownRevision, reference, metadata, new PaymentSupplementOcrSource(database, history), review, token), ct);
    }
}
