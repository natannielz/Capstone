using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;

namespace UnitToko.Platform.Billing;

// A request-local adapter binds the declaration AND separate supplement identity.
// Neither a route ID nor a PDF reference can widen authority to another owner.
internal sealed class PaymentSupplementEvidenceTarget(IMongoDatabase database, PaymentSupplementService owner,
    long declarationId, long supplementId, long ownRevision, bool review = false, bool metadataOnly = false,
    PaymentSupplementAssociation? pending = null) : IPaymentSupplementEvidenceUploadTarget
{
    public IMongoDatabase Database => database;
    public Task<T> WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) =>
        WithAsync(tx, actor, target, expectedRevision, null, false, action, ct);
    public Task<T> WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) =>
        WithAsync(tx, actor, target, expectedRevision, reference, true, action, ct);
    private async Task<T> WithAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
        EvidenceRef? reference, bool read, Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || target.Kind != EvidenceTargetKind.PaymentSupplement ||
            target.Id != supplementId || expectedRevision is { } requested && requested != ownRevision ||
            ownRevision is not (1 or 2) || !read && (review || metadataOnly) || metadataOnly && ownRevision != 1)
            throw PaymentSupplementRecords.Broken();
        async Task<T> Apply(PaymentSupplementAdmissionAuthority authority, CancellationToken token) {
            if (authority.Primary.DeclarationId != declarationId || authority.SupplementId != supplementId || authority.Revision != ownRevision ||
                !metadataOnly && authority.ApprovalUntil is null)
                throw new AuthorizationDeniedException("scope_denied");
            var scope = authority.Scope;
            return await action(new(target, new GrantScope(GrantScopeKind.FulfillmentShop, scope["organizationId"].AsInt64,
                ShopId: scope["shopId"].AsInt64, ChannelId: scope["channelId"].AsInt64), authority.Revision, null, null,
                SupplementReservationId: authority.ReservationId, SupplementId: authority.ReservationId is null ? null : authority.SupplementId,
                SupplementUploaderId: authority.ReservationId is null ? null : authority.UploaderId, OwnerApprovalUntil: authority.ApprovalUntil), token);
        }
        if (pending is not null) {
            if (read || review || metadataOnly || ownRevision != 1 || pending.SupplementId != supplementId || pending.Primary.DeclarationId != declarationId)
                throw PaymentSupplementRecords.Broken();
            return await Apply(await owner.PendingEvidenceAsync(tx, actor, pending, ct), ct);
        }
        return metadataOnly
            ? await owner.WithEvidenceMetadataAsync(tx, actor, declarationId, supplementId, ownRevision, Apply, ct)
            : await owner.WithAdmissionAsync(tx, actor, supplementId, ownRevision, read, review, reference, Apply, ct);
    }
}
