using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed record PaymentOcrSourceSnapshot(long SubmissionId, long DeclarationId, long DeclarationVersionId, long DeclarationRevision,
    long ReviewRevision, long ReservationId, long PinId, long UploaderId, EvidenceRef Evidence, string SubmissionHash, GrantScope Owner);
internal interface IPaymentOcrSource
{
    IMongoDatabase Database { get; }
    Task<PaymentOcrSourceSnapshot> RequireAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct);
}
