using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentEvidenceService
{
    public async Task<PaymentCorrectionView> ReadCorrectionsAsync(ActorContext actor, long id, PaymentCorrectionBinding binding, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        PaymentReviewCorrectionPolicy.Validate(binding, 0); await StorageAsync(ct); await DocumentProcessingStorage.RequireAsync(database, ct); await declarations.CorrectionStorageAsync(ct);
        return await unit.ExecuteAsync((tx, token) => reviewEvidence.WithPaymentExtractionAsync(tx, actor, Target(id), binding.DeclarationRevision, binding.Evidence, metadata,
            new PaymentOcrSource(database), (view, _, inner) => declarations.CorrectionViewAsync(tx, view, binding, inner), token), ct);
    }
    public async Task<BsonDocument> CorrectReviewAsync(ActorContext actor, long id, long revision, PaymentCorrectionBinding binding, ImmutableArray<PaymentReviewField> fields,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        await StorageAsync(ct); await DocumentProcessingStorage.RequireAsync(database, ct);
        return await declarations.CorrectAsync(actor, id, revision, binding, fields, metadata, (tx, consume, token) =>
            reviewEvidence.WithPaymentExtractionAsync(tx, actor, Target(id), binding.DeclarationRevision, binding.Evidence,
                new("payment-ocr-view", metadata.CorrelationId), new PaymentOcrSource(database), consume, token), ct);
    }
    public Task<BsonDocument?> RecoverReviewCorrectionAsync(ActorContext actor, long id, string key, CancellationToken ct = default) => declarations.RecoverCorrectionAsync(actor, id, key, ct);
}
