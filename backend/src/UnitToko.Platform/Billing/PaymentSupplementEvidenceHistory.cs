using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Storage;
using B = UnitToko.Platform.Billing.PaymentSupplementBson;

namespace UnitToko.Platform.Billing;

// This bridge is deliberately constructible by the worker without an actor/DI graph.
// Historical integrity never requires a renewed approval or a fabricated requester.
internal sealed class PaymentSupplementEvidenceHistory : IPaymentSupplementEvidenceHistory
{
    private readonly IMongoDatabase database;
    private readonly EvidenceService evidence;
    internal PaymentSupplementEvidenceHistory(IMongoDatabase database, EvidenceService evidence)
    {
        var source = evidence.PaymentSupplementHistoryDatabase;
        if (!ReferenceEquals(database.Client, source.Client) || !database.DatabaseNamespace.Equals(source.DatabaseNamespace))
            throw new ArgumentException("Historical supplement evidence must join the same database and owning transaction.");
        this.database = database; this.evidence = evidence;
    }
    public async Task<ExpectedPublication> RequireOriginalAsync(IClientSessionHandle tx, long supplementId, long reservationId,
        EvidenceRef reference, long uploaderId, CancellationToken ct)
    {
        Transaction(tx);
        return await evidence.SubmissionOriginalAsync(tx, new(EvidenceTargetKind.PaymentSupplement, supplementId), 1,
            reservationId, reference, uploaderId, ct) ?? throw PaymentSupplementRecords.Broken();
    }
    public async Task RequireHistoricalPinAsync(IClientSessionHandle tx, BsonDocument attachment, CancellationToken ct)
    {
        Transaction(tx);
        var pin = await EvidencePinRecords.ReadAsync(database, tx, attachment["pinId"].AsInt64, ct);
        if (pin["associationKind"] != B.SourceKind || pin["associationId"] != attachment["_id"] || pin["supplementId"] != attachment["_id"] ||
            pin["supplementRevision"] != 2L || pin["associationHash"] != attachment["snapshotHash"] ||
            pin["actorId"] != attachment["uploaderId"] || pin["actorGrantVersion"] != attachment["actorGrantVersion"] ||
            pin["reservationId"] != attachment["reservationId"] || !pin["reference"].Equals(attachment["evidence"]) ||
            !pin["primary"].Equals(attachment["primary"]) || pin["primarySubmissionHash"] != attachment["primarySubmissionHash"] ||
            pin["createdAt"] != attachment["attachedAt"] || pin["reason"] != attachment["reason"] || pin["correlationId"] != attachment["correlationId"] ||
            pin["storeId"] != attachment["original"]["storeId"] || pin["generation"] != attachment["original"]["generation"])
            throw PaymentSupplementRecords.Broken();
        // Reconstructs the actual admission, completion, whole-PDF validation, lifecycle
        // and exact policy provenance; never re-enters PaymentSupplementRecords.
        await evidence.ValidateHistoricalPinAsync(tx, pin, ct);
    }
    public async Task RequireCompletedDocumentResultAsync(IClientSessionHandle tx, BsonDocument job, BsonDocument attachment, CancellationToken ct)
    {
        Transaction(tx);
        if (job["kind"] != B.JobKind || job["sourceKey"] != B.JobKey(attachment["_id"].AsInt64) ||
            !job["payload"].Equals(B.JobPayload(attachment))) throw PaymentSupplementRecords.Broken();
        // Shared records must install their exact PaymentSupplement branch before
        // activation. No generic/image result can satisfy this call.
        var records = new PdfDocumentExtractionRecords(database); var result = await records.ResultAsync(tx, job, ct);
        if (job["status"] == "completed" && result is null) throw PaymentSupplementRecords.Broken();
        if (result is not null) RequireAttachmentBinding(result, attachment);
        var reads = await database.GetCollection<BsonDocument>(PdfDocumentExtractionRecords.ProcessingReads).Find(tx, new BsonDocument("outboxId", job["_id"]))
            .Sort(new BsonDocument("_id", 1)).Limit(6).ToListAsync(ct);
        if (reads.Count > 5) throw PaymentSupplementRecords.Broken();
        foreach (var read in reads) {
            var verified = await records.ReadAuditAsync(tx, read["_id"].AsInt64, ct);
            if (!verified.Equals(read) || read["outboxId"] != job["_id"]) throw PaymentSupplementRecords.Broken();
            RequireAttachmentBinding(verified, attachment);
        }
        var failures = await database.GetCollection<BsonDocument>(PdfDocumentExtractionRecords.Failures).Find(tx, new BsonDocument("outboxId", job["_id"]))
            .Sort(new BsonDocument("_id", 1)).Limit(6).ToListAsync(ct);
        if (failures.Count > 5) throw PaymentSupplementRecords.Broken();
        foreach (var failure in failures) {
            var verified = await records.HistoricalFailureAsync(tx, failure["_id"].AsInt64, ct);
            if (!verified.Failure.Equals(failure)) throw PaymentSupplementRecords.Broken();
            RequireAttachmentBinding(verified.Failure, attachment);
        }
    }
    private static void RequireAttachmentBinding(BsonDocument row, BsonDocument attachment)
    {
        var binding = row["binding"].AsBsonDocument;
        if (row["sourceKind"] != B.SourceKind || row["purpose"] != "Payment" || binding["submissionId"] != attachment["_id"] ||
            binding["supplementId"] != attachment["_id"] || binding["supplementRevision"] != 2L ||
            binding["reservationId"] != attachment["reservationId"] || binding["pinId"] != attachment["pinId"] ||
            binding["uploaderId"] != attachment["uploaderId"] || binding["submissionHash"] != attachment["snapshotHash"] ||
            !binding["primary"].Equals(attachment["primary"]) || binding["primarySubmissionHash"] != attachment["primarySubmissionHash"] ||
            !binding["reference"].Equals(attachment["evidence"]) || !row["original"].Equals(attachment["original"]))
            throw PaymentSupplementRecords.Broken();
    }
    private void Transaction(IClientSessionHandle tx)
    {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw PaymentSupplementRecords.Broken();
    }
}
