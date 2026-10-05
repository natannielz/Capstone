using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Billing;

// Immutable reconstruction only. Expired current D06 approvals cannot rewrite a
// previously recorded correction or invalidate its bank-review history.
internal sealed class PaymentCorrectionSupplementRecords(IMongoDatabase database)
{
    internal async Task RequireAsync(IClientSessionHandle tx, BsonDocument correction, CancellationToken ct)
    {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || database.Client is not MongoClient client)
            throw PaymentCorrectionRecords.Bad();
        var observations = PaymentCorrectionBson.Observations(correction);
        if (observations is null) return;
        if (correction["schemaVersion"] != 2 || correction["personAtCorrection"]["verification"] != "Verified" ||
            !correction["supplementaryObservations"].Equals(PaymentCorrectionBson.Observations(observations.Value)))
            throw PaymentCorrectionRecords.Bad();
        var binding = PaymentCorrectionBson.Binding(correction["binding"].AsBsonDocument);
        var primary = new PaymentSupplementPrimary(correction["declarationId"].AsInt64, binding.DeclarationRevision, binding.SubmissionId, binding.Evidence);
        var historical = new PaymentSupplementEvidenceHistory(database,
            new EvidenceService(database, new MongoUnitOfWork(client), new IdAllocator(database), new SystemClock()));
        var source = new PaymentSupplementOcrSource(database, historical); var records = new PdfDocumentExtractionRecords(database);
        foreach (var group in observations.Value.GroupBy(value => value.SupplementId)) {
            var job = await database.GetCollection<BsonDocument>("outboxEvents").Find(tx, new BsonDocument("sourceKey", PaymentSupplementBson.JobKey(group.Key))).SingleOrDefaultAsync(ct)
                ?? throw PaymentCorrectionRecords.Bad();
            var current = await source.RequireAsync(tx, job, ct);
            if (current.Primary != primary || current.SupplementRevision != 2 || current.PdfEvidence == primary.PrimaryEvidence)
                throw PaymentCorrectionRecords.Bad();
            var result = await records.ResultAsync(tx, job, ct) ?? throw PaymentCorrectionRecords.Bad();
            if (result["createdAt"].CompareTo(correction["recordedAt"]) > 0) throw PaymentCorrectionRecords.Bad();
            var readFilter = new BsonDocument { { "actorId", correction["recordedBy"] }, { "actorGrantVersion", correction["actorGrantVersion"] },
                { "outboxId", job["_id"] }, { "sourceKind", PaymentSupplementBson.SourceKind }, { "sourceId", group.Key }, { "sourceRevision", 2L },
                { "submissionId", group.Key }, { "reference", PaymentSubmissionBson.Reference(current.PdfEvidence) }, { "resultId", result["_id"] },
                { "resultHash", result["resultHash"] }, { "state", "succeeded" }, { "purpose", "payment-review-supplement-pdf-ocr-view" },
                { "correlationId", correction["correlationId"] }, { "createdAt", new BsonDocument { { "$gte", result["createdAt"] }, { "$lte", correction["recordedAt"] } } } };
            if (!await database.GetCollection<BsonDocument>(PdfDocumentExtractionRecords.Reads).Find(tx, new BsonDocument("$and", new BsonArray {
                readFilter, PaymentSupplementSchemas.DocumentOwnerValidator(PdfDocumentExtractionRecords.Reads) })).Limit(1).AnyAsync(ct))
                throw PaymentCorrectionRecords.Bad();
            foreach (var observation in group) {
                if (observation.Candidate.Evidence != current.PdfEvidence) throw PaymentCorrectionRecords.Bad();
                _ = await records.RequireCandidateAsync(tx, job, PaymentSupplementBson.SourceKind, result["binding"].AsBsonDocument,
                    current.Original, observation.Candidate, ct);
            }
        }
    }
}
