using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using B = UnitToko.Platform.Procurement.ProcurementDisbursementBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementDisbursementService : IProcurementDisbursementEvidenceUploadTarget
{
    // Issued only for this command's pending record. No verified graph is shared across transactions.
    private sealed class RecordingEvidenceTarget(ProcurementDisbursementService owner, Context context, long recordId) : IProcurementDisbursementEvidenceUploadTarget {
        private readonly long transaction = context.Tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        public IMongoDatabase Database => owner.database;
        public Task<T> WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
            Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) =>
            ((IProcurementDisbursementEvidenceUploadTarget)owner).WithTargetAsync(tx, actor, target, expectedRevision, action, ct);
        public Task<T> WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference,
            Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) =>
            ((IProcurementDisbursementEvidenceUploadTarget)owner).WithReadTargetAsync(tx, actor, target, expectedRevision, reference, action, ct);
        public Task<ProcurementDisbursementEvidenceAssociation> RequireDisbursementAssociationAsync(IClientSessionHandle tx, ActorContext actor,
            long disbursementId, EvidenceReadClaim claim, CancellationToken ct) {
            if (!ReferenceEquals(tx, context.Tx) || actor != context.Actor || !tx.IsInTransaction || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction ||
                disbursementId != recordId || context.Proof is null) throw B.Broken();
            owner.Current(context);
            return owner.DisbursementAssociationAsync(tx, actor, disbursementId, claim, ct, context.Proof);
        }
    }
    IMongoDatabase IProcurementDisbursementEvidenceUploadTarget.Database => database;
    Task<T> IProcurementDisbursementEvidenceUploadTarget.WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) => WithEvidenceTargetAsync(tx, actor, target, expectedRevision, null, false, action, ct);
    Task<T> IProcurementDisbursementEvidenceUploadTarget.WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) => WithEvidenceTargetAsync(tx, actor, target, expectedRevision, reference, true, action, ct);
    private async Task<T> WithEvidenceTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference, bool read,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || target.Kind != EvidenceTargetKind.ProcurementDisbursement || expectedRevision is not (null or 1)) throw B.Broken();
        var authority = await authorization.LoadAsync(actor, tx, ct);
        var route = await Rows(B.Drafts).Find(tx, new BsonDocument("_id", target.Id)).Project(new BsonDocument { { "location", 1 }, { "recordedDisbursementId", 1 } }).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var permission = read ? Permissions.ProcurementDisbursementEvidenceRead : Permissions.DisbursementRecord;
        _ = ContextFor(actor, authority, tx, ProcurementRequestBson.Location(route["location"]), permission);
        var context = route["recordedDisbursementId"].IsBsonNull
            ? await CurrentDraftContextAsync(actor, authority, tx, target.Id, permission, ct)
            : await HistoricalContextAsync(actor, authority, tx, target.Id, permission, ct);
        var draft = context.Draft!; BsonDocument? record = null;
        if (!read && draft["actorId"] != actor.UserId) throw new AuthorizationDeniedException("scope_denied");
        if (!draft["recordedDisbursementId"].IsBsonNull) {
            if (!read) throw B.Changed();
            record = context.Capacity?.Claims.GetValueOrDefault(draft["recordedDisbursementId"].AsInt64) ?? throw B.Broken();
            if (reference is not null) ProcurementDisbursementRecords.Equal(record["evidence"], PaymentSubmissionBson.Reference(reference));
        } else {
            // Upload and draft-original access belong to the original recorder; reviewers read the pinned recorded evidence.
            if (draft["actorId"] != actor.UserId) throw new AuthorizationDeniedException("scope_denied");
        }
        var location = context.Location; var owner = new GrantScope(GrantScopeKind.FulfillmentShop, location.OrganizationId, ShopId: location.ShopId, ChannelId: location.ChannelId);
        var result = await action(new(target, owner, 1, null, null, DisbursementReservationId: record?["reservationId"].AsInt64,
            DisbursementId: record?["_id"].AsInt64, DisbursementUploaderId: record?["actorId"].AsInt64), ct);
        await FenceAsync(context, ct); return result;
    }
    Task<ProcurementDisbursementEvidenceAssociation> IProcurementDisbursementEvidenceUploadTarget.RequireDisbursementAssociationAsync(IClientSessionHandle tx, ActorContext actor,
        long disbursementId, EvidenceReadClaim claim, CancellationToken ct) => DisbursementAssociationAsync(tx, actor, disbursementId, claim, ct);
    private async Task<ProcurementDisbursementEvidenceAssociation> DisbursementAssociationAsync(IClientSessionHandle tx, ActorContext actor,
        long disbursementId, EvidenceReadClaim claim, CancellationToken ct, ProcurementDisbursementProof? proof = null) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || claim.Target.Kind != EvidenceTargetKind.ProcurementDisbursement || claim.TargetRevision != 1) throw B.Broken();
        var row = await records.RecordAsync(tx, disbursementId, true, ct, proof);
        ProcurementDisbursementRecords.Equal(row["draftId"], claim.Target.Id); ProcurementDisbursementRecords.Equal(row["actorId"], actor.UserId); ProcurementDisbursementRecords.Equal(row["actorGrantVersion"], actor.GrantVersion);
        ProcurementDisbursementRecords.Equal(row["reservationId"], claim.ReservationId); ProcurementDisbursementRecords.Equal(row["evidence"], PaymentSubmissionBson.Reference(claim.Reference));
        var location = ProcurementRequestBson.Location(row["location"]);
        return new(disbursementId, row["pinId"].AsInt64, row["draftId"].AsInt64, 1, row["snapshotHash"].AsString, row["reason"].AsString, row["correlationId"].AsString,
            new(row["recordedAt"].ToUniversalTime()), new(GrantScopeKind.FulfillmentShop, location.OrganizationId, ShopId: location.ShopId, ChannelId: location.ChannelId));
    }
}
