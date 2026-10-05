using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierDocumentService
{
    private void CheckPdfMatchWindow(DateTimeOffset deadline, DateTimeOffset? capturedUntil) {
        if (clock.UtcNow >= deadline || capturedUntil is { } until && clock.UtcNow >= until)
            throw new PersistenceConflictException("evidence_deadline_expired", "Reload the authorized original and reviewed PDF observations");
    }
    private async Task RequirePdfMatchAsync(Context context, SupplierDocumentMatchInput input, DateTimeOffset deadline,
        Action<DateTimeOffset> captureWindow, string correlationId, EvidenceStoreRegistration? registration, CancellationToken ct) {
        var observations = SupplierDocumentRules.CopyPdfObservations(input.PdfObservations) ?? throw B.Broken();
        context.IncludeEvidence = true; Current(context);
        var document = context.Submission ?? throw B.Broken();
        var reference = PaymentSubmissionBson.Reference(document["evidence"].AsBsonDocument);
        if (observations.Any(value => value.Candidate.Evidence != reference)) throw B.Changed();
        var evidence = new EvidenceService(database, unit, ids, clock, registration,
            supplierDocumentTargets: new MatchingEvidenceTarget(this, context));
        var check = await evidence.RequireSupplierDocumentCandidatesAsync(context.Tx, context.Actor,
            new(EvidenceTargetKind.SupplierDocument, context.Draft!["_id"].AsInt64), 1, reference,
            observations.Select(value => value.Candidate).ToArray(), new SupplierDocumentOcrSource(this), deadline, captureWindow,
            new("supplier-document-pdf-ocr-view", correlationId), ct);
        var requestCheck = context.PdfEligibility;
        context.PdfEligibility = () => { requestCheck?.Invoke(); check(); };
        Current(context);
    }
    // Only the existing owning match Context can use this adapter. The outer
    // preview/command owns its source/head/person/grant fence; no nested owner
    // touches the same head using a separately captured snapshot.
    private sealed class MatchingEvidenceTarget(SupplierDocumentService owner, Context context) : ISupplierDocumentEvidenceUploadTarget
    {
        public IMongoDatabase Database => owner.database;
        public Task<T> WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
            Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) => throw B.Broken();
        public Task<SupplierDocumentEvidenceAssociation> RequireDocumentAssociationAsync(IClientSessionHandle tx, ActorContext actor,
            long documentId, EvidenceReadClaim claim, CancellationToken ct) => throw B.Broken();
        public async Task<T> WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
            EvidenceRef? reference, Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) {
            if (!ReferenceEquals(tx, context.Tx) || actor != context.Actor || context.Permission != UnitToko.Modules.Identity.Permissions.ProcurementSupplierDocumentMatch ||
                target != new EvidenceTargetRef(EvidenceTargetKind.SupplierDocument, context.Draft!["_id"].AsInt64) ||
                expectedRevision != 1 || reference is null || context.Submission is null) throw B.Broken();
            context.IncludeEvidence = true; owner.Current(context);
            var document = context.Submission;
            SupplierDocumentRecords.Equal(document["evidence"], PaymentSubmissionBson.Reference(reference));
            var location = context.Location;
            var result = await action(new(target,
                new GrantScope(GrantScopeKind.FulfillmentShop, location.OrganizationId, ShopId: location.ShopId, ChannelId: location.ChannelId),
                1, null, null, SupplierDocumentReservationId: document["reservationId"].AsInt64,
                SupplierDocumentId: document["_id"].AsInt64, SupplierDocumentUploaderId: document["actorId"].AsInt64), ct);
            owner.Current(context); ct.ThrowIfCancellationRequested(); return result;
        }
    }
}
