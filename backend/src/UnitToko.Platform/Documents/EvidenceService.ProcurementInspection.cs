using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

internal sealed partial class EvidenceService
{
    internal Task<long> VideoUploadLimitAsync(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long revision, CancellationToken ct) => GuardAsync(tx, () =>
        WithTargetAsync(tx, actor, target, revision, async (authority, token) => {
            if (authority.Target.Kind != EvidenceTargetKind.ProcurementInspection) throw Invalid();
            RequireStore();
            var policy = await Rows("evidenceVideoStorePolicies").Find(tx, new BsonDocument("$and", new BsonArray {
                ProcurementInspectionEvidenceSchemas.Validator("evidenceVideoStorePolicies"), new BsonDocument("storeId", registration.StoreId) })).SingleOrDefaultAsync(token);
            RequirePolicy(policy); return policy!["maximumContentBytes"].AsInt64;
        }, ct));

    internal Task<Action> PinProcurementInspectionAsync(IClientSessionHandle tx, ActorContext actor, long inspectionId, EvidenceReadClaim claim,
        VerifiedOriginalHandle handle, long verificationStarted, CancellationToken ct) => GuardAsync<Action>(tx, async () => {
        if (procurementInspectionTargets is null) throw Invalid();
        var association = await procurementInspectionTargets.RequireInspectionAssociationAsync(tx, actor, inspectionId, claim, ct);
        if (association.Id != inspectionId || claim.Target.Kind != EvidenceTargetKind.ProcurementInspection || claim.Target.Id != association.DraftId ||
            claim.TargetRevision != association.DraftRevision) throw Invalid();
        return await PinVerifiedAsync(tx, actor, claim, handle, verificationStarted, new(association.Id, association.PinId, association.Owner,
            association.DraftRevision, association.Hash, association.Reason, association.CorrelationId, association.RecordedAt,
            new() { { "associationKind", "ProcurementInspection" }, { "procurementInspectionDraftId", association.DraftId }, { "procurementInspectionDraftRevision", association.DraftRevision } }), ct);
    });
    internal EvidenceReadObserver? ProcurementInspectionReadObserver { get; set; }
    internal Action<double, bool>? ProcurementInspectionPinObserver { get; set; }

    internal async Task<ProtectedEvidenceRead> OpenProcurementInspectionReadAsync(ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        await ProcurementInspectionEvidenceSchemas.RequireStorageAsync(database, ct); RequireStore();
        var started = Stopwatch.GetTimestamp();
        var claim = await unit.ExecuteAsync((tx, token) => AcquireProcurementInspectionReadAsync(tx, actor, target, expectedRevision, reservationId, reference, metadata, token), ct);
        VerifiedOriginalHandle? handle = null;
        try {
            handle = await OpenProcurementInspectionOriginalAsync(claim, ct);
            var window = await unit.ExecuteAsync((tx, token) => AuthorizeProcurementInspectionReadAsync(tx, actor, claim, handle, false, token), ct);
            var held = handle;
            var content = new EvidenceReadStream(held, clock, window, claim.RequestDuration - Stopwatch.GetElapsedTime(started),
                token => unit.ExecuteAsync((tx, inner) => AuthorizeProcurementInspectionReadAsync(tx, actor, claim, held, true, inner), token),
                () => ReleaseProcurementInspectionReadAsync(claim), ct);
            handle = null; return new(content, claim.Mime, claim.Original.Content.Length, claim.Reference);
        } catch {
            try { if (handle is not null) await handle.DisposeAsync(); } catch { }
            try { await ReleaseProcurementInspectionReadAsync(claim); } catch { }
            throw;
        }
    }

    // Verify the immutable original outside all retrying transactions; the owner supplies
    // its real pending receipt/association before the atomic pin and final authority checks.
    internal async Task<T> WithProcurementInspectionPinAsync<T>(ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata,
        Func<EvidenceReadClaim, VerifiedOriginalHandle, long, CancellationToken, Task<T>> command, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(command);
        await ProcurementInspectionEvidenceSchemas.RequireStorageAsync(database, ct); RequireStore();
        var started = Stopwatch.GetTimestamp();
        var claim = await unit.ExecuteAsync((tx, token) => AcquireProcurementInspectionReadAsync(tx, actor, target, expectedRevision, reservationId, reference, metadata, token), ct);
        VerifiedOriginalHandle? handle = null;
        try {
            handle = await OpenProcurementInspectionOriginalAsync(claim, ct);
            return await command(claim, handle, started, ct);
        } finally {
            try { if (handle is not null) await handle.DisposeAsync(); }
            finally {
                // Cleanup cannot decide whether the owner's transaction committed.
                // Only the original operation key can recover that outcome.
                try { await ReleaseProcurementInspectionReadAsync(claim); } catch { }
            }
        }
    }
    private async Task<VerifiedOriginalHandle> OpenProcurementInspectionOriginalAsync(EvidenceReadClaim claim, CancellationToken ct) {
        var opened = await registration.Store!.OpenVerifiedAsync(claim.Original, claim.VerificationBudget, ct);
        var handle = opened is OpenOutcome.Opened success ? success.Handle : throw new PersistenceConflictException("evidence_read_integrity_unavailable", "The exact original cannot be safely read");
        try {
            if (ProcurementInspectionReadObserver is { } observer) await observer.Verified();
            return handle;
        } catch { await handle.DisposeAsync(); throw; }
    }
    private async Task ReleaseProcurementInspectionReadAsync(EvidenceReadClaim claim) {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await unit.ExecuteAsync(async (tx, token) => { await ReleaseReadAsync(tx, claim, token); return true; }, cleanup.Token);
    }
}
