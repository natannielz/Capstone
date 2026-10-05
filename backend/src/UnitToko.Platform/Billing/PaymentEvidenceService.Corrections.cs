using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentEvidenceService
{
    public async Task<PaymentCorrectionView> ReadCorrectionsAsync(ActorContext actor, long id, PaymentCorrectionBinding binding, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        PaymentReviewCorrectionPolicy.Validate(binding, 0); await StorageAsync(ct); await DocumentProcessingStorage.RequireAsync(database, ct); await declarations.CorrectionStorageAsync(ct);
        return await unit.ExecuteAsync((tx, token) => reviewEvidence.WithPaymentExtractionAsync(tx, actor, Target(id), binding.DeclarationRevision, binding.Evidence, metadata,
            new PaymentOcrSource(database), (view, _, inner) => declarations.CorrectionViewAsync(tx, view, binding, inner), token), ct);
    }
    public Task<BsonDocument> CorrectReviewAsync(ActorContext actor, long id, long revision, PaymentCorrectionBinding binding, ImmutableArray<PaymentReviewField> fields,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) => CorrectReviewAsync(actor, id, revision, binding, fields, null, metadata, ct);
    public async Task<BsonDocument> CorrectReviewAsync(ActorContext actor, long id, long revision, PaymentCorrectionBinding binding, ImmutableArray<PaymentReviewField> fields,
        ImmutableArray<PaymentSupplementReviewObservation>? supplementaryObservations, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        supplementaryObservations = PaymentReviewCorrectionPolicy.CopySupplementaryObservations(supplementaryObservations);
        await StorageAsync(ct); await DocumentProcessingStorage.RequireAsync(database, ct);
        if (supplementaryObservations is not null) {
            await PaymentSupplementCorrectionStorage.RequireAsync(database, PaymentCorrectionBson.Versions, ct, requireAnnotated: true);
            await PaymentSupplementStorage.RequireAsync(database, ct);
        }
        // Capture once for this request, outside the bounded transaction retries.
        // Retrying contention cannot extend the authority/original review window.
        var pdfDeadline = clock.UtcNow.AddSeconds(5);
        DateTimeOffset? pdfWindow = null;
        void Capture(DateTimeOffset until) { if (pdfWindow is null || until < pdfWindow.Value) pdfWindow = until; }
        return await declarations.CorrectAsync(actor, id, revision, binding, fields, metadata, async (tx, consume, token) => {
            var checks = new List<Action>();
            if (supplementaryObservations is not null) {
                var ids = new IdAllocator(database);
                var history = new PaymentSupplementEvidenceHistory(database, new EvidenceService(database, unit, ids, clock));
                var owner = new PaymentSupplementService(database, unit, ids, clock, declarations, history);
                var primary = new PaymentSupplementPrimary(id, binding.DeclarationRevision, binding.SubmissionId, binding.Evidence);
                foreach (var group in supplementaryObservations.Value.GroupBy(value => value.SupplementId)) {
                    var scoped = new EvidenceService(database, unit, ids, clock, registration,
                        paymentSupplementTargets: new PaymentSupplementEvidenceTarget(database, owner, id, group.Key, 2, review: true));
                    // Complete each existing parent/policy fence before the primary
                    // adapter captures its declaration snapshot. Nesting these
                    // adapters would invalidate that snapshot within our own transaction.
                    checks.Add(await scoped.RequirePaymentSupplementCandidatesAsync(tx, actor, primary, group.Key, [.. group],
                        new PaymentSupplementOcrSource(database, history), pdfDeadline, Capture,
                        new("payment-review-supplement-pdf-ocr-view", metadata.CorrelationId), token));
                }
            }
            void CheckSupplements() {
                foreach (var check in checks) check();
                token.ThrowIfCancellationRequested();
                if (pdfWindow is { } until && clock.UtcNow >= until)
                    throw new PersistenceConflictException("evidence_deadline_expired", "The captured supplementary original review window expired");
            }
            CheckSupplements();
            return await reviewEvidence.WithPaymentExtractionAsync(tx, actor, Target(id), binding.DeclarationRevision, binding.Evidence,
                new("payment-ocr-view", metadata.CorrelationId), new PaymentOcrSource(database), async (image, imageCheck, inner) => {
                    if (supplementaryObservations is null) return await consume(image, imageCheck, inner);
                    void Check() {
                        imageCheck(); CheckSupplements();
                        inner.ThrowIfCancellationRequested();
                    }
                    Check(); var result = await consume(image, Check, inner); Check(); return result;
                }, token);
        }, ct, supplementaryObservations);
    }
    public Task<BsonDocument?> RecoverReviewCorrectionAsync(ActorContext actor, long id, string key, CancellationToken ct = default) => declarations.RecoverCorrectionAsync(actor, id, key, ct);
}
