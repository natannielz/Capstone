using UnitToko.Platform.Documents;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Billing;

// The implementation belongs to the existing private-evidence owner. Implementations
// must reconstruct actual persisted PaymentSupplementPdf and validated whole original;
// they may not infer it from caller MIME or adopt a PaymentDeclaration PDF reservation.
internal interface IPaymentSupplementEvidenceHistory
{
    Task<ExpectedPublication> RequireOriginalAsync(IClientSessionHandle tx, long supplementId, long reservationId,
        EvidenceRef reference, long uploaderId, CancellationToken ct);
    Task RequireHistoricalPinAsync(IClientSessionHandle tx, BsonDocument attachment, CancellationToken ct);
    // Delegates to the shared strict whole-PDF result/audit/read/receipt owner. Merely
    // observing a processingReceipt field is insufficient; this callback cannot be optional.
    // Called for every associated job. Pending/running may have no result, but all
    // present records must agree; completed requires the whole original result.
    Task RequireCompletedDocumentResultAsync(IClientSessionHandle tx, BsonDocument job, BsonDocument attachment, CancellationToken ct);
}
internal sealed record PaymentSupplementAdmissionAuthority(long SupplementId, long Revision, long UploaderId,
    PaymentSupplementPrimary Primary, BsonDocument Scope, long? ReservationId, EvidenceRef? Reference, DateTimeOffset? ApprovalUntil = null);
internal sealed record PaymentSupplementAssociation(long SupplementId, long Revision, long PinId, long UploaderId,
    long ActorGrantVersion, long ReservationId, EvidenceRef PdfEvidence, PaymentSupplementPrimary Primary,
    string PrimarySubmissionHash, string AttachmentHash, ExpectedPublication Original, DateTimeOffset AttachedAt, string Reason, string CorrelationId);
internal sealed record PaymentSupplementOcrSourceSnapshot(long SupplementId, long SupplementRevision,
    PaymentSupplementPrimary Primary, string PrimarySubmissionHash, long ReservationId, long PinId, long UploaderId,
    EvidenceRef PdfEvidence, string AttachmentHash, ExpectedPublication Original, BsonDocument Scope);

// Source operations are read-only, performed under a genuine shared Mongo transaction.
// No financial permission, approval, invoice issuance, primary-image update or stock write is exposed.
internal interface IPaymentSupplementOcrSource
{
    IMongoDatabase Database { get; }
    Task<PaymentSupplementOcrSourceSnapshot> RequireAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct);
}
