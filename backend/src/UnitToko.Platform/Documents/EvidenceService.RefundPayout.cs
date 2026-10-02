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
    internal EvidenceReadObserver? RefundPayoutReadObserver { get; set; }
    internal Action<double, bool>? RefundPayoutPinObserver { get; set; }
    internal Task<ProtectedEvidenceRead> OpenRefundPayoutReadAsync(ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) =>
        OpenBoundRefundReadAsync(actor, (tx, token) => AcquireRefundPayoutReadAsync(tx, actor, target, expectedRevision, reservationId, reference, metadata, token), ct);
    internal Task<ProtectedEvidenceRead> OpenRefundSettlementReadAsync(ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) =>
        OpenBoundRefundReadAsync(actor, (tx, token) => AcquireRefundSettlementReadAsync(tx, actor, target, expectedRevision, reservationId, reference, metadata, token), ct);
    private async Task<ProtectedEvidenceRead> OpenBoundRefundReadAsync(ActorContext actor,
        Func<IClientSessionHandle, CancellationToken, Task<EvidenceReadClaim>> acquire, CancellationToken ct) {
        await RefundPayoutEvidenceSchemas.RequireStorageAsync(database, ct); RequireStore();
        var started = Stopwatch.GetTimestamp();
        var claim = await unit.ExecuteAsync(acquire, ct);
        VerifiedOriginalHandle? handle = null;
        try {
            handle = await OpenRefundPayoutOriginalAsync(claim, ct);
            var window = await unit.ExecuteAsync((tx, token) => AuthorizeRefundPayoutReadAsync(tx, actor, claim, handle, false, token), ct);
            var held = handle;
            var content = new EvidenceReadStream(held, clock, window, claim.RequestDuration - Stopwatch.GetElapsedTime(started),
                token => unit.ExecuteAsync((tx, inner) => AuthorizeRefundPayoutReadAsync(tx, actor, claim, held, true, inner), token),
                () => ReleaseRefundPayoutReadAsync(claim), ct);
            handle = null; return new(content, claim.Mime, claim.Original.Content.Length, claim.Reference);
        } catch {
            try { if (handle is not null) await handle.DisposeAsync(); } catch { }
            try { await ReleaseRefundPayoutReadAsync(claim); } catch { }
            throw;
        }
    }
    // Filesystem verification is outside all retrying transactions. The owning Billing
    // command supplies the exact immutable pending association, then invokes PinRefundPayoutAsync.
    internal async Task<T> WithRefundPayoutPinAsync<T>(ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata,
        Func<EvidenceReadClaim, VerifiedOriginalHandle, long, CancellationToken, Task<T>> command, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(command);
        await RefundPayoutEvidenceSchemas.RequireStorageAsync(database, ct); RequireStore();
        var started = Stopwatch.GetTimestamp();
        var claim = await unit.ExecuteAsync((tx, token) => AcquireRefundPayoutReadAsync(tx, actor, target, expectedRevision, reservationId, reference, metadata, token), ct);
        VerifiedOriginalHandle? handle = null;
        try {
            handle = await OpenRefundPayoutOriginalAsync(claim, ct);
            return await command(claim, handle, started, ct);
        } finally {
            try { if (handle is not null) await handle.DisposeAsync(); }
            finally {
                // Neither close nor cleanup failure can infer whether the financial owner's
                // command committed. Its original operation key remains the sole recovery path.
                try { await ReleaseRefundPayoutReadAsync(claim); } catch { }
            }
        }
    }
    private async Task<VerifiedOriginalHandle> OpenRefundPayoutOriginalAsync(EvidenceReadClaim claim, CancellationToken ct) {
        var opened = await registration.Store!.OpenVerifiedAsync(claim.Original, claim.VerificationBudget, ct);
        var handle = opened is OpenOutcome.Opened success ? success.Handle : throw new PersistenceConflictException("evidence_read_integrity_unavailable", "The exact original cannot be safely read");
        try {
            if (RefundPayoutReadObserver is { } observer) await observer.Verified();
            return handle;
        } catch { await handle.DisposeAsync(); throw; }
    }
    private async Task ReleaseRefundPayoutReadAsync(EvidenceReadClaim claim) {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await unit.ExecuteAsync(async (tx, token) => { await ReleaseReadAsync(tx, claim, token); return true; }, cleanup.Token);
    }
}
