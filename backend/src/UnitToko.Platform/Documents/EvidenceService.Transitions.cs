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
    internal Task<StageClaim> ClaimStageAsync(IClientSessionHandle tx, ActorContext actor, long reservationId,
        long expectedReservationRevision, string claimRequestId, DateTimeOffset invocationDeadline,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        PhaseAsync(tx, actor, reservationId, metadata, async (bundle, policy, token) =>
        {
            StorageValidation.Token(claimRequestId);
            var a = bundle.Attempt; var r = bundle.Reservation;
            if (invocationDeadline.Offset != TimeSpan.Zero || invocationDeadline.UtcTicks % TimeSpan.TicksPerMillisecond != 0 ||
                invocationDeadline > Date(r["expiresAt"]) || invocationDeadline > clock.UtcNow.AddMilliseconds(policy["writerDurationMs"].AsInt64))
                throw Invalid("evidence_writer_deadline_invalid");
            FinalCheck(invocationDeadline, token);
            if (a["state"] == "writing" && a["claimRequestId"] == claimRequestId)
            {
                if (a["writerRequestId"] != claimRequestId || a["claimUntil"] != a["writerUntil"] || Date(a["writerUntil"]) != invocationDeadline) throw Invalid();
                var replay = Stage(bundle); FinalCheck(replay.Until, token); return (replay, replay.Until);
            }
            if (a["state"] != "fresh") throw Conflict("evidence_recovery_required");
            Expected(r, expectedReservationRevision);
            var before = Revisions(bundle);
            var until = invocationDeadline;
            a["state"] = "writing"; a["writerUntil"] = new BsonDateTime(until.UtcDateTime); a["writerRequestId"] = claimRequestId;
            Claim(a, "stage", claimRequestId, until);
            await SaveMutationAsync(tx, actor, bundle, before, "evidence.claim", metadata, null, token);
            return (Stage(bundle), until);
        }, ct);

    internal Task<ExpectedPublication> FreezeContentAsync(IClientSessionHandle tx, ActorContext actor, StageClaim claim,
        SealedStage stage, AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        GuardAsync(tx, async () =>
        {
            ArgumentNullException.ThrowIfNull(claim); ArgumentNullException.ThrowIfNull(stage);
            return await PhaseAsync(tx, actor, claim.Attempt.ReservationId, metadata, async (bundle, policy, token) =>
            {
                RequireStage(bundle, claim);
                if (stage.Attempt != Ticket(bundle) || stage.Attempt != claim.Attempt || stage.Content.Length > policy["maximumContentBytes"].AsInt64)
                    throw Invalid("evidence_stage_mismatch");
                var before = Revisions(bundle);
                foreach (var row in new[] { bundle.Attempt, bundle.Version })
                { row["contentRevision"] = 1L; row["length"] = stage.Content.Length; row["sha256"] = stage.Content.Sha256; }
                bundle.Attempt["state"] = "publish_pending"; ClearClaim(bundle.Attempt);
                await SaveMutationAsync(tx, actor, bundle, before, "evidence.freeze", metadata, null, token);
                return (Publication(bundle), claim.Until);
            }, ct);
        });

    internal Task<PublicationClaim> ClaimPublicationAsync(IClientSessionHandle tx, ActorContext actor, long reservationId,
        long expectedReservationRevision, string claimRequestId, DateTimeOffset invocationDeadline,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        PhaseAsync(tx, actor, reservationId, metadata, async (bundle, policy, token) =>
        {
            StorageValidation.Token(claimRequestId);
            var a = bundle.Attempt; var r = bundle.Reservation;
            if (a["state"] != "publish_pending" || a["contentRevision"] != 1L) throw Conflict("evidence_recovery_required");
            var until = invocationDeadline;
            if (invocationDeadline.Offset != TimeSpan.Zero || invocationDeadline.UtcTicks % TimeSpan.TicksPerMillisecond != 0 ||
                until > Date(r["expiresAt"]) || until > clock.UtcNow.AddMilliseconds(policy["writerDurationMs"].AsInt64) ||
                a["writerRequestId"] == claimRequestId && until != Date(a["writerUntil"])) throw Invalid("evidence_writer_deadline_invalid");
            FinalCheck(until, token);
            if (a["claimKind"] == "publication" && a["claimRequestId"] == claimRequestId)
            {
                if (Date(a["claimUntil"]) != until) throw Invalid();
                FinalCheck(until, token); return (PublishedClaim(bundle), until);
            }
            Expected(r, expectedReservationRevision);
            if (!a["claimKind"].IsBsonNull && Date(a["claimUntil"]) > clock.UtcNow) throw Conflict("evidence_publication_busy");
            var before = Revisions(bundle);
            Claim(a, "publication", claimRequestId, until);
            await SaveMutationAsync(tx, actor, bundle, before, "evidence.claim", metadata, null, token);
            return (PublishedClaim(bundle), until);
        }, ct);

    internal Task<EvidenceResult> CompleteUploadAsync(IClientSessionHandle tx, ActorContext actor, PublicationClaim claim,
        VerifiedOriginalHandle handle, AuthorizationCommandMetadata metadata, CancellationToken ct = default) => GuardAsync(tx, async () =>
    {
        ArgumentNullException.ThrowIfNull(claim); ValidateMetadata(metadata);
        var bundle = await LoadAsync(tx, claim.Expected.Attempt.ReservationId, ct);
        DateTimeOffset? deadline = null;
        var result = await WithTargetAsync(tx, actor, ReadTarget(bundle.Reservation), null, async (target, token) =>
        {
            RequireAuthority(bundle, target, actor, true);
            if (claim.Expected != Publication(bundle)) throw Invalid("evidence_publication_mismatch");
            if (bundle.Reservation["state"] == "quarantined") return await CompletionAsync(tx, bundle, token);
            await TouchPolicyAsync(tx, bundle, token);
            RequirePublication(bundle, claim);
            if (handle is null || !handle.Stream.CanRead || handle.Attempt != claim.Expected.Attempt || handle.Content != claim.Expected.Content)
                throw Invalid("evidence_handle_mismatch");
            deadline = claim.Until;
            var before = Revisions(bundle);
            var auditId = (await ids.NextAsync("auditEvents", token)).Value;
            bundle.Reservation["state"] = "quarantined"; bundle.Reservation["completionAuditId"] = auditId;
            bundle.Attempt["state"] = "quarantined"; bundle.Attempt["completionAuditId"] = auditId; ClearClaim(bundle.Attempt);
            bundle.Version["state"] = "quarantined"; bundle.Version["quarantinedAt"] = Now;
            bundle.Version["lifecycleRevision"] = Next(bundle.Version["lifecycleRevision"]);
            var hash = Hash(CompletionKey(bundle) + ":" + bundle.Version["sha256"].AsString);
            await SaveMutationAsync(tx, actor, bundle, before, "evidence.quarantine", metadata, hash, token, auditId);
            await WriteReceiptAsync(tx, actor, bundle, true, auditId, CompletionKey(bundle), hash, token);
            await new OutboxStore(database, clock).EnqueueAsync(tx, SourceKey(bundle), "evidence.validation.requested", Payload(bundle), token);
            return new EvidenceResult(bundle.Reservation["_id"].AsInt64, Reference(bundle)!, bundle.Version["lifecycleRevision"].AsInt64);
        }, ct);
        FinalCheck(deadline, ct); return result;
    });

    internal Task RecordInterruptedAsync(IClientSessionHandle tx, ActorContext actor, StageClaim claim,
        string safeFailureCode, AuthorizationCommandMetadata metadata, CancellationToken ct = default) => GuardAsync(tx, async () =>
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (safeFailureCode is not ("storage_interrupted" or "storage_rejected" or "storage_unavailable" or "storage_existing_attempt")) throw Invalid();
        return await PhaseAsync(tx, actor, claim.Attempt.ReservationId, metadata, async (bundle, _, token) =>
        {
            RequireStage(bundle, claim);
            var before = Revisions(bundle); bundle.Attempt["state"] = "interrupted"; bundle.Attempt["lastSafeFailure"] = safeFailureCode;
            ClearClaim(bundle.Attempt);
            await SaveMutationAsync(tx, actor, bundle, before, "evidence.interrupted", metadata, null, token);
            return (true, claim.Until);
        }, ct);
    });

    private Task<T> PhaseAsync<T>(IClientSessionHandle tx, ActorContext actor, long reservationId,
        AuthorizationCommandMetadata metadata, Func<Bundle, BsonDocument, CancellationToken, Task<(T Result, DateTimeOffset Until)>> mutation,
        CancellationToken ct) => GuardAsync(tx, async () =>
    {
        ValidateMetadata(metadata);
        var bundle = await LoadAsync(tx, reservationId, ct);
        var result = await WithTargetAsync(tx, actor, ReadTarget(bundle.Reservation), null, async (target, token) =>
        {
            RequireAuthority(bundle, target, actor, true);
            if (bundle.Reservation["state"] != "reserved") throw Conflict("evidence_recovery_required");
            var policy = await TouchPolicyAsync(tx, bundle, token);
            return await mutation(bundle, policy, token);
        }, ct);
        FinalCheck(result.Until, ct); FinalCheck(Date(bundle.Reservation["expiresAt"]), ct);
        return result.Result;
    });
    private static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;
    private static void Expected(BsonDocument row, long revision)
    { if (revision <= 0 || row["revision"] != revision) throw Conflict("evidence_revision_conflict"); }
    private static StorageAttempt Ticket(Bundle bundle) => new(bundle.Attempt["storeId"].AsString, bundle.Reservation["_id"].AsInt64,
        bundle.Attempt["_id"].AsInt64, bundle.Attempt["ticketRevision"].AsInt64, bundle.Attempt["stageToken"].AsString, bundle.Attempt["generation"].AsString);
    private static ExpectedPublication Publication(Bundle bundle) => new(Ticket(bundle), bundle.Attempt["contentRevision"].AsInt64,
        new(bundle.Attempt["length"].AsInt64, bundle.Attempt["sha256"].AsString));
    private static StageClaim Stage(Bundle b) => new(Ticket(b), b.Reservation["revision"].AsInt64, b.Attempt["revision"].AsInt64,
        b.Attempt["claimEpoch"].AsInt64, b.Attempt["claimToken"].AsString, b.Attempt["claimRequestId"].AsString, Date(b.Attempt["claimUntil"]));
    private static PublicationClaim PublishedClaim(Bundle b) => new(Publication(b), b.Reservation["revision"].AsInt64, b.Attempt["revision"].AsInt64,
        b.Attempt["claimEpoch"].AsInt64, b.Attempt["claimToken"].AsString, b.Attempt["claimRequestId"].AsString, Date(b.Attempt["claimUntil"]));
    private static void Claim(BsonDocument a, string kind, string request, DateTimeOffset until)
    {
        a["claimEpoch"] = NextFence(a["claimEpoch"]); a["claimToken"] = Token(); a["claimKind"] = kind;
        a["claimRequestId"] = request; a["claimUntil"] = new BsonDateTime(until.UtcDateTime);
    }
    private static void ClearClaim(BsonDocument a) { foreach (var key in new[] { "claimToken", "claimKind", "claimUntil", "claimRequestId" }) a[key] = BsonNull.Value; }
    private void RequireStage(Bundle b, StageClaim claim)
    {
        if (b.Attempt["state"] != "writing" || claim.Attempt != Ticket(b) ||
            b.Attempt["writerRequestId"] != claim.RequestId || b.Attempt["writerUntil"] != new BsonDateTime(claim.Until.UtcDateTime)) throw Invalid("evidence_stage_claim_mismatch");
        RequireClaim(b, claim.ReservationRevision, claim.AttemptRevision, claim.Epoch, claim.Token, claim.RequestId, claim.Until, "stage");
    }
    private void RequirePublication(Bundle b, PublicationClaim claim)
    {
        if (b.Attempt["state"] != "publish_pending" || claim.Expected != Publication(b)) throw Invalid("evidence_publication_claim_mismatch");
        RequireClaim(b, claim.ReservationRevision, claim.AttemptRevision, claim.Epoch, claim.Token, claim.RequestId, claim.Until, "publication");
    }
    private void RequireClaim(Bundle b, long reservationRevision, long attemptRevision, long epoch, string token, string requestId, DateTimeOffset until, string kind)
    {
        if (b.Reservation["revision"] != reservationRevision || b.Attempt["revision"] != attemptRevision || b.Attempt["claimEpoch"] != epoch ||
            b.Attempt["claimToken"] != token || b.Attempt["claimRequestId"] != requestId || b.Attempt["claimKind"] != kind ||
            Date(b.Attempt["claimUntil"]) != until) throw Conflict("evidence_stale_claim");
        FinalCheck(until, CancellationToken.None); FinalCheck(Date(b.Reservation["expiresAt"]), CancellationToken.None);
    }
    private async Task SaveMutationAsync(IClientSessionHandle tx, ActorContext actor, Bundle bundle, BsonDocument before,
        string kind, AuthorizationCommandMetadata metadata, string? hash, CancellationToken ct, long? auditId = null)
    {
        foreach (var (collection, row) in new[] { ("evidenceUploadReservations", bundle.Reservation), ("evidenceUploadAttempts", bundle.Attempt), ("documentVersions", bundle.Version) })
        {
            var old = Clone(row); row["revision"] = Next(row["revision"]); row["updatedAt"] = Now;
            if (row.Contains("fence")) row["fence"] = NextFence(row["fence"]);
            await ReplaceAsync(tx, collection, old, row, ct);
        }
        await WriteAuditAsync(tx, actor, bundle, kind, auditId ?? (await ids.NextAsync("auditEvents", ct)).Value, before, metadata, hash, ct);
    }
}
