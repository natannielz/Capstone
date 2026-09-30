using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Platform.Documents;

internal sealed partial class EvidenceService
{
    // Immutable decision history may outlive a current read/processing policy. This returns
    // only its original extraction after current owning access and the complete saved audit.
    // It never admits new verification or returns image bytes.
    internal Task<PaymentExtractionView> AcceptedPaymentExtractionAsync(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target,
        long revision, EvidenceRef reference, long extractionId, CancellationToken ct) => GuardAsync(tx, () =>
        WithReadTargetAsync(tx, actor, target, revision, reference, async (owner, token) => {
            if (owner.SubmittedSubmissionId is not { } submissionId || owner.SubmittedReservationId is null) throw Invalid();
            var job = await Rows("outboxEvents").Find(tx, new BsonDocument { { "kind", "payment.ocr.requested" }, { "payload.submissionId", submissionId } }).SingleOrDefaultAsync(token) ?? throw ExtractionRecords.Broken();
            var row = await new ExtractionRecords(database).ResultAsync(tx, job, token) ?? throw ExtractionRecords.Broken();
            if (row["_id"] != extractionId || !row["binding"]["reference"].Equals(ReadReference(reference)) || row["binding"]["submissionId"] != submissionId) throw ExtractionRecords.Broken();
            return new PaymentExtractionView(target.Id, submissionId, reference, "succeeded", extractionId, Date(row["createdAt"]), ExtractionRecords.Extract(row));
        }, ct));
}
