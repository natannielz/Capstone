using System.Diagnostics;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

internal sealed partial class EvidenceService
{
    internal Task<Action> PinSupplierDocumentAsync(IClientSessionHandle tx, ActorContext actor, long documentId, EvidenceReadClaim claim,
        VerifiedOriginalHandle handle, long verificationStarted, CancellationToken ct) => GuardAsync<Action>(tx, async () => {
        if (supplierDocumentTargets is null) throw Invalid();
        var association = await supplierDocumentTargets.RequireDocumentAssociationAsync(tx, actor, documentId, claim, ct);
        if (association.Id != documentId || claim.Target.Kind != EvidenceTargetKind.SupplierDocument || claim.Target.Id != association.DraftId ||
            claim.TargetRevision != association.DraftRevision) throw Invalid();
        return await PinVerifiedAsync(tx, actor, claim, handle, verificationStarted, new(association.Id, association.PinId, association.Owner,
            association.DraftRevision, association.Hash, association.Reason, association.CorrelationId, association.RecordedAt,
            new() { { "associationKind", "SupplierDocument" }, { "supplierDocumentDraftId", association.DraftId }, { "supplierDocumentDraftRevision", association.DraftRevision } }), ct);
    });
    internal EvidenceReadObserver? SupplierDocumentReadObserver { get; set; }
    internal Action<double, bool>? SupplierDocumentPinObserver { get; set; }

    internal async Task<ProtectedEvidenceRead> OpenSupplierDocumentReadAsync(ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        await SupplierDocumentEvidenceSchemas.RequireStorageAsync(database, ct); RequireStore();
        var started = Stopwatch.GetTimestamp();
        var claim = await unit.ExecuteAsync((tx, token) => AcquireSupplierDocumentReadAsync(tx, actor, target, expectedRevision, reservationId, reference, metadata, token), ct);
        VerifiedOriginalHandle? handle = null;
        try {
            handle = await OpenSupplierDocumentOriginalAsync(claim, ct);
            var window = await unit.ExecuteAsync((tx, token) => AuthorizeBoundReadAsync(tx, actor, claim, EvidenceTargetKind.SupplierDocument, handle, false, token), ct);
            var held = handle;
            var content = new EvidenceReadStream(held, clock, window, claim.RequestDuration - Stopwatch.GetElapsedTime(started),
                token => unit.ExecuteAsync((tx, inner) => AuthorizeBoundReadAsync(tx, actor, claim, EvidenceTargetKind.SupplierDocument, held, true, inner), token),
                () => ReleaseSupplierDocumentReadAsync(claim), ct);
            handle = null; return new(content, claim.Mime, claim.Original.Content.Length, claim.Reference);
        } catch {
            try { if (handle is not null) await handle.DisposeAsync(); } catch { }
            try { await ReleaseSupplierDocumentReadAsync(claim); } catch { }
            throw;
        }
    }

    // The real owner creates its pending receipt and association in the transaction.
    // Verify original bytes before that transaction, then fence authority and eligibility while pinning.
    internal async Task<T> WithSupplierDocumentPinAsync<T>(ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata,
        Func<EvidenceReadClaim, VerifiedOriginalHandle, long, CancellationToken, Task<T>> command, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(command);
        await SupplierDocumentEvidenceSchemas.RequireStorageAsync(database, ct); RequireStore();
        var started = Stopwatch.GetTimestamp();
        var claim = await unit.ExecuteAsync((tx, token) => AcquireSupplierDocumentReadAsync(tx, actor, target, expectedRevision, reservationId, reference, metadata, token), ct);
        VerifiedOriginalHandle? handle = null;
        try {
            handle = await OpenSupplierDocumentOriginalAsync(claim, ct);
            return await command(claim, handle, started, ct);
        } finally {
            try { if (handle is not null) await handle.DisposeAsync(); }
            finally {
                // Cleanup does not decide commit outcome; recover by the original operation key.
                try { await ReleaseSupplierDocumentReadAsync(claim); } catch { }
            }
        }
    }
    private Task<EvidenceReadClaim> AcquireSupplierDocumentReadAsync(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct) =>
        AcquireBoundReadAsync(tx, actor, target, EvidenceTargetKind.SupplierDocument, "supplier-document-evidence-view", expectedRevision, reservationId, reference, metadata, ct);

    private async Task<VerifiedOriginalHandle> OpenSupplierDocumentOriginalAsync(EvidenceReadClaim claim, CancellationToken ct) {
        var opened = await registration.Store!.OpenVerifiedAsync(claim.Original, claim.VerificationBudget, ct);
        var handle = opened is OpenOutcome.Opened success ? success.Handle : throw new PersistenceConflictException("evidence_read_integrity_unavailable", "The exact original cannot be safely read");
        try {
            if (SupplierDocumentReadObserver is { } observer) await observer.Verified();
            return handle;
        } catch { await handle.DisposeAsync(); throw; }
    }
    private async Task ReleaseSupplierDocumentReadAsync(EvidenceReadClaim claim) {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await unit.ExecuteAsync(async (tx, token) => { await ReleaseReadAsync(tx, claim, token); return true; }, cleanup.Token);
    }
}
