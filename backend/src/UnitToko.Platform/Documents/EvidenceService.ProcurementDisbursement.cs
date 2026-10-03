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
    internal Task<Action> PinProcurementDisbursementAsync(IClientSessionHandle tx, ActorContext actor, long disbursementId, EvidenceReadClaim claim,
        VerifiedOriginalHandle handle, long verificationStarted, CancellationToken ct) => GuardAsync<Action>(tx, async () => {
        if (procurementDisbursementTargets is null) throw Invalid();
        var association = await procurementDisbursementTargets.RequireDisbursementAssociationAsync(tx, actor, disbursementId, claim, ct);
        if (association.Id != disbursementId || claim.Target.Kind != EvidenceTargetKind.ProcurementDisbursement || claim.Target.Id != association.DraftId ||
            claim.TargetRevision != association.DraftRevision) throw Invalid();
        return await PinVerifiedAsync(tx, actor, claim, handle, verificationStarted, new(association.Id, association.PinId, association.Owner,
            association.DraftRevision, association.Hash, association.Reason, association.CorrelationId, association.RecordedAt,
            new() { { "associationKind", "ProcurementDisbursement" }, { "procurementDisbursementDraftId", association.DraftId }, { "procurementDisbursementDraftRevision", association.DraftRevision } }), ct);
    });
    internal EvidenceReadObserver? ProcurementDisbursementReadObserver { get; set; }
    internal Action<double, bool>? ProcurementDisbursementPinObserver { get; set; }
    internal Task<ProtectedEvidenceRead> OpenProcurementDisbursementReadAsync(ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) =>
        OpenBoundProcurementDisbursementReadAsync(actor, (tx, token) => AcquireProcurementDisbursementReadAsync(tx, actor, target, expectedRevision, reservationId, reference, metadata, token), ct);
    private async Task<ProtectedEvidenceRead> OpenBoundProcurementDisbursementReadAsync(ActorContext actor,
        Func<IClientSessionHandle, CancellationToken, Task<EvidenceReadClaim>> acquire, CancellationToken ct) {
        await ProcurementDisbursementEvidenceSchemas.RequireStorageAsync(database, ct); RequireStore();
        var started = Stopwatch.GetTimestamp();
        var claim = await unit.ExecuteAsync(acquire, ct);
        VerifiedOriginalHandle? handle = null;
        try {
            handle = await OpenProcurementDisbursementOriginalAsync(claim, ct);
            var window = await unit.ExecuteAsync((tx, token) => AuthorizeProcurementDisbursementReadAsync(tx, actor, claim, handle, false, token), ct);
            var held = handle;
            var content = new EvidenceReadStream(held, clock, window, claim.RequestDuration - Stopwatch.GetElapsedTime(started),
                token => unit.ExecuteAsync((tx, inner) => AuthorizeProcurementDisbursementReadAsync(tx, actor, claim, held, true, inner), token),
                () => ReleaseProcurementDisbursementReadAsync(claim), ct);
            handle = null; return new(content, claim.Mime, claim.Original.Content.Length, claim.Reference);
        } catch {
            try { if (handle is not null) await handle.DisposeAsync(); } catch { }
            try { await ReleaseProcurementDisbursementReadAsync(claim); } catch { }
            throw;
        }
    }
    // Filesystem verification is outside all retrying transactions. The owning Procurement
    // command supplies the exact immutable pending association, then invokes PinProcurementDisbursementAsync.
    internal async Task<T> WithProcurementDisbursementPinAsync<T>(ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata,
        Func<EvidenceReadClaim, VerifiedOriginalHandle, long, CancellationToken, Task<T>> command, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(command);
        await ProcurementDisbursementEvidenceSchemas.RequireStorageAsync(database, ct); RequireStore();
        var started = Stopwatch.GetTimestamp();
        var claim = await unit.ExecuteAsync((tx, token) => AcquireProcurementDisbursementReadAsync(tx, actor, target, expectedRevision, reservationId, reference, metadata, token), ct);
        VerifiedOriginalHandle? handle = null;
        try {
            handle = await OpenProcurementDisbursementOriginalAsync(claim, ct);
            return await command(claim, handle, started, ct);
        } finally {
            try { if (handle is not null) await handle.DisposeAsync(); }
            finally {
                // Neither close nor cleanup failure can infer whether the financial owner's
                // command committed. Its original operation key remains the sole recovery path.
                try { await ReleaseProcurementDisbursementReadAsync(claim); } catch { }
            }
        }
    }
    private async Task<VerifiedOriginalHandle> OpenProcurementDisbursementOriginalAsync(EvidenceReadClaim claim, CancellationToken ct) {
        var opened = await registration.Store!.OpenVerifiedAsync(claim.Original, claim.VerificationBudget, ct);
        var handle = opened is OpenOutcome.Opened success ? success.Handle : throw new PersistenceConflictException("evidence_read_integrity_unavailable", "The exact original cannot be safely read");
        try {
            if (ProcurementDisbursementReadObserver is { } observer) await observer.Verified();
            return handle;
        } catch { await handle.DisposeAsync(); throw; }
    }
    private async Task ReleaseProcurementDisbursementReadAsync(EvidenceReadClaim claim) {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await unit.ExecuteAsync(async (tx, token) => { await ReleaseReadAsync(tx, claim, token); return true; }, cleanup.Token);
    }
}
