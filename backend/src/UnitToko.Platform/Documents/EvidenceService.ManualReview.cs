using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Documents;

internal sealed record ManualFailureSource(BsonDocument Snapshot, Action RequireCurrent);

internal sealed partial class EvidenceService
{
    // Called inside WithPaymentExtractionAsync's already-authorized owning review and held-original consumer.
    // Re-entering that owner would touch its conditional head twice in the same transaction.
    internal Task<ManualFailureSource> ManualFailureAsync(IClientSessionHandle tx, PaymentExtractionView view, long revision,
        CancellationToken ct) => GuardAsync(tx, async () => {
            var token = ct; var submissionId = view.SubmissionId; var reference = view.Evidence;
            if (view.DeclarationId <= 0 || revision <= 0 || submissionId <= 0 || view.ResultId is not null || view.State is not ("retry" or "dead"))
                throw PaymentManualReviewPolicy.Invalid("payment_manual_review_failure_required");
            var job = await Rows("outboxEvents").Find(tx, new BsonDocument { { "kind", "payment.ocr.requested" }, { "payload.submissionId", submissionId } }).SingleOrDefaultAsync(token) ?? throw ExtractionRecords.Broken();
            var records = new ExtractionRecords(database);
            if (await records.ResultAsync(tx, job, token) is not null || !PaymentManualReviewPolicy.IsReviewableFailure(job["status"].AsString, job.GetValue("lastFailureCode", "").AsString))
                throw PaymentManualReviewPolicy.Invalid("payment_manual_review_failure_required");
            var latest = await Rows("documentProcessingFailures").Find(tx, new BsonDocument("outboxId", job["_id"])).Sort(new BsonDocument("claimAttempt", -1)).FirstOrDefaultAsync(token) ?? throw ExtractionRecords.Broken();
            var (failure, read) = await records.HistoricalFailureAsync(tx, latest["_id"].AsInt64, token);
            if (failure["claimAttempt"] != job["attempts"] || failure["safeFailure"] != job["lastFailureCode"] || failure["outboxId"] != job["_id"]) throw ExtractionRecords.Broken();
            if (job["status"] != view.State) throw ExtractionRecords.Broken();
            RequireManualFailureSource(job, read, submissionId, view.DeclarationId, revision, reference);
            var original = await ProcessingOriginalAsync(tx, new Billing.PaymentOcrSource(database), job, clock.UtcNow.AddSeconds(5), token);
            // Renewed security policies can change their identities, but never the pinned original.
            var historical = read["binding"].DeepClone().AsBsonDocument; var current = original.Binding.DeepClone().AsBsonDocument;
            foreach (var key in new[] { "readPolicyId", "readPolicyRevision", "validationPolicyId", "validationPolicyRevision" }) { historical.Remove(key); current.Remove(key); }
            if (!historical.Equals(current)) throw ExtractionRecords.Broken();
            await new OutboxStore(database, clock).FenceFailedPaymentProcessingAsync(tx, job, token); FinalCheck(original.Until, token);
            var snapshot = new BsonDocument { { "outboxId", job["_id"] }, { "jobState", job["status"] }, { "attempt", job["attempts"] },
                { "failureId", failure["_id"] }, { "failureHash", failure["recordHash"] }, { "readAuditId", read["_id"] }, { "readHash", read["recordHash"] },
                { "bindingHash", failure["bindingHash"] }, { "pipelineId", failure["pipelineId"] }, { "safeFailure", failure["safeFailure"] }, { "failedAt", failure["createdAt"] } };
            return new ManualFailureSource(snapshot, () => FinalCheck(original.Until, token));
        });

    // Billing has already authorized and fenced the exact immutable submitted owning source.
    internal Task ValidateAcceptedManualFailureAsync(IClientSessionHandle tx, long declarationId, long revision, long submissionId,
        EvidenceRef reference, BsonDocument snapshot, CancellationToken ct) => GuardAsync(tx, async () => {
            var token = ct;
            var job = await Rows("outboxEvents").Find(tx, new BsonDocument("_id", snapshot["outboxId"])).SingleOrDefaultAsync(token) ?? throw ExtractionRecords.Broken();
            var (failure, read) = await new ExtractionRecords(database).HistoricalFailureAsync(tx, snapshot["failureId"].AsInt64, token);
            if (declarationId <= 0 || revision <= 0 || submissionId <= 0 || snapshot["failureHash"] != failure["recordHash"] || snapshot["readAuditId"] != read["_id"] || snapshot["readHash"] != read["recordHash"] ||
                snapshot["bindingHash"] != failure["bindingHash"] || snapshot["pipelineId"] != failure["pipelineId"] || snapshot["safeFailure"] != failure["safeFailure"] ||
                snapshot["attempt"] != failure["claimAttempt"] || snapshot["failedAt"] != failure["createdAt"] || failure["outboxId"] != job["_id"] ||
                !PaymentManualReviewPolicy.IsReviewableFailure(snapshot["jobState"].AsString, snapshot["safeFailure"].AsString)) throw ExtractionRecords.Broken();
            RequireManualFailureSource(job, read, submissionId, declarationId, revision, reference); return true;
        });

    private static void RequireManualFailureSource(BsonDocument job, BsonDocument read, long submissionId, long declarationId, long revision, EvidenceRef reference) {
        var binding = read["binding"];
        var payload = new BsonDocument { { "submissionId", binding["submissionId"] }, { "reviewRevision", binding["reviewRevision"] }, { "declarationVersionId", binding["declarationVersionId"] },
            { "evidence", binding["reference"] }, { "pinId", binding["pinId"] }, { "submissionHash", binding["submissionHash"] } };
        if (job["kind"] != "payment.ocr.requested" || !job["payload"].Equals(payload) || job["createdAt"].CompareTo(read["createdAt"]) > 0 ||
            binding["submissionId"] != submissionId || binding["declarationId"] != declarationId || binding["declarationRevision"] != revision || !binding["reference"].Equals(ReadReference(reference)))
            throw ExtractionRecords.Broken();
    }
}
