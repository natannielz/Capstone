using System.Diagnostics;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

internal sealed partial class EvidenceService
{
    internal Task<Action> PinPaymentSupplementAsync(IClientSessionHandle tx, ActorContext actor, PaymentSupplementAssociation association,
        EvidenceReadClaim claim, VerifiedOriginalHandle handle, long verificationStarted, CancellationToken ct) => GuardAsync<Action>(tx, async () => {
        if (paymentSupplementTargets is null || association.Revision != 2 || association.UploaderId != actor.UserId ||
            association.ActorGrantVersion != actor.GrantVersion || claim.Target.Kind != EvidenceTargetKind.PaymentSupplement ||
            claim.Target.Id != association.SupplementId || claim.TargetRevision != 1 || claim.ReservationId != association.ReservationId ||
            claim.Reference != association.PdfEvidence || claim.Original != association.Original)
            throw PaymentSupplementRecords.Broken();
        // The request-local pending adapter validates exact inserted version/attachment,
        // current primary, policy and actor without recursively reading unfinished history.
        return await WithTargetAsync<Action>(tx, actor, claim.Target, 1, async (authority, token) => {
            var approvalStarted = Stopwatch.GetTimestamp();
            var approvalDuration = authority.OwnerApprovalUntil is { } expires ? expires - clock.UtcNow : throw PaymentSupplementRecords.Broken();
            if (approvalDuration <= TimeSpan.Zero) throw Conflict("payment_supplement_policy_changed");
            var final = await PinVerifiedAsync(tx, actor, claim, handle,
            verificationStarted, new(association.SupplementId, association.PinId, authority.Owner, 1, association.AttachmentHash,
                association.Reason, association.CorrelationId, association.AttachedAt, new() {
                    { "associationKind", "PaymentSupplement" }, { "supplementId", association.SupplementId }, { "supplementRevision", 2L },
                    { "primary", PaymentSupplementBson.Primary(association.Primary) }, { "primarySubmissionHash", association.PrimarySubmissionHash } }), token);
            return () => {
                FinalCheck(authority.OwnerApprovalUntil, token);
                if (Stopwatch.GetElapsedTime(approvalStarted) >= approvalDuration) throw Conflict("payment_supplement_policy_changed");
                final();
            };
        }, ct);
    });
    internal async Task<ProtectedEvidenceRead> OpenPaymentSupplementReadAsync(ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata, bool review, CancellationToken ct)
    {
        if (expectedRevision != 2) throw PaymentDraftRecords.Changed();
        await PaymentSupplementEvidenceSchemas.RequireAsync(database, ct); RequireStore();
        var started = Stopwatch.GetTimestamp(); var purpose = review ? "payment-review-supplement-pdf-view" : "payment-supplement-pdf-view";
        var claim = await unit.ExecuteAsync((tx, token) => AcquireBoundReadAsync(tx, actor, target, EvidenceTargetKind.PaymentSupplement,
            purpose, expectedRevision, reservationId, reference, metadata, token), ct);
        VerifiedOriginalHandle? handle = null;
        try {
            handle = await OpenPaymentSupplementOriginalAsync(claim, ct);
            var window = await unit.ExecuteAsync((tx, token) => AuthorizeBoundReadAsync(tx, actor, claim, EvidenceTargetKind.PaymentSupplement, handle, false, token), ct);
            var held = handle;
            var content = new EvidenceReadStream(held, clock, window, claim.RequestDuration - Stopwatch.GetElapsedTime(started),
                token => unit.ExecuteAsync((tx, inner) => AuthorizeBoundReadAsync(tx, actor, claim, EvidenceTargetKind.PaymentSupplement, held, true, inner), token),
                () => ReleaseSupplierDocumentReadAsync(claim), ct);
            handle = null; return new(content, claim.Mime, claim.Original.Content.Length, claim.Reference);
        } catch {
            try { if (handle is not null) await handle.DisposeAsync(); } catch { }
            try { await ReleaseSupplierDocumentReadAsync(claim); } catch { }
            throw;
        }
    }
    internal async Task<T> WithPaymentSupplementPinAsync<T>(ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata,
        Func<EvidenceReadClaim, VerifiedOriginalHandle, long, CancellationToken, Task<T>> command, CancellationToken ct)
    {
        if (expectedRevision != 1) throw PaymentDraftRecords.Changed();
        await PaymentSupplementEvidenceSchemas.RequireAsync(database, ct); RequireStore();
        var started = Stopwatch.GetTimestamp();
        var claim = await unit.ExecuteAsync((tx, token) => AcquireBoundReadAsync(tx, actor, target, EvidenceTargetKind.PaymentSupplement,
            "payment-supplement-pdf-attach", expectedRevision, reservationId, reference, metadata, token), ct);
        VerifiedOriginalHandle? handle = null;
        try {
            handle = await OpenPaymentSupplementOriginalAsync(claim, ct);
            return await command(claim, handle, started, ct);
        } finally {
            try { if (handle is not null) await handle.DisposeAsync(); }
            finally { try { await ReleaseSupplierDocumentReadAsync(claim); } catch { } }
        }
    }
    internal async Task<EvidencePurpose> PaymentSupplementPurposeAsync(IClientSessionHandle tx, long supplementId, long reservationId,
        EvidenceRef reference, long uploaderId, CancellationToken ct)
    {
        var original = await SubmissionOriginalAsync(tx, new(EvidenceTargetKind.PaymentSupplement, supplementId), 1, reservationId, reference, uploaderId, ct);
        if (original is null) throw PaymentSupplementRecords.Broken();
        var bundle = await LoadAsync(tx, reservationId, ct);
        if (PersistedPurpose(bundle.Reservation) != EvidencePurpose.PaymentSupplementPdf) throw PaymentSupplementRecords.Broken();
        return EvidencePurpose.PaymentSupplementPdf;
    }
    private async Task<VerifiedOriginalHandle> OpenPaymentSupplementOriginalAsync(EvidenceReadClaim claim, CancellationToken ct)
    {
        if (claim.Target.Kind != EvidenceTargetKind.PaymentSupplement || claim.Mime != PdfDocumentExtractionContract.InputMime) throw PaymentSupplementRecords.Broken();
        var opened = await registration.Store!.OpenVerifiedAsync(claim.Original, claim.VerificationBudget, ct);
        return opened is OpenOutcome.Opened success ? success.Handle : throw new PersistenceConflictException("evidence_read_integrity_unavailable", "Dokumen PDF asli tidak dapat diverifikasi.");
    }
}
