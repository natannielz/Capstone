using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

internal sealed partial class EvidenceService
{
    internal Task<Action> PinPaymentAsync(IClientSessionHandle tx, ActorContext actor, long submissionId, EvidenceReadClaim claim,
        VerifiedOriginalHandle handle, long verificationStarted, CancellationToken ct) => GuardAsync<Action>(tx, async () => {
        if (paymentTargets is null) throw Invalid();
        // Only the owning command can produce a pending immutable association and its
        // receipt inside this transaction. A reference or uploaded file alone is insufficient.
        var association = await paymentTargets.RequireSubmissionAssociationAsync(tx, actor, submissionId, claim, ct);
        if (claim.Target.Kind != EvidenceTargetKind.PaymentDeclaration || claim.Target.Id != association.DeclarationId || claim.TargetRevision != association.DeclarationRevision) throw Invalid();
        return await PinVerifiedAsync(tx, actor, claim, handle, verificationStarted, new(association.Id, association.PinId, association.Owner,
            association.DeclarationRevision, association.Hash, association.Reason, association.CorrelationId, association.SubmittedAt,
            new() { { "associationKind", "PaymentSubmission" }, { "declarationId", association.DeclarationId }, { "declarationVersionId", association.DeclarationVersionId },
                { "declarationRevision", association.DeclarationRevision }, { "reviewRevision", association.ReviewRevision } }), ct);
    });
    internal Task<Action> PinRefundPayoutAsync(IClientSessionHandle tx, ActorContext actor, long payoutId, EvidenceReadClaim claim,
        VerifiedOriginalHandle handle, long verificationStarted, CancellationToken ct) => GuardAsync<Action>(tx, async () => {
        if (refundPayoutTargets is null) throw Invalid();
        var association = await refundPayoutTargets.RequirePayoutAssociationAsync(tx, actor, payoutId, claim, ct);
        if (association.Id != payoutId || claim.Target.Kind != EvidenceTargetKind.RefundPayout || claim.Target.Id != association.DraftId ||
            claim.TargetRevision != association.DraftRevision) throw Invalid();
        return await PinVerifiedAsync(tx, actor, claim, handle, verificationStarted, new(association.Id, association.PinId, association.Owner,
            association.DraftRevision, association.Hash, association.Reason, association.CorrelationId, association.RecordedAt,
            new() { { "associationKind", "RefundPayout" }, { "refundPayoutDraftId", association.DraftId }, { "refundPayoutDraftRevision", association.DraftRevision } }), ct);
    });
    private sealed record PinAssociation(long Id, long PinId, UnitToko.Modules.Organizations.GrantScope Owner, long TargetRevision,
        string Hash, string Reason, string CorrelationId, DateTimeOffset RecordedAt, BsonDocument Fields);
    private async Task<Action> PinVerifiedAsync(IClientSessionHandle tx, ActorContext actor, EvidenceReadClaim claim,
        VerifiedOriginalHandle handle, long verificationStarted, PinAssociation association, CancellationToken ct) {
        await RequireBindingAsync(tx, actor, claim.Target, claim.ReservationId, ct);
        var bundle = await LoadAsync(tx, claim.ReservationId, ct);
        if (IsPdf(bundle)) throw new UnitToko.Platform.Mongo.PersistenceConflictException("evidence_pdf_processing_not_connected",
            "PDF sudah dapat diunggah dan diperiksa keamanannya; pengajuan untuk peninjauan bisnis menunggu pemrosesan seluruh halaman PDF.");
        RequireAuthority(bundle, new(claim.Target, association.Owner, association.TargetRevision, null, null), actor, true);
        var lease = await LoadReadLeaseAsync(tx, claim, ct);
        DateTimeOffset? policyUntil = null;
        void Final() {
            FinalCheck(Date(lease["until"]), ct); FinalCheck(claim.Deadline, ct);
            if (policyUntil is { } until) FinalCheck(until, ct);
            if (claim.Target.Kind == EvidenceTargetKind.RefundPayout)
                RefundPayoutPinObserver?.Invoke(Stopwatch.GetElapsedTime(verificationStarted).TotalMilliseconds, handle.Stream.CanRead);
            if (claim.Target.Kind == EvidenceTargetKind.ProcurementDisbursement)
                ProcurementDisbursementPinObserver?.Invoke(Stopwatch.GetElapsedTime(verificationStarted).TotalMilliseconds, handle.Stream.CanRead);
            if (claim.Target.Kind == EvidenceTargetKind.ProcurementInspection)
                ProcurementInspectionPinObserver?.Invoke(Stopwatch.GetElapsedTime(verificationStarted).TotalMilliseconds, handle.Stream.CanRead);
            if (claim.Target.Kind == EvidenceTargetKind.SupplierDocument)
                SupplierDocumentPinObserver?.Invoke(Stopwatch.GetElapsedTime(verificationStarted).TotalMilliseconds, handle.Stream.CanRead);
            if (!handle.Stream.CanRead || Stopwatch.GetElapsedTime(claim.LeaseStarted) >= claim.InitialLeaseDuration ||
                Stopwatch.GetElapsedTime(verificationStarted) >= claim.RequestDuration) throw Conflict("evidence_read_lease_expired");
        }
        Final();
        if (lease["state"] != "provisional" || !lease["actor"].Equals(ReadActor(actor)) || lease["target"]["kind"] != claim.Target.Kind.ToString() || lease["target"]["id"] != claim.Target.Id ||
            lease["target"]["revision"] != claim.TargetRevision || Date(lease["deadline"]) != claim.Deadline || !lease["reference"].Equals(ReadReference(claim.Reference)) ||
            Reference(bundle) != claim.Reference || handle.Attempt != claim.Original.Attempt || handle.Content != claim.Original.Content || Publication(bundle) != claim.Original)
            throw Conflict("evidence_read_reference_changed");
        var eligible = await ReadEligibilityAsync(tx, bundle, ct);
        policyUntil = ReadUntil(eligible, claim.Deadline);
        if (lease["readPolicyId"] != eligible.Policy["_id"] || lease["readPolicyRevision"] != eligible.Policy["revision"] || lease["validationPolicyId"] != eligible.ValidationPolicy["_id"] ||
            lease["validationPolicyRevision"] != eligible.ValidationPolicy["revision"] || lease["validationHash"] != eligible.Validation["resultHash"] ||
            lease["lifecycleRevision"] != eligible.Lifecycle["revision"] || lease["versionRowId"] != bundle.Version["_id"] || lease["storeId"] != bundle.Version["storeId"] || lease["generation"] != bundle.Version["generation"])
            throw Conflict("evidence_read_reference_changed");
        // Activation compares the exact lease revision/fence and records its required audit,
        // serializing a pin with release or expiry without a second unaudited mutation.
        var active = Clone(lease); active["state"] = "active";
        await SaveReadLeaseAsync(tx, lease, active, "activated", ct);
        var row = new BsonDocument { { "_id", association.PinId }, { "schemaVersion", 1 }, { "associationId", association.Id },
            { "reference", ReadReference(claim.Reference) }, { "reservationId", claim.ReservationId }, { "versionRowId", bundle.Version["_id"] }, { "lifecycleRevision", eligible.Lifecycle["revision"] },
            { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "validationPolicyId", eligible.ValidationPolicy["_id"] }, { "validationPolicyRevision", eligible.ValidationPolicy["revision"] },
            { "readPolicyId", eligible.Policy["_id"] }, { "readPolicyRevision", eligible.Policy["revision"] }, { "storeId", bundle.Version["storeId"] }, { "generation", bundle.Version["generation"] },
            { "validationHash", eligible.Validation["resultHash"] }, { "associationHash", association.Hash }, { "createdAt", association.RecordedAt.UtcDateTime },
            { "reason", association.Reason }, { "correlationId", association.CorrelationId }, { "auditId", (await ids.NextAsync("evidencePinAudits", ct)).Value } };
        row.AddRange(association.Fields); row.Add("pinHash", EvidencePinRecords.Hash(row));
        await Rows("evidencePins").InsertOneAsync(tx, row, cancellationToken: ct);
        await Rows("evidencePinAudits").InsertOneAsync(tx, EvidencePinRecords.Audit(row), cancellationToken: ct);
        _ = await EvidencePinRecords.ReadAsync(database, tx, association.PinId, ct); Final(); return Final;
    }
}
