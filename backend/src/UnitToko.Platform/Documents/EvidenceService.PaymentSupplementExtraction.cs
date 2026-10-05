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
    internal Task<PaymentSupplementOcrView> ReadPaymentSupplementPdfExtractionAsync(IClientSessionHandle tx, ActorContext actor,
        EvidenceTargetRef target, long expectedRevision, EvidenceRef reference, AuthorizationReadMetadata metadata,
        IPaymentSupplementOcrSource source, bool review, CancellationToken ct) => GuardAsync(tx, async () => {
            var purpose = review ? "payment-review-supplement-pdf-ocr-view" : "payment-supplement-pdf-ocr-view";
            DateTimeOffset? until = null;
            var result = await WithReadTargetAsync(tx, actor, target, expectedRevision, reference, async (owner, token) => {
                if (target.Kind != EvidenceTargetKind.PaymentSupplement || expectedRevision != 2 || owner.Revision != 2 ||
                    metadata.Purpose != purpose || !Text(metadata.CorrelationId, 128) ||
                    owner.SupplementId != target.Id || owner.SupplementReservationId is not { } reservationId ||
                    owner.SupplementUploaderId is not { } uploaderId || owner.OwnerApprovalUntil is not { } approvalUntil)
                    throw Invalid();
                var job = await Rows("outboxEvents").Find(tx, new BsonDocument {
                    { "kind", PaymentSupplementBson.JobKind }, { "payload.supplementId", target.Id }
                }).SingleOrDefaultAsync(token) ?? throw PdfDocumentExtractionRecords.Broken();
                var original = await PaymentSupplementPdfOriginalAsync(tx, source, job,
                    clock.UtcNow.AddSeconds(5), false, token);
                var snapshot = original.Source;
                if (snapshot.SupplementId != target.Id || snapshot.SupplementRevision != expectedRevision ||
                    snapshot.ReservationId != reservationId || snapshot.UploaderId != uploaderId || snapshot.PdfEvidence != reference)
                    throw Invalid();
                var view = await new PdfDocumentExtractionRecords(database).ReadViewAsync(tx, job,
                    PaymentSupplementBson.SourceKind, original.Binding, original.Publication,
                    actor.UserId, actor.GrantVersion, ids, clock, purpose, metadata.CorrelationId, token);
                until = original.Until < approvalUntil ? original.Until : approvalUntil;
                FinalCheck(until, token);
                var primary = snapshot.Primary;
                return new PaymentSupplementOcrView(primary.DeclarationId, primary.DeclarationRevision, primary.SubmissionId,
                    primary.PrimaryEvidence, snapshot.SupplementId, snapshot.SupplementRevision, snapshot.PdfEvidence, view);
            }, ct);
            FinalCheck(until, ct); return result;
        });
}
