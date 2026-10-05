using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Billing;
internal static class PaymentCorrectionBson
{
    internal const string Heads = "paymentReviewHeads", Versions = "paymentReviewCorrections", Operation = "billing.payment.review.correct";
    internal static BsonArray Fields(IEnumerable<PaymentReviewField> fields) => new(fields.Select(field => new BsonDocument { { "field", field.Field }, { "value", field.Value is null ? BsonNull.Value : new BsonString(field.Value) } }));
    internal static ImmutableArray<PaymentReviewField> Fields(BsonArray fields) => [.. fields.Select(field => new PaymentReviewField(field["field"].AsString, field["value"].IsBsonNull ? null : field["value"].AsString))];
    internal static BsonDocument Binding(PaymentCorrectionBinding value) => new() { { "declarationRevision", value.DeclarationRevision }, { "submissionId", value.SubmissionId }, { "evidence", PaymentSubmissionBson.Reference(value.Evidence) }, { "extractionId", value.ExtractionId } };
    internal static PaymentCorrectionBinding Binding(BsonDocument value) => new(value["declarationRevision"].AsInt64, value["submissionId"].AsInt64, PaymentSubmissionBson.Reference(value["evidence"].AsBsonDocument), value["extractionId"].AsInt64);
    internal static object Body(long declarationId, long revision, PaymentCorrectionBinding binding, ImmutableArray<PaymentReviewField> fields,
        ImmutableArray<PaymentSupplementReviewObservation>? supplementaryObservations = null) => supplementaryObservations is null
        ? new { declarationId, expectedReviewRevision = revision, binding, fields }
        : (object)new { declarationId, expectedReviewRevision = revision, binding, fields, supplementaryObservations = supplementaryObservations.Value };
    internal static BsonArray Observations(ImmutableArray<PaymentSupplementReviewObservation> values) =>
        new(PaymentReviewCorrectionPolicy.CopySupplementaryObservations(values)!.Value.Select(value => new BsonDocument {
            { "field", value.Field }, { "supplementId", value.SupplementId }, { "supplementRevision", value.SupplementRevision },
            { "candidate", PdfDocumentExtractionRecords.CandidateReference(value.Candidate) }, { "originalReviewed", value.OriginalReviewed } }));
    internal static ImmutableArray<PaymentSupplementReviewObservation>? Observations(BsonDocument row) {
        if (!row.TryGetValue("supplementaryObservations", out var value)) return null;
        if (!value.IsBsonArray) throw PaymentCorrectionRecords.Bad();
        var observations = value.AsBsonArray.Select(item => {
            var candidate = item["candidate"];
            return new PaymentSupplementReviewObservation(item["field"].AsString, item["supplementId"].AsInt64, item["supplementRevision"].AsInt64,
                new(candidate["resultId"].AsInt64, candidate["resultHash"].AsString, PaymentSubmissionBson.Reference(candidate["evidence"].AsBsonDocument),
                    candidate["pageIndex"].AsInt32, candidate["field"].AsString), item["originalReviewed"].AsBoolean);
        }).ToImmutableArray();
        return PaymentReviewCorrectionPolicy.CopySupplementaryObservations(observations);
    }
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "snapshotHash")));
    internal static BsonDocument Result(BsonDocument row) => new() { { "declarationId", row["declarationId"] }, { "submissionId", row["binding"]["submissionId"] },
        { "reviewRevision", row["revision"] }, { "correctionId", row["_id"] }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument row) {
        var audit = new BsonDocument { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["recordedBy"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "snapshotHash", row["snapshotHash"] }, { "bodyHash", row["bodyHash"] },
        { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
        if (row.TryGetValue("supplementaryObservations", out var observations)) audit.Add("supplementaryObservations", observations.DeepClone());
        return audit;
    }
    internal static string SourceKey(BsonDocument row) => Operation + ":" + row["_id"].AsInt64;
}
