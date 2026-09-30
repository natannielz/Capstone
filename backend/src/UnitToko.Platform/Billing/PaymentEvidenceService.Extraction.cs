using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentEvidenceService
{
    public async Task<PaymentExtractionView> ReadReviewExtractionAsync(ActorContext actor, long declarationId, long expectedRevision,
        EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        await StorageAsync(ct); await DocumentProcessingStorage.RequireAsync(database, ct);
        return await unit.ExecuteAsync((tx, token) => reviewEvidence.ReadPaymentExtractionAsync(tx, actor, Target(declarationId), expectedRevision, reference, metadata, new PaymentOcrSource(database), token), ct);
    }
    public async Task<PaymentExtractionView> ReadExtractionAsync(ActorContext actor, long declarationId, long expectedRevision,
        EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        await StorageAsync(ct); await DocumentProcessingStorage.RequireAsync(database, ct);
        return await unit.ExecuteAsync((tx, token) => evidence.ReadPaymentExtractionAsync(tx, actor, Target(declarationId), expectedRevision, reference, metadata, new PaymentOcrSource(database), token), ct);
    }
}
