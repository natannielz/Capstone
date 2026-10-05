using MongoDB.Bson;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierDocumentEvidenceService
{
    // The existing facade supplies its genuinely activated private store to the
    // owning match transaction. It adds no reader or separate owner fence.
    public Task<BsonDocument> PreviewMatchAsync(ActorContext actor, SupplierDocumentMatchInput input, CancellationToken ct = default) =>
        documents.PreviewMatchWithEvidenceAsync(actor, input, registration, ct);

    public Task<BsonDocument> MatchAsync(ActorContext actor, SupplierDocumentMatchInput input, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        documents.MatchWithEvidenceAsync(actor, input, binding, metadata, registration, ct);
}
