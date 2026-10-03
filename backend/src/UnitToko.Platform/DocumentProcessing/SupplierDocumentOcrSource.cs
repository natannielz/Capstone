using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed record SupplierDocumentOcrSourceSnapshot(long SubmissionId, long DraftId, long DraftRevision,
    long ReservationId, long PinId, long UploaderId, EvidenceRef Evidence, string SubmissionHash, string DraftHash, GrantScope Owner);
internal interface ISupplierDocumentOcrSource
{
    IMongoDatabase Database { get; }
    Task<SupplierDocumentOcrSourceSnapshot> RequireAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct);
}
internal sealed record SupplierDocumentProcessingOriginal(SupplierDocumentOcrSourceSnapshot Source, ExpectedPublication Publication,
    StorageIoBudget Budget, BsonDocument Binding, DateTimeOffset Until);
