using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

internal sealed record EvidenceReadObserver(Func<Task> Verified);
internal sealed record EvidenceReadClaim(long Id, string Token, EvidenceTargetRef Target, long TargetRevision, long ReservationId,
    EvidenceRef Reference, ExpectedPublication Original, StorageIoBudget VerificationBudget, DateTimeOffset Deadline, TimeSpan RequestDuration, string Mime, TimeSpan InitialLeaseDuration, long LeaseStarted);
internal sealed record EvidenceReadWindow(DateTimeOffset Until, DateTimeOffset Deadline, TimeSpan LeaseDuration);

internal sealed partial class EvidenceService
{
    private sealed record ReadEligibility(Bundle Bundle, BsonDocument Validation, BsonDocument Lifecycle, BsonDocument Policy, BsonDocument ValidationPolicy);
    private async Task<ReadEligibility> ReadEligibilityAsync(IClientSessionHandle tx, Bundle bundle, CancellationToken ct) {
        await CompletionAsync(tx, bundle, ct); await TouchPolicyAsync(tx, bundle, ct);
        var job = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", SourceKey(bundle))).SingleAsync(ct);
        var video = IsVideo(bundle);
        var pdf = IsPdf(bundle);
        var validation = pdf ? await PdfEvidenceValidationRecords.ReadAsync(database, tx, job, Publication(bundle), Payload(bundle), ct)
            : video ? await VideoValidationRecords.ReadAsync(database, tx, job, Publication(bundle), Payload(bundle), ct)
            : await EvidenceValidationRecords.ReadAsync(database, tx, job, Publication(bundle), Payload(bundle), ct);
        if (validation is null || validation["status"] != "passed") throw Conflict("evidence_read_not_eligible");
        if (pdf && validation["mime"] != PdfEvidenceValidationContract.Mime) throw Conflict("evidence_read_not_eligible");
        var policy = await TouchReadPolicyAsync(tx, bundle.Version["storeId"].AsString, video, ct);
        if (bundle.Version["length"].AsInt64 > policy["maximumBytes"].AsInt64 ||
            Date(validation["checkedAt"]) > clock.UtcNow || Date(validation["checkedAt"]).AddMilliseconds(policy["maximumValidationAgeMs"].AsInt64) <= clock.UtcNow ||
            Date(validation["scannerDefinitionsAt"]) > clock.UtcNow || Date(validation["scannerDefinitionsAt"]).AddMilliseconds(policy["maximumSignatureAgeMs"].AsInt64) <= clock.UtcNow)
            throw Conflict("evidence_read_not_eligible");
        var validationPolicies = pdf ? PdfEvidenceValidationRecords.Policies : video ? "evidenceVideoValidationPolicies" : "evidenceValidationPolicies";
        var validationPolicy = await Rows(validationPolicies).FindOneAndUpdateAsync(tx,
            new BsonDocument("$and", new BsonArray { pdf ? PdfEvidenceValidationSchemas.Validator(validationPolicies) : video ? VideoValidationSchemas.Validator(validationPolicies) : EvidenceValidationSchemas.Validator(validationPolicies), new BsonDocument {
                { "_id", validation["policyId"] }, { "revision", validation["policyRevision"] }, { "storeId", validation["storeId"] }, { "consumerId", validation["consumerId"] },
                { "pipelineId", validation["pipelineId"] }, { "active", true }, { "approvedAt", new BsonDocument("$lte", validation["checkedAt"]) },
                { "expiresAt", new BsonDocument("$gt", Now) }, { "fence", new BsonDocument("$lt", long.MaxValue) } } }),
            new BsonDocument("$inc", new BsonDocument("fence", 1L)), new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct)
            ?? throw Conflict("evidence_read_not_eligible");
        var lifecycle = await new EvidenceLifecycleStore(database, ids, clock).EnsureAsync(tx, validation, Publication(bundle), ct);
        if (lifecycle["state"] != "validated") throw Conflict("evidence_read_not_eligible");
        var version = Clone(bundle.Version); version["fence"] = NextFence(version["fence"]); version["revision"] = Next(version["revision"]); version["updatedAt"] = Now;
        await ReplaceAsync(tx, "documentVersions", bundle.Version, version, ct);
        return new(bundle, validation, lifecycle, policy, validationPolicy);
    }
    private async Task<BsonDocument> TouchReadPolicyAsync(IClientSessionHandle tx, string storeId, bool video, CancellationToken ct) =>
        await Rows(video ? "evidenceVideoReadPolicies" : "evidenceReadPolicies").FindOneAndUpdateAsync(tx, new BsonDocument("$and", new BsonArray {
            video ? ProcurementInspectionEvidenceSchemas.Validator("evidenceVideoReadPolicies") : EvidenceAccessSchemas.Validator("evidenceReadPolicies"),
            new BsonDocument { { "storeId", storeId }, { "active", true }, { "fence", new BsonDocument("$lt", long.MaxValue) } } }),
            new BsonDocument("$inc", new BsonDocument("fence", 1L)), new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct)
            ?? throw Conflict("evidence_read_policy_unavailable");
    internal Task<EvidenceReadClaim> AcquirePaymentReadAsync(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct) =>
        AcquireBoundReadAsync(tx, actor, target, EvidenceTargetKind.PaymentDeclaration, "payment-evidence-view", expectedRevision, reservationId, reference, metadata, ct);
    internal Task<EvidenceReadClaim> AcquireRefundPayoutReadAsync(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct) =>
        AcquireBoundReadAsync(tx, actor, target, EvidenceTargetKind.RefundPayout, "refund-payout-evidence-view", expectedRevision, reservationId, reference, metadata, ct);
    internal Task<EvidenceReadClaim> AcquireRefundSettlementReadAsync(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct) =>
        AcquireBoundReadAsync(tx, actor, target, EvidenceTargetKind.RefundPayout, "refund-settlement-evidence-view", expectedRevision, reservationId, reference, metadata, ct);
    internal Task<EvidenceReadClaim> AcquireProcurementDisbursementReadAsync(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct) =>
        AcquireBoundReadAsync(tx, actor, target, EvidenceTargetKind.ProcurementDisbursement, "procurement-disbursement-evidence-view", expectedRevision, reservationId, reference, metadata, ct);
    internal Task<EvidenceReadClaim> AcquireProcurementInspectionReadAsync(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct) =>
        AcquireBoundReadAsync(tx, actor, target, EvidenceTargetKind.ProcurementInspection, "procurement-inspection-evidence-view", expectedRevision, reservationId, reference, metadata, ct);
    private Task<EvidenceReadClaim> AcquireBoundReadAsync(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target,
        EvidenceTargetKind requiredKind, string requiredPurpose, long expectedRevision, long reservationId, EvidenceRef reference,
        AuthorizationReadMetadata metadata, CancellationToken ct) => GuardAsync<EvidenceReadClaim>(tx, async () => {
        if (target.Kind != requiredKind || expectedRevision <= 0 || metadata.Purpose != requiredPurpose || !Text(metadata.CorrelationId, 128)) throw Invalid();
        DateTimeOffset? deadline = null;
        var result = await WithReadTargetAsync(tx, actor, target, expectedRevision, reference, async (authority, token) => {
            var bundle = await LoadAsync(tx, reservationId, token);
            RequireReadAuthority(bundle, authority, actor);
            if (Reference(bundle) != reference) throw Conflict("evidence_read_reference_changed");
            var eligible = await ReadEligibilityAsync(tx, bundle, token);
            // Admission precedes the lease. Allocator/insertion waits, final owner fences
            // and commit all consume its lifetime from this point, including with a frozen UTC clock.
            var leaseStarted = Stopwatch.GetTimestamp();
            var now = clock.UtcNow; deadline = now.AddMilliseconds(eligible.Policy["requestDurationMs"].AsInt64);
            var until = ReadUntil(eligible, deadline.Value);
            var row = new BsonDocument {
                { "_id", (await ids.NextAsync("evidenceReadLeases", token)).Value }, { "schemaVersion", 1 }, { "versionRowId", bundle.Version["_id"] },
                { "reservationId", reservationId }, { "reference", ReadReference(reference) }, { "storeId", bundle.Version["storeId"] }, { "generation", bundle.Version["generation"] },
                { "validationHash", eligible.Validation["resultHash"] }, { "lifecycleRevision", eligible.Lifecycle["revision"] },
                { "target", new BsonDocument { { "kind", target.Kind.ToString() }, { "id", target.Id }, { "revision", authority.Revision } } },
                { "actor", ReadActor(actor) }, { "token", Token() }, { "state", "provisional" }, { "revision", 1L }, { "fence", 0L },
                { "lastAuditId", (await ids.NextAsync("evidenceReadAudits", token)).Value }, { "readPolicyId", eligible.Policy["_id"] }, { "readPolicyRevision", eligible.Policy["revision"] },
                { "validationPolicyId", eligible.ValidationPolicy["_id"] }, { "validationPolicyRevision", eligible.ValidationPolicy["revision"] },
                { "createdAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime }, { "deadline", deadline.Value.UtcDateTime }, { "until", until.UtcDateTime }, { "correlationId", metadata.CorrelationId } };
            await Rows("evidenceReadLeases").InsertOneAsync(tx, row, cancellationToken: token);
            await Rows("evidenceReadAudits").InsertOneAsync(tx, ReadAudit(row, "acquired"), cancellationToken: token);
            deadline = until;
            return new EvidenceReadClaim(row["_id"].AsInt64, row["token"].AsString, target, authority.Revision, reservationId, reference, Publication(bundle),
                new(eligible.Policy["maximumBytes"].AsInt64, TimeSpan.FromMilliseconds(eligible.Policy["verificationDurationMs"].AsInt64), (int)bundle.Reservation["admittedLimits"]["bufferBytes"].AsInt64),
                Date(row["deadline"]), TimeSpan.FromMilliseconds(eligible.Policy["requestDurationMs"].AsInt64), eligible.Validation["mime"].AsString, until - now, leaseStarted);
        }, ct);
        FinalCheck(deadline, ct); return result;
    });
    internal Task<EvidenceReadWindow> AuthorizePaymentReadAsync(IClientSessionHandle tx, ActorContext actor, EvidenceReadClaim claim,
        VerifiedOriginalHandle handle, bool renew, CancellationToken ct) =>
        AuthorizeBoundReadAsync(tx, actor, claim, EvidenceTargetKind.PaymentDeclaration, handle, renew, ct);
    internal Task<EvidenceReadWindow> AuthorizeRefundPayoutReadAsync(IClientSessionHandle tx, ActorContext actor, EvidenceReadClaim claim,
        VerifiedOriginalHandle handle, bool renew, CancellationToken ct) =>
        AuthorizeBoundReadAsync(tx, actor, claim, EvidenceTargetKind.RefundPayout, handle, renew, ct);
    internal Task<EvidenceReadWindow> AuthorizeProcurementDisbursementReadAsync(IClientSessionHandle tx, ActorContext actor, EvidenceReadClaim claim,
        VerifiedOriginalHandle handle, bool renew, CancellationToken ct) =>
        AuthorizeBoundReadAsync(tx, actor, claim, EvidenceTargetKind.ProcurementDisbursement, handle, renew, ct);
    internal Task<EvidenceReadWindow> AuthorizeProcurementInspectionReadAsync(IClientSessionHandle tx, ActorContext actor, EvidenceReadClaim claim,
        VerifiedOriginalHandle handle, bool renew, CancellationToken ct) =>
        AuthorizeBoundReadAsync(tx, actor, claim, EvidenceTargetKind.ProcurementInspection, handle, renew, ct);
    private Task<EvidenceReadWindow> AuthorizeBoundReadAsync(IClientSessionHandle tx, ActorContext actor, EvidenceReadClaim claim,
        EvidenceTargetKind requiredKind, VerifiedOriginalHandle handle, bool renew, CancellationToken ct) => GuardAsync<EvidenceReadWindow>(tx, async () => {
        if (claim.Target.Kind != requiredKind) throw Invalid();
        DateTimeOffset? finalDeadline = null;
        var result = await WithReadTargetAsync(tx, actor, claim.Target, claim.TargetRevision, claim.Reference, async (authority, token) => {
            var bundle = await LoadAsync(tx, claim.ReservationId, token);
            RequireReadAuthority(bundle, authority, actor);
            var row = await LoadReadLeaseAsync(tx, claim, token);
            if (row["state"] != (renew ? "active" : "provisional") || !row["actor"].Equals(ReadActor(actor)) || Date(row["until"]) <= clock.UtcNow || Date(row["deadline"]) <= clock.UtcNow ||
                row["target"]["kind"] != claim.Target.Kind.ToString() || row["target"]["id"] != claim.Target.Id || row["target"]["revision"] != claim.TargetRevision || Date(row["deadline"]) != claim.Deadline || !row["reference"].Equals(ReadReference(claim.Reference)) ||
                Reference(bundle) != claim.Reference || handle is null || !handle.Stream.CanRead || handle.Attempt != claim.Original.Attempt || handle.Content != claim.Original.Content ||
                Publication(bundle) != claim.Original) throw Conflict("evidence_read_lease_expired");
            var eligible = await ReadEligibilityAsync(tx, bundle, token);
            if (row["readPolicyId"] != eligible.Policy["_id"] || row["readPolicyRevision"] != eligible.Policy["revision"] ||
                row["validationPolicyId"] != eligible.ValidationPolicy["_id"] || row["validationPolicyRevision"] != eligible.ValidationPolicy["revision"] ||
                row["validationHash"] != eligible.Validation["resultHash"] || row["lifecycleRevision"] != eligible.Lifecycle["revision"] ||
                row["versionRowId"] != bundle.Version["_id"] || row["storeId"] != bundle.Version["storeId"] || row["generation"] != bundle.Version["generation"]) throw Conflict("evidence_read_reference_changed");
            var next = Clone(row); next["state"] = "active"; next["until"] = ReadUntil(eligible, Date(row["deadline"])).UtcDateTime;
            await SaveReadLeaseAsync(tx, row, next, renew ? "renewed" : "activated", token);
            finalDeadline = Date(next["until"]);
            return new EvidenceReadWindow(finalDeadline.Value, Date(next["deadline"]), TimeSpan.FromMilliseconds(eligible.Policy["leaseDurationMs"].AsInt64));
        }, ct);
        FinalCheck(finalDeadline, ct); return result;
    });
    internal Task ReleaseReadAsync(IClientSessionHandle tx, EvidenceReadClaim claim, CancellationToken ct) => GuardAsync(tx, async () => {
        var row = await LoadReadLeaseAsync(tx, claim, ct);
        if (row["state"] == "released") return true;
        var next = Clone(row); next["state"] = "released"; await SaveReadLeaseAsync(tx, row, next, "released", ct); return true;
    });
    // Only the corresponding owner's immutable association may bind a different original uploader.
    // Upload/admission paths continue to require the exact current actor's provider identity.
    private static void RequireReadAuthority(Bundle bundle, EvidenceUploadTarget target, ActorContext actor) {
        var payment = target.ReviewUploaderId is not null || target.SubmittedReservationId is not null || target.SubmittedSubmissionId is not null;
        var payout = target.PayoutUploaderId is not null || target.PayoutReservationId is not null || target.PayoutId is not null;
        var disbursement = target.DisbursementUploaderId is not null || target.DisbursementReservationId is not null || target.DisbursementId is not null;
        var inspection = target.InspectionUploaderId is not null || target.InspectionReservationId is not null || target.InspectionId is not null;
        var supplierDocument = target.SupplierDocumentUploaderId is not null || target.SupplierDocumentReservationId is not null || target.SupplierDocumentId is not null;
        if ((payment ? 1 : 0) + (payout ? 1 : 0) + (disbursement ? 1 : 0) + (inspection ? 1 : 0) + (supplierDocument ? 1 : 0) > 1) throw Invalid("evidence_actor_or_owner_denied");
        // A submitted payment also carries its immutable association for the original
        // uploader. Only a separately authorized reader supplies an uploader override.
        RequireAuthority(bundle, target, actor, target.ReviewUploaderId is null && target.PayoutUploaderId is null && target.DisbursementUploaderId is null && target.InspectionUploaderId is null && target.SupplierDocumentUploaderId is null);
        if (payment && (target.Target.Kind != EvidenceTargetKind.PaymentDeclaration ||
            target.SubmittedSubmissionId is not > 0 || target.SubmittedReservationId != bundle.Reservation["_id"].AsInt64 ||
            target.ReviewUploaderId is { } uploader && (uploader <= 0 || bundle.Reservation["uploader"]["userId"] != uploader)))
            throw Invalid("evidence_actor_or_owner_denied");
        if (payout && (target.PayoutUploaderId is not > 0 || target.Target.Kind != EvidenceTargetKind.RefundPayout ||
            target.PayoutId is not > 0 || target.PayoutReservationId != bundle.Reservation["_id"].AsInt64 || bundle.Reservation["uploader"]["userId"] != target.PayoutUploaderId.Value))
            throw Invalid("evidence_actor_or_owner_denied");
        if (disbursement && (target.DisbursementUploaderId is not > 0 || target.Target.Kind != EvidenceTargetKind.ProcurementDisbursement ||
            target.DisbursementId is not > 0 || target.DisbursementReservationId != bundle.Reservation["_id"].AsInt64 || bundle.Reservation["uploader"]["userId"] != target.DisbursementUploaderId.Value))
            throw Invalid("evidence_actor_or_owner_denied");
        if (inspection && (target.InspectionUploaderId is not > 0 || target.Target.Kind != EvidenceTargetKind.ProcurementInspection ||
            target.InspectionId is not > 0 || target.InspectionReservationId != bundle.Reservation["_id"].AsInt64 || bundle.Reservation["uploader"]["userId"] != target.InspectionUploaderId.Value))
            throw Invalid("evidence_actor_or_owner_denied");
        if (supplierDocument && (target.SupplierDocumentUploaderId is not > 0 || target.Target.Kind != EvidenceTargetKind.SupplierDocument ||
            target.SupplierDocumentId is not > 0 || target.SupplierDocumentReservationId != bundle.Reservation["_id"].AsInt64 || bundle.Reservation["uploader"]["userId"] != target.SupplierDocumentUploaderId.Value))
            throw Invalid("evidence_actor_or_owner_denied");
    }
    private Task<BsonDocument> LoadReadLeaseAsync(IClientSessionHandle tx, EvidenceReadClaim claim, CancellationToken ct) =>
        LoadReadLeaseAsync(tx, claim.Id, claim.Token, claim.ReservationId, ct);
    private async Task<BsonDocument> LoadReadLeaseAsync(IClientSessionHandle tx, long id, string token, long reservationId, CancellationToken ct) {
        var row = await Rows("evidenceReadLeases").Find(tx, new BsonDocument("$and", new BsonArray {
            new BsonDocument { { "_id", id }, { "token", token }, { "reservationId", reservationId } }, SupplierDocumentEvidenceSchemas.Validator("evidenceReadLeases") })).SingleOrDefaultAsync(ct)
            ?? throw Conflict("evidence_read_lease_expired");
        var audit = await Rows("evidenceReadAudits").Find(tx, F.Eq("_id", row["lastAuditId"])).SingleOrDefaultAsync(ct);
        var kind = row["state"] == "provisional" ? "acquired" : row["state"] == "released" ? "released" : row["revision"] == 2L ? "activated" : "renewed";
        if (audit is null || !audit.Equals(ReadAudit(row, kind))) throw EvidenceValidationRecords.Broken();
        // A current snapshot cannot make missing prior required audit events acceptable.
        var history = await Rows("evidenceReadAudits").Find(tx, F.Eq("leaseId", row["_id"])).Sort(new BsonDocument("revision", 1)).Limit(257).ToListAsync(ct);
        if (history.Count > 256 || history.Count != row["revision"].AsInt64) throw EvidenceValidationRecords.Broken();
        for (var index = 0; index < history.Count; index++) {
            var item = history[index]; var expectedKind = index == 0 ? "acquired" : index == history.Count - 1 ? kind : index == 1 ? "activated" : "renewed";
            if (item.GetValue("revision", BsonNull.Value) != new BsonInt64(index + 1L) || item.GetValue("kind", "") != expectedKind ||
                item.GetValue("actorId", BsonNull.Value) != row["actor"]["userId"] || item.GetValue("versionRowId", BsonNull.Value) != row["versionRowId"] ||
                item.GetValue("correlationId", "") != row["correlationId"] || item.GetValue("createdAt", BsonNull.Value).BsonType != BsonType.DateTime ||
                Date(item["createdAt"]) < Date(row["createdAt"]) || Date(item["createdAt"]) > Date(row["updatedAt"]) ||
                index > 0 && Date(item["createdAt"]) < Date(history[index - 1]["createdAt"])) throw EvidenceValidationRecords.Broken();
        }
        return row;
    }
    private async Task SaveReadLeaseAsync(IClientSessionHandle tx, BsonDocument before, BsonDocument next, string kind, CancellationToken ct) {
        next["revision"] = Next(before["revision"]); next["fence"] = NextFence(before["fence"]); next["updatedAt"] = Now;
        next["lastAuditId"] = (await ids.NextAsync("evidenceReadAudits", ct)).Value;
        var updated = await Rows("evidenceReadLeases").ReplaceOneAsync(tx, new BsonDocument { { "_id", before["_id"] }, { "revision", before["revision"] }, { "token", before["token"] }, { "fence", before["fence"] } }, next, cancellationToken: ct);
        if (updated.ModifiedCount != 1) throw Conflict("evidence_read_lease_expired");
        await Rows("evidenceReadAudits").InsertOneAsync(tx, ReadAudit(next, kind), cancellationToken: ct);
    }
    private DateTimeOffset ReadUntil(ReadEligibility eligible, DateTimeOffset deadline) => new[] {
        clock.UtcNow.AddMilliseconds(eligible.Policy["leaseDurationMs"].AsInt64), deadline, Date(eligible.ValidationPolicy["expiresAt"]),
        Date(eligible.Validation["checkedAt"]).AddMilliseconds(eligible.Policy["maximumValidationAgeMs"].AsInt64),
        Date(eligible.Validation["scannerDefinitionsAt"]).AddMilliseconds(eligible.Policy["maximumSignatureAgeMs"].AsInt64) }.Min();
    private static BsonDocument ReadReference(EvidenceRef value) => new() { { "id", value.Id }, { "version", value.Version }, { "sha256", value.Sha256 } };
    private static BsonDocument ReadActor(ActorContext actor) => new() { { "userId", actor.UserId }, { "issuer", actor.ProviderIssuer }, { "subject", actor.ProviderSubject }, { "grantVersion", actor.GrantVersion },
        { "session", actor.Session is { } session ? new BsonDocument { { "id", session.Id }, { "version", session.Version }, { "securityVersion", session.SecurityVersion } } : BsonNull.Value } };
    private static BsonDocument ReadAudit(BsonDocument row, string kind) => new() {
        { "_id", row["lastAuditId"] }, { "schemaVersion", 1 }, { "leaseId", row["_id"] }, { "versionRowId", row["versionRowId"] }, { "revision", row["revision"] },
        { "kind", kind }, { "actorId", row["actor"]["userId"] }, { "recordHash", EvidenceLifecycleStore.Hash(row) }, { "correlationId", row["correlationId"] }, { "createdAt", row["updatedAt"] } };
}
