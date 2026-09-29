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
    // This orchestration alone crosses the Mongo/filesystem boundary. No stream is captured by a retry callback.
    internal async Task<UploadOutcome> UploadAsync(ActorContext actor, long reservationId, long expectedReservationRevision,
        Stream source, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source); ValidateMetadata(metadata);
        var began = clock.UtcNow; var started = Stopwatch.GetTimestamp();
        var initial = await ReadOperationAsync(actor, reservationId, ct);
        if (initial.Attempt["state"] != "fresh") return new("recovery_required");
        var limits = initial.Reservation["admittedLimits"].AsBsonDocument;
        var until = Earlier(Date(initial.Reservation["expiresAt"]),
            Date(new MongoDB.Bson.BsonDateTime(began.AddMilliseconds(limits["writerDurationMs"].AsInt64).UtcDateTime)));
        using var window = new WriterWindow(until, began, started, clock, ct);
        window.Check();
        var requestId = Token();
        StageClaim claim;
        try
        {
            claim = await unit.ExecuteAsync((tx, token) => ClaimStageAsync(tx, actor, reservationId,
                expectedReservationRevision, requestId, window.Until, metadata, token), window.Token);
        }
        catch (UncertainCommitException)
        {
            // Missing state is not proof of abort. Never consume a source to resolve uncertainty.
            var committed = await TryReadOperationAsync(actor, reservationId, window.Token);
            if (committed is null || committed.Attempt["state"] != "writing" || committed.Attempt["claimRequestId"] != requestId ||
                Date(committed.Attempt["writerUntil"]) != window.Until || committed.Attempt["writerRequestId"] != requestId)
                return new("unknown");
            claim = Stage(committed);
        }
        window.Check();
        var staged = await registration.Store!.StageAsync(claim.Attempt, source, window.Budget(limits), window.Token);
        if (staged is not StageOutcome.Sealed sealedStage)
        {
            var failure = staged switch {
                StageOutcome.ExistingAttempt => "storage_existing_attempt", StageOutcome.Rejected => "storage_rejected",
                StageOutcome.Unavailable => "storage_unavailable", _ => "storage_interrupted" };
            try
            {
                await unit.ExecuteAsync(async (tx, token) => {
                    await RecordInterruptedAsync(tx, actor, claim, failure, metadata, token); return true;
                }, window.Token);
                return new("interrupted");
            }
            catch (UncertainCommitException) { return new("unknown"); }
            catch (OperationCanceledException) { return new("unresolved"); }
            catch (PersistenceConflictException) { return new("unresolved"); }
        }
        window.Check();
        try
        {
            await unit.ExecuteAsync((tx, token) => FreezeContentAsync(tx, actor, claim, sealedStage.Stage, metadata, token), window.Token);
        }
        catch (UncertainCommitException)
        {
            var committed = await TryReadOperationAsync(actor, reservationId, window.Token);
            if (committed is null || committed.Attempt["state"] != "publish_pending" ||
                Publication(committed).Attempt != claim.Attempt || Publication(committed).Content != sealedStage.Stage.Content)
                return new("unknown");
        }
        // Always rehydrate the exact expected tuple from a positively committed read, even after known freeze success.
        var frozen = await ReadOperationAsync(actor, reservationId, window.Token);
        window.Check();
        if (frozen.Attempt["state"] != "publish_pending" || Publication(frozen).Attempt != claim.Attempt ||
            Publication(frozen).Content != sealedStage.Stage.Content) return new("unknown");
        return await PublishAndCompleteAsync(actor, frozen, requestId, metadata, window);
    }

    internal async Task<UploadOutcome> RecoverAsync(ActorContext actor, long reservationId,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        ValidateMetadata(metadata);
        var began = clock.UtcNow; var started = Stopwatch.GetTimestamp();
        var bundle = await ReadOperationAsync(actor, reservationId, ct);
        if (bundle.Reservation["state"] == "quarantined")
            return new("quarantined", await CompletedOperationAsync(actor, reservationId, ct));
        if (bundle.Attempt["state"] != "publish_pending") return new("unresolved");
        var until = Earlier(Date(bundle.Reservation["expiresAt"]),
            Date(new MongoDB.Bson.BsonDateTime(began.AddMilliseconds(bundle.Reservation["admittedLimits"]["writerDurationMs"].AsInt64).UtcDateTime)));
        using var window = new WriterWindow(until, began, started, clock, ct);
        window.Check();
        return await PublishAndCompleteAsync(actor, bundle, Token(), metadata, window);
    }

    private async Task<UploadOutcome> PublishAndCompleteAsync(ActorContext actor, Bundle frozen, string requestId,
        AuthorizationCommandMetadata metadata, WriterWindow window)
    {
        var reservationId = frozen.Reservation["_id"].AsInt64;
        PublicationClaim claim;
        try
        {
            claim = await unit.ExecuteAsync((tx, token) => ClaimPublicationAsync(tx, actor, reservationId,
                frozen.Reservation["revision"].AsInt64, requestId, window.Until, metadata, token), window.Token);
        }
        catch (UncertainCommitException)
        {
            var committed = await TryReadOperationAsync(actor, reservationId, window.Token);
            if (committed is null || committed.Attempt["state"] != "publish_pending" || committed.Attempt["claimRequestId"] != requestId ||
                committed.Attempt["claimKind"] != "publication" || Date(committed.Attempt["claimUntil"]) != window.Until) return new("unknown");
            claim = PublishedClaim(committed);
        }
        window.Check();
        var limits = frozen.Reservation["admittedLimits"].AsBsonDocument;
        var published = await registration.Store!.PublishAsync(claim.Expected, window.Budget(limits), window.Token);
        if (published.State is not (PublishState.Published or PublishState.ExistingIdentical)) return new("publish_pending");
        window.Check();
        var opened = await registration.Store.OpenVerifiedAsync(claim.Expected, window.Budget(limits), window.Token);
        if (opened is not OpenOutcome.Opened proof) return new("publish_pending");
        await using var handle = proof.Handle;
        window.Check();
        try
        {
            var complete = await unit.ExecuteAsync((tx, token) => CompleteUploadAsync(tx, actor, claim, handle, metadata, token), window.Token);
            return new("quarantined", complete);
        }
        catch (UncertainCommitException)
        {
            var committed = await TryReadOperationAsync(actor, reservationId, window.Token);
            if (committed is null || committed.Reservation["state"] != "quarantined") return new("unknown");
            return new("quarantined", await CompletedOperationAsync(actor, reservationId, window.Token));
        }
    }

    private Task<Bundle> ReadOperationAsync(ActorContext actor, long reservationId, CancellationToken ct) =>
        unit.ExecuteAsync((tx, token) => GuardAsync(tx, async () =>
        {
            var bundle = await LoadAsync(tx, reservationId, token);
            var result = await WithTargetAsync(tx, actor, ReadTarget(bundle.Reservation), null, async (target, inner) =>
            {
                RequireAuthority(bundle, target, actor, true);
                if (bundle.Reservation["state"] == "quarantined") await CompletionAsync(tx, bundle, inner);
                else await TouchPolicyAsync(tx, bundle, inner);
                return bundle;
            }, token);
            FinalCheck(result.Reservation["state"] == "quarantined" ? null : Date(result.Reservation["expiresAt"]), token);
            return result;
        }), ct);
    private async Task<Bundle?> TryReadOperationAsync(ActorContext actor, long reservationId, CancellationToken ct)
    {
        try { return await ReadOperationAsync(actor, reservationId, ct); }
        catch (UncertainCommitException) { return null; }
        catch (OperationCanceledException) { return null; }
    }
    private Task<EvidenceResult> CompletedOperationAsync(ActorContext actor, long reservationId, CancellationToken ct) =>
        unit.ExecuteAsync((tx, token) => GuardAsync(tx, async () =>
        {
            var bundle = await LoadAsync(tx, reservationId, token);
            var result = await WithTargetAsync(tx, actor, ReadTarget(bundle.Reservation), null, async (target, inner) =>
            {
                RequireAuthority(bundle, target, actor, true); return await CompletionAsync(tx, bundle, inner);
            }, token);
            token.ThrowIfCancellationRequested(); return result;
        }), ct);

    private sealed class WriterWindow : IDisposable
    {
        private readonly IClock clock;
        private readonly long started;
        private readonly TimeSpan duration;
        private readonly CancellationTokenSource cancellation;
        internal DateTimeOffset Until { get; }
        internal CancellationToken Token => cancellation.Token;
        internal WriterWindow(DateTimeOffset until, DateTimeOffset began, long started, IClock clock, CancellationToken token)
        {
            this.clock = clock; this.started = started; Until = until; duration = until - began;
            var remaining = duration - Stopwatch.GetElapsedTime(started);
            var clockRemaining = until - clock.UtcNow;
            if (clockRemaining < remaining) remaining = clockRemaining;
            if (remaining <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1L) throw Conflict("evidence_deadline_expired");
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); cancellation.CancelAfter(remaining);
        }
        internal void Check()
        {
            Token.ThrowIfCancellationRequested();
            if (clock.UtcNow >= Until || Stopwatch.GetElapsedTime(started) >= duration) throw Conflict("evidence_deadline_expired");
        }
        internal StorageIoBudget Budget(MongoDB.Bson.BsonDocument limits)
        {
            Check();
            var remaining = duration - Stopwatch.GetElapsedTime(started);
            var clockRemaining = Until - clock.UtcNow;
            if (clockRemaining < remaining) remaining = clockRemaining;
            var configured = TimeSpan.FromMilliseconds(limits["ioDurationMs"].AsInt64);
            return new(limits["maximumContentBytes"].AsInt64, configured < remaining ? configured : remaining, checked((int)limits["bufferBytes"].AsInt64));
        }
        public void Dispose() => cancellation.Dispose();
    }
}
