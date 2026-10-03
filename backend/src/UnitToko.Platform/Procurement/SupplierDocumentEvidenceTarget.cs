using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierDocumentService : ISupplierDocumentEvidenceUploadTarget
{
    private sealed class SubmittingEvidenceTarget(SupplierDocumentService owner, Context context, long documentId, string hash) : ISupplierDocumentEvidenceUploadTarget {
        public IMongoDatabase Database => owner.database;
        public Task<T> WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) =>
            ((ISupplierDocumentEvidenceUploadTarget)owner).WithTargetAsync(tx, actor, target, expectedRevision, action, ct);
        public Task<T> WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference, Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) =>
            ((ISupplierDocumentEvidenceUploadTarget)owner).WithReadTargetAsync(tx, actor, target, expectedRevision, reference, action, ct);
        public async Task<SupplierDocumentEvidenceAssociation> RequireDocumentAssociationAsync(IClientSessionHandle tx, ActorContext actor, long id, EvidenceReadClaim claim, CancellationToken ct) {
            if (!ReferenceEquals(tx, context.Tx) || actor != context.Actor || id != documentId || context.Submission is null ||
                context.Submission["snapshotHash"] != hash || B.Hash(context.Submission) != hash) throw B.Broken();
            owner.Current(context);
            var row = await owner.records.SubmissionAsync(tx, id, ct, context.Draft, pendingPin: true);
            SupplierDocumentRecords.Equal(row, context.Submission);
            var result = Association(actor, row, claim); owner.Current(context); ct.ThrowIfCancellationRequested(); return result;
        }
    }
    IMongoDatabase ISupplierDocumentEvidenceUploadTarget.Database => database;
    Task<T> ISupplierDocumentEvidenceUploadTarget.WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) => WithEvidenceTargetAsync(tx, actor, target, expectedRevision, null, false, action, ct);
    Task<T> ISupplierDocumentEvidenceUploadTarget.WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) => WithEvidenceTargetAsync(tx, actor, target, expectedRevision, reference, true, action, ct);
    private async Task<T> WithEvidenceTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference, bool read,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || target.Kind != EvidenceTargetKind.SupplierDocument || expectedRevision is not (null or 1)) throw B.Broken();
        var authority = await authorization.LoadAsync(actor, tx, ct);
        var context = await DraftContextAsync(actor, authority, tx, target.Id, read ? Permissions.ProcurementSupplierDocumentEvidenceRead : Permissions.ProcurementSupplierDocumentRecord, false, ct);
        var draft = context.Draft!; var document = context.Submission;
        if (!read && document is not null) throw B.Changed();
        if (document is null && draft["actorId"] != actor.UserId) throw new AuthorizationDeniedException("scope_denied");
        if (document is not null && reference is not null) SupplierDocumentRecords.Equal(document["evidence"], PaymentSubmissionBson.Reference(reference));
        var location = context.Location;
        var result = await action(new(target, new(GrantScopeKind.FulfillmentShop, location.OrganizationId, ShopId: location.ShopId, ChannelId: location.ChannelId), 1, null, null,
            SupplierDocumentReservationId: document?["reservationId"].AsInt64, SupplierDocumentId: document?["_id"].AsInt64, SupplierDocumentUploaderId: document?["actorId"].AsInt64), ct);
        await FenceAsync(context, ct); return result;
    }
    private static SupplierDocumentEvidenceAssociation Association(ActorContext actor, BsonDocument row, EvidenceReadClaim claim) {
        if (claim.Target.Kind != EvidenceTargetKind.SupplierDocument || claim.TargetRevision != 1) throw B.Broken();
        SupplierDocumentRecords.Equal(row["draftId"], claim.Target.Id); SupplierDocumentRecords.Equal(row["actorId"], actor.UserId); SupplierDocumentRecords.Equal(row["actorGrantVersion"], actor.GrantVersion);
        SupplierDocumentRecords.Equal(row["reservationId"], claim.ReservationId); SupplierDocumentRecords.Equal(row["evidence"], PaymentSubmissionBson.Reference(claim.Reference));
        var location = R.Location(row["location"]);
        return new(row["_id"].AsInt64, row["pinId"].AsInt64, row["draftId"].AsInt64, 1, row["snapshotHash"].AsString, row["reason"].AsString, row["correlationId"].AsString,
            new(row["recordedAt"].ToUniversalTime()), new(GrantScopeKind.FulfillmentShop, location.OrganizationId, ShopId: location.ShopId, ChannelId: location.ChannelId));
    }
    async Task<SupplierDocumentEvidenceAssociation> ISupplierDocumentEvidenceUploadTarget.RequireDocumentAssociationAsync(IClientSessionHandle tx, ActorContext actor, long documentId, EvidenceReadClaim claim, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        var authority = await authorization.LoadAsync(actor, tx, ct);
        var context = await DraftContextAsync(actor, authority, tx, claim.Target.Id, Permissions.ProcurementSupplierDocumentRecord, false, ct);
        if (context.Submission is null || context.Submission["_id"] != documentId) throw B.Broken();
        var result = Association(actor, context.Submission, claim); await FenceAsync(context, ct); return result;
    }
}
