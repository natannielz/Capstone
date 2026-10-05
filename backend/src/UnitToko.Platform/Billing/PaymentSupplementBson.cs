using UnitToko.Platform.Documents;
using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Billing;

internal static class PaymentSupplementBson
{
    internal const string Heads = "paymentSupplementHeads", Versions = "paymentSupplementVersions", Attachments = "paymentSupplementAttachments",
        Policies = "paymentSupplementPolicies", CreateOperation = "billing.payment.supplement.create", AttachOperation = "billing.payment.supplement.attach",
        JobKind = "payment.supplement-document-ocr.requested", SourceKind = "PaymentSupplement";
    internal static BsonDocument Primary(PaymentSupplementPrimary value) => new() { { "declarationId", value.DeclarationId },
        { "declarationRevision", value.DeclarationRevision }, { "submissionId", value.SubmissionId }, { "primaryEvidence", PaymentSubmissionBson.Reference(value.PrimaryEvidence) } };
    internal static PaymentSupplementPrimary Primary(BsonDocument value) => new PaymentSupplementPrimary(value["declarationId"].AsInt64,
        value["declarationRevision"].AsInt64, value["submissionId"].AsInt64, PaymentSubmissionBson.Reference(value["primaryEvidence"].AsBsonDocument)).Validate();
    internal static object CreateBody(PaymentSupplementPrimary primary) => new { primary = primary.Validate() };
    internal static object AttachBody(PaymentSupplementAttachInput input) => new { input = input.Validate() };
    internal static string Hash(BsonDocument row) { var copy = row.DeepClone().AsBsonDocument; copy.Remove("snapshotHash"); return PdfEvidenceValidationRecords.Hash(copy); }
    internal static PaymentSupplementView View(BsonDocument version, BsonDocument? attachment) {
        var primary = Primary(version["primary"].AsBsonDocument);
        return new(primary.DeclarationId, primary.DeclarationRevision, primary.SubmissionId, primary.PrimaryEvidence,
            version["supplementId"].AsInt64, version["revision"].AsInt64, version["state"].AsString,
            attachment?["reservationId"].AsInt64, attachment is null ? null : PaymentSubmissionBson.Reference(attachment["evidence"].AsBsonDocument),
            attachment is null ? null : new DateTimeOffset(attachment["attachedAt"].ToUniversalTime()), version["reason"].AsString);
    }
    internal static BsonDocument Result(BsonDocument version, BsonDocument? attachment) {
        var result = version["primary"].DeepClone().AsBsonDocument;
        result.Add("supplementId", version["supplementId"]).Add("supplementRevision", version["revision"]).Add("state", version["state"])
            .Add("reservationId", attachment?["reservationId"] ?? BsonNull.Value).Add("pdfEvidence", attachment?["evidence"] ?? BsonNull.Value)
            .Add("attachedAt", attachment?["attachedAt"] ?? BsonNull.Value).Add("reason", version["reason"]); return result;
    }
    internal static string JobKey(long supplementId) => JobKind + ":" + supplementId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":2";
    internal static BsonDocument JobPayload(BsonDocument attachment) => new() { { "sourceKind", SourceKind }, { "supplementId", attachment["_id"] },
        { "supplementRevision", 2L }, { "primary", attachment["primary"] }, { "primarySubmissionHash", attachment["primarySubmissionHash"] },
        { "evidence", attachment["evidence"] }, { "reservationId", attachment["reservationId"] }, { "uploaderId", attachment["uploaderId"] },
        { "pinId", attachment["pinId"] }, { "attachmentHash", attachment["snapshotHash"] }, { "original", attachment["original"] } };
    internal static BsonDocument Audit(BsonDocument version, BsonDocument? attachment) => new() { { "_id", version["auditId"] }, { "schemaVersion", 1 },
        { "kind", version["operation"] }, { "operation", version["operation"] }, { "actorId", version["actorId"] }, { "actorGrantVersion", version["actorGrantVersion"] },
        { "requestKey", version["requestKey"] }, { "bodyHash", version["bodyHash"] }, { "snapshotHash", version["snapshotHash"] },
        { "reason", version["reason"] }, { "correlationId", version["correlationId"] }, { "createdAt", version["recordedAt"] }, { "result", Result(version, attachment) } };
    internal static void Metadata(BsonDocument row, string operation, long auditId, long actorId, long grantVersion,
        AuthorizationCommandMetadata metadata, object body, DateTimeOffset now) {
        row.Add("operation", operation).Add("auditId", auditId).Add("actorId", actorId).Add("actorGrantVersion", grantVersion)
            .Add("requestKey", metadata.IdempotencyKey).Add("bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason))
            .Add("reason", metadata.Reason).Add("correlationId", metadata.CorrelationId).Add("recordedAt", now.UtcDateTime);
        row.Add("snapshotHash", Hash(row));
    }
    internal static BsonDocument PolicySnapshot(BsonDocument policy) => new() { { "id", policy["_id"] }, { "revision", policy["revision"] },
        { "scope", policy["scope"] }, { "decisionRef", policy["decisionRef"] }, { "decisionHash", policy["decisionHash"] },
        { "approvals", policy["approvals"] }, { "maximumSupplements", policy["maximumSupplements"] }, { "approvedAt", policy["approvedAt"] }, { "expiresAt", policy["expiresAt"] } };
    internal static ExpectedPublication Publication(BsonDocument value) => new(new(value["storeId"].AsString, value["reservationId"].AsInt64,
        value["attemptId"].AsInt64, value["attemptRevision"].AsInt64, value["stageToken"].AsString, value["generation"].AsString),
        value["contentRevision"].AsInt64, new(value["length"].AsInt64, value["sha256"].AsString));
}
