using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using B = UnitToko.Platform.Procurement.ProcurementInspectionBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementInspectionService : IProcurementInspectionEvidenceUploadTarget
{
    private sealed class SubmittingEvidenceTarget(ProcurementInspectionService owner, Context context, long inspectionId, string hash) : IProcurementInspectionEvidenceUploadTarget {
        public IMongoDatabase Database => owner.database;
        public Task<T> WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) =>
            ((IProcurementInspectionEvidenceUploadTarget)owner).WithTargetAsync(tx, actor, target, expectedRevision, action, ct);
        public Task<T> WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference, Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) =>
            ((IProcurementInspectionEvidenceUploadTarget)owner).WithReadTargetAsync(tx, actor, target, expectedRevision, reference, action, ct);
        public async Task<ProcurementInspectionEvidenceAssociation> RequireInspectionAssociationAsync(IClientSessionHandle tx, ActorContext actor, long id, EvidenceReadClaim claim, CancellationToken ct) {
            if (!ReferenceEquals(tx, context.Tx) || actor != context.Actor || id != inspectionId || context.Submission is null ||
                context.Submission["snapshotHash"] != hash || B.Hash(context.Submission) != hash) throw B.Broken();
            owner.Current(context);
            var row = await owner.records.RowAsync(tx, B.Submissions, id, ct); ProcurementInspectionRecords.Equal(row, context.Submission);
            await owner.records.SubmissionAsync(tx, row, context.Draft!, context.History!.Source, true, ct, completedReceipt: true);
            var result = Association(actor, row, claim); owner.Current(context); ct.ThrowIfCancellationRequested(); return result;
        }
    }
    IMongoDatabase IProcurementInspectionEvidenceUploadTarget.Database => database;
    Task<T> IProcurementInspectionEvidenceUploadTarget.WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) => WithEvidenceTargetAsync(tx, actor, target, expectedRevision, null, false, action, ct);
    Task<T> IProcurementInspectionEvidenceUploadTarget.WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) => WithEvidenceTargetAsync(tx, actor, target, expectedRevision, reference, true, action, ct);
    private async Task<T> WithEvidenceTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference, bool read,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || target.Kind != EvidenceTargetKind.ProcurementInspection || expectedRevision is not (null or 1)) throw B.Broken();
        var authority = await authorization.LoadAsync(actor, tx, ct);
        var context = await DraftContextAsync(actor, authority, tx, target.Id, read ? Permissions.ProcurementInspectionEvidenceRead : Permissions.ProcurementInspect, false, ct);
        var draft = context.Draft!; var inspection = context.Submission;
        if (!read && inspection is not null) throw B.Changed();
        if (inspection is null && draft["actorId"] != actor.UserId) throw new AuthorizationDeniedException("scope_denied");
        if (inspection is not null && reference is not null) ProcurementInspectionRecords.Equal(inspection["evidence"], PaymentSubmissionBson.Reference(reference));
        var location = context.Location;
        var result = await action(new(target, new(GrantScopeKind.FulfillmentShop, location.OrganizationId, ShopId: location.ShopId, ChannelId: location.ChannelId), 1, null, null,
            InspectionReservationId: inspection?["reservationId"].AsInt64, InspectionId: inspection?["_id"].AsInt64, InspectionUploaderId: inspection?["actorId"].AsInt64), ct);
        await FenceAsync(context, ct); return result;
    }
    private static ProcurementInspectionEvidenceAssociation Association(ActorContext actor, BsonDocument row, EvidenceReadClaim claim) {
        if (claim.Target.Kind != EvidenceTargetKind.ProcurementInspection || claim.TargetRevision != 1) throw B.Broken();
        ProcurementInspectionRecords.Equal(row["draftId"], claim.Target.Id); ProcurementInspectionRecords.Equal(row["actorId"], actor.UserId); ProcurementInspectionRecords.Equal(row["actorGrantVersion"], actor.GrantVersion);
        ProcurementInspectionRecords.Equal(row["reservationId"], claim.ReservationId); ProcurementInspectionRecords.Equal(row["evidence"], PaymentSubmissionBson.Reference(claim.Reference));
        var location = R.Location(row["location"]);
        return new(row["_id"].AsInt64, row["pinId"].AsInt64, row["draftId"].AsInt64, 1, row["snapshotHash"].AsString, row["reason"].AsString, row["correlationId"].AsString,
            new(row["recordedAt"].ToUniversalTime()), new(GrantScopeKind.FulfillmentShop, location.OrganizationId, ShopId: location.ShopId, ChannelId: location.ChannelId));
    }
    async Task<ProcurementInspectionEvidenceAssociation> IProcurementInspectionEvidenceUploadTarget.RequireInspectionAssociationAsync(IClientSessionHandle tx, ActorContext actor, long inspectionId, EvidenceReadClaim claim, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        var authority = await authorization.LoadAsync(actor, tx, ct);
        var context = await DraftContextAsync(actor, authority, tx, claim.Target.Id, Permissions.ProcurementInspect, false, ct);
        if (context.Submission is null || context.Submission["_id"] != inspectionId) throw B.Broken();
        var result = Association(actor, context.Submission, claim); await FenceAsync(context, ct); return result;
    }
}
