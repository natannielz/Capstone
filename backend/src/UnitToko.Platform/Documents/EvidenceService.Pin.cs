using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
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
        await RequireBindingAsync(tx, actor, claim.Target, claim.ReservationId, ct);
        var bundle = await LoadAsync(tx, claim.ReservationId, ct);
        RequireAuthority(bundle, new(claim.Target, association.Owner, association.DeclarationRevision, null, null), actor, true);
        var lease = await LoadReadLeaseAsync(tx, claim, ct);
        DateTimeOffset? policyUntil = null;
        void Final() {
            FinalCheck(Date(lease["until"]), ct); FinalCheck(claim.Deadline, ct);
            if (policyUntil is { } until) FinalCheck(until, ct);
            if (!handle.Stream.CanRead || Stopwatch.GetElapsedTime(verificationStarted) >= claim.InitialLeaseDuration) throw Conflict("evidence_read_lease_expired");
        }
        Final();
        if (lease["state"] != "provisional" || !lease["actor"].Equals(ReadActor(actor)) || lease["target"]["id"] != claim.Target.Id ||
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
        var row = new BsonDocument { { "_id", association.PinId }, { "schemaVersion", 1 }, { "associationKind", "PaymentSubmission" }, { "associationId", association.Id },
            { "declarationId", association.DeclarationId }, { "declarationVersionId", association.DeclarationVersionId }, { "declarationRevision", association.DeclarationRevision }, { "reviewRevision", association.ReviewRevision },
            { "reference", ReadReference(claim.Reference) }, { "reservationId", claim.ReservationId }, { "versionRowId", bundle.Version["_id"] }, { "lifecycleRevision", eligible.Lifecycle["revision"] },
            { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "validationPolicyId", eligible.ValidationPolicy["_id"] }, { "validationPolicyRevision", eligible.ValidationPolicy["revision"] },
            { "readPolicyId", eligible.Policy["_id"] }, { "readPolicyRevision", eligible.Policy["revision"] }, { "storeId", bundle.Version["storeId"] }, { "generation", bundle.Version["generation"] },
            { "validationHash", eligible.Validation["resultHash"] }, { "associationHash", association.Hash }, { "createdAt", association.SubmittedAt.UtcDateTime },
            { "reason", association.Reason }, { "correlationId", association.CorrelationId }, { "auditId", (await ids.NextAsync("evidencePinAudits", ct)).Value } };
        row.Add("pinHash", EvidencePinRecords.Hash(row));
        await Rows("evidencePins").InsertOneAsync(tx, row, cancellationToken: ct);
        await Rows("evidencePinAudits").InsertOneAsync(tx, EvidencePinRecords.Audit(row), cancellationToken: ct);
        _ = await EvidencePinRecords.ReadAsync(database, tx, association.PinId, ct); Final(); return Final;
    });
}
