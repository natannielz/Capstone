using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Billing;

internal static class PaymentSubmissionBson
{
    internal const string Operation = "billing.payment.submit", Collection = "paymentSubmissions";
    internal static DomainValidationException ImageRequired() => new("payment_image_evidence_required",
        "Pengajuan pembayaran wajib memakai bukti gambar yang telah divalidasi. PDF tidak dapat menggantikan bukti gambar.");
    internal static object Body(long id, long revision, long reservationId, EvidenceRef evidence) => new { id, revision, reservationId, evidence };
    internal static BsonDocument Reference(EvidenceRef value) => new() { { "id", value.Id }, { "version", value.Version }, { "sha256", value.Sha256 } };
    internal static EvidenceRef Reference(BsonDocument value) => new(value["id"].AsInt64, value["version"].AsInt64, value["sha256"].AsString);
    internal static BsonDocument Result(BsonDocument row) => new() { { "declarationId", row["declarationId"] }, { "submissionId", row["_id"] }, { "declarationRevision", row["declarationRevision"] },
        { "reviewRevision", row["reviewRevision"] }, { "state", row["state"] }, { "submittedAt", row["submittedAt"] } };
    internal static string Hash(BsonDocument row) => PaymentDraftBson.Hash(row);
    internal static string BodyHash(BsonDocument row) => AuthorizationStore.CommandHash(Body(row["declarationId"].AsInt64, row["declarationRevision"].AsInt64, row["reservationId"].AsInt64, Reference(row["evidence"].AsBsonDocument)), row["reason"].AsString);
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["submittedBy"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["submittedAt"] }, { "result", Result(row) } };
    internal static BsonDocument OcrPayload(BsonDocument row) => new() { { "submissionId", row["_id"] }, { "reviewRevision", row["reviewRevision"] },
        { "declarationVersionId", row["declarationVersionId"] }, { "evidence", row["evidence"] }, { "pinId", row["pinId"] }, { "submissionHash", row["snapshotHash"] } };
    internal static string OcrKey(BsonDocument row) => "payment.ocr.requested:" + row["_id"].AsInt64;
}
