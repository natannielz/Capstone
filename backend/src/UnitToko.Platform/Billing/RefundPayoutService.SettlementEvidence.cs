using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Billing.RefundPayoutBson;
using R = UnitToko.Platform.Billing.RefundPayoutRecords;

namespace UnitToko.Platform.Billing;

public sealed partial class RefundPayoutService
{
    // The verifier's authority is independent of the draft recorder's admission. The
    // exact recorded payout and immutable private pin are the only readable target.
    internal async Task<T> WithSettlementEvidenceTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, long payoutId, long reservationId,
        EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference, Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) {
        BankTransactionClaims.RequireTransaction(database, tx);
        if (payoutId <= 0 || reservationId <= 0 || target.Kind != EvidenceTargetKind.RefundPayout || expectedRevision != 1 || reference is null) throw R.Changed();
        var authority = await authorization.LoadAsync(actor, tx, ct);
        var payout = await records.PayoutAsync(tx, payoutId, false, ct);
        R.Equal(payout["draftId"], target.Id); R.Equal(payout["draftRevision"], 1L);
        R.Equal(payout["reservationId"], reservationId); R.Equal(payout["evidence"], PaymentSubmissionBson.Reference(reference));
        var resources = PaymentDraftBson.Scopes(payout["scopes"].AsBsonArray); _ = RequireSettlement(actor, authority, resources);
        var approval = await approvals.PayoutEvidenceSourceAsync(actor, authority, tx, payout["financialApprovalId"].AsInt64, ct);
        R.Equal(payout["source"]["approval"], ApprovalSnapshot(approval)); R.Equal(payout["scopes"], approval.Invoice["scopes"]);
        var draft = await records.DraftAsync(tx, target.Id, ct);
        R.Equal(draft["recordedPayoutId"], payoutId); R.Equal(draft["snapshotHash"], payout["draftHash"]);
        var recorder = payout["recordedBy"].AsInt64;
        var people = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [.. new[] { actor.UserId, recorder }.Distinct().Order()], ct);
        if (actor.UserId == recorder || !PersonIndependencePolicy.AreIndependent(people.Evidence.Single(person => person.UserId == actor.UserId),
            people.Evidence.Single(person => person.UserId == recorder))) throw new AuthorizationDeniedException("independent_person_required");
        var recordedPerson = payout["people"].AsBsonArray.Select(ReceivingAccountBson.Person).Single(person => person.UserId == recorder);
        R.Equal(people.Evidence.Single(person => person.UserId == recorder).PersonId ?? throw B.Broken(), recordedPerson.PersonId ?? throw B.Broken());
        void Current() {
            _ = RequireSettlement(actor, authority, resources); approval.RequireCurrent(); people.RequireCurrent(tx, clock.UtcNow); ct.ThrowIfCancellationRequested();
        }
        Current();
        var party = payout["party"];
        var owner = new GrantScope(GrantScopeKind.FulfillmentShop, party["fulfillmentOrganizationId"].AsInt64, ShopId: party["shopId"].AsInt64, ChannelId: party["channelId"].AsInt64);
        var result = await action(new(target, owner, 1, null, null, PayoutReservationId: reservationId, PayoutId: payoutId, PayoutUploaderId: recorder), ct);
        await approval.FenceAsync(ct);
        _ = await BankReceiptService.ReadPayoutBankHistoryAsync(database, tx, payout["source"]["payingBank"].AsBsonDocument,
            new(draft["createdAt"].ToUniversalTime()), ct);
        await new PersonAuthorizationFence(database, clock).TouchAsync(tx, people, ct);
        await SettlementReadAuthorityFenceAsync(actor, authority, tx, resources, ct);
        var actual = await records.PayoutAsync(tx, payoutId, false, ct); R.Equal(B.Snapshot(actual), B.Snapshot(payout));
        await records.TouchAsync(tx, B.Payouts, actual, ct); await records.TouchAsync(tx, B.Drafts, draft, ct);
        Current(); return result;
    }
}

// This real Billing owner can only read an existing recorded original. It cannot
// admit uploads, recover recorder writes, or create a new payout association.
internal sealed class RefundSettlementEvidenceTarget(IMongoDatabase database, RefundPayoutService payouts, long payoutId, long reservationId) : IRefundPayoutEvidenceUploadTarget
{
    public IMongoDatabase Database => database;
    public Task<T> WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) => throw R.Changed();
    public Task<RefundPayoutEvidenceAssociation> RequirePayoutAssociationAsync(IClientSessionHandle tx, ActorContext actor, long id, EvidenceReadClaim claim, CancellationToken ct) => throw R.Changed();
    public Task<T> WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) =>
        payouts.WithSettlementEvidenceTargetAsync(tx, actor, payoutId, reservationId, target, expectedRevision, reference, action, ct);
}
