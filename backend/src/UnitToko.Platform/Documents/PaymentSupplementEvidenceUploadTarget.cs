using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Documents;

internal interface IPaymentSupplementEvidenceUploadTarget
{
    IMongoDatabase Database { get; }
    Task<T> WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct);
    Task<T> WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct);
}
