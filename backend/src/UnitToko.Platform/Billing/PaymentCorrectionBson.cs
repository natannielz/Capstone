using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Billing;
internal static class PaymentCorrectionBson
{
    internal const string Heads = "paymentReviewHeads", Versions = "paymentReviewCorrections", Operation = "billing.payment.review.correct";
    internal static BsonArray Fields(IEnumerable<PaymentReviewField> fields) => new(fields.Select(field => new BsonDocument { { "field", field.Field }, { "value", field.Value is null ? BsonNull.Value : new BsonString(field.Value) } }));
    internal static ImmutableArray<PaymentReviewField> Fields(BsonArray fields) => [.. fields.Select(field => new PaymentReviewField(field["field"].AsString, field["value"].IsBsonNull ? null : field["value"].AsString))];
    internal static BsonDocument Binding(PaymentCorrectionBinding value) => new() { { "declarationRevision", value.DeclarationRevision }, { "submissionId", value.SubmissionId }, { "evidence", PaymentSubmissionBson.Reference(value.Evidence) }, { "extractionId", value.ExtractionId } };
    internal static PaymentCorrectionBinding Binding(BsonDocument value) => new(value["declarationRevision"].AsInt64, value["submissionId"].AsInt64, PaymentSubmissionBson.Reference(value["evidence"].AsBsonDocument), value["extractionId"].AsInt64);
    internal static object Body(long declarationId, long revision, PaymentCorrectionBinding binding, ImmutableArray<PaymentReviewField> fields) =>
        new { declarationId, expectedReviewRevision = revision, binding, fields };
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "snapshotHash")));
    internal static BsonDocument Result(BsonDocument row) => new() { { "declarationId", row["declarationId"] }, { "submissionId", row["binding"]["submissionId"] },
        { "reviewRevision", row["revision"] }, { "correctionId", row["_id"] }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["recordedBy"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "snapshotHash", row["snapshotHash"] }, { "bodyHash", row["bodyHash"] },
        { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Operation + ":" + row["_id"].AsInt64;
}
