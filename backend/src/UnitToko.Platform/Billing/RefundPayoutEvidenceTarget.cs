using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Documents;
using B = UnitToko.Platform.Billing.RefundPayoutBson;
using R = UnitToko.Platform.Billing.RefundPayoutRecords;

namespace UnitToko.Platform.Billing;

public sealed partial class RefundPayoutService : IRefundPayoutEvidenceUploadTarget
{
    IMongoDatabase IRefundPayoutEvidenceUploadTarget.Database => database;
    Task<T> IRefundPayoutEvidenceUploadTarget.WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) => WithEvidenceTargetAsync(tx, actor, target, expectedRevision, null, false, action, ct);
    Task<T> IRefundPayoutEvidenceUploadTarget.WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) => WithEvidenceTargetAsync(tx, actor, target, expectedRevision, reference, true, action, ct);
    private async Task<T> WithEvidenceTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference, bool read,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || target.Kind != EvidenceTargetKind.RefundPayout) throw B.Broken();
        var authority = await authorization.LoadAsync(actor, tx, ct);
        var (draft, frame) = await DraftFrameAsync(actor, authority, tx, target.Id, ct, documentAccess: true);
        if (expectedRevision.HasValue && expectedRevision != 1 || !read && !draft["recordedPayoutId"].IsBsonNull) throw R.Changed();
        BsonDocument? payout = null;
        if (!draft["recordedPayoutId"].IsBsonNull) {
            payout = await records.PayoutAsync(tx, draft["recordedPayoutId"].AsInt64, false, ct);
            if (reference is not null) R.Equal(payout["evidence"], PaymentSubmissionBson.Reference(reference));
        }
        var party = draft["party"];
        var owner = new GrantScope(GrantScopeKind.FulfillmentShop, party["fulfillmentOrganizationId"].AsInt64, ShopId: party["shopId"].AsInt64, ChannelId: party["channelId"].AsInt64);
        var result = await action(new(target, owner, 1, null, null,
            PayoutReservationId: payout?["reservationId"].AsInt64, PayoutId: payout?["_id"].AsInt64, PayoutUploaderId: payout?["recordedBy"].AsInt64), ct);
        await records.TouchAsync(tx, B.Drafts, draft, ct); await FenceAsync(frame, ct); return result;
    }
    async Task<RefundPayoutEvidenceAssociation> IRefundPayoutEvidenceUploadTarget.RequirePayoutAssociationAsync(IClientSessionHandle tx, ActorContext actor, long payoutId, EvidenceReadClaim claim, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || claim.Target.Kind != EvidenceTargetKind.RefundPayout || claim.TargetRevision != 1) throw B.Broken();
        var row = await records.PayoutAsync(tx, payoutId, true, ct);
        R.Equal(row["draftId"], claim.Target.Id); R.Equal(row["actorId"], actor.UserId); R.Equal(row["actorGrantVersion"], actor.GrantVersion);
        R.Equal(row["reservationId"], claim.ReservationId); R.Equal(row["evidence"], PaymentSubmissionBson.Reference(claim.Reference));
        var party = row["party"];
        return new(payoutId, row["pinId"].AsInt64, row["draftId"].AsInt64, 1, row["snapshotHash"].AsString, row["reason"].AsString, row["correlationId"].AsString,
            new(row["recordedAt"].ToUniversalTime()), new(GrantScopeKind.FulfillmentShop, party["fulfillmentOrganizationId"].AsInt64, ShopId: party["shopId"].AsInt64, ChannelId: party["channelId"].AsInt64));
    }
}
