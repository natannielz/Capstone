using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

/// <summary>Authenticated private-image facts, valid only in the supplied active transaction.</summary>
internal sealed record CatalogImageProof(long ReservationId, long VersionRowId, EvidenceRef Reference,
    string ValidationHash, string Mime, string StoreId, string Generation);

internal sealed partial class EvidenceService
{
    // A committed photo outlives its short upload reservation. The caller must have just
    // authenticated the current association and current buyer/staff entitlement in this tx.
    // Current validation, read policy, lifecycle and physical original remain mandatory.
    private async Task<ReadEligibility> DurableCatalogEligibilityAsync(IClientSessionHandle tx,
        CatalogPhotoSelected selected, Bundle bundle, CancellationToken ct)
    {
        if (selected.Proof.ReservationId != bundle.Reservation["_id"].AsInt64 ||
            selected.Proof.VersionRowId != bundle.Version["_id"].AsInt64 ||
            selected.Proof.Reference != Reference(bundle) ||
            bundle.Reservation["purpose"] != EvidencePurpose.CatalogImage.ToString() ||
            ReadTarget(bundle.Reservation) != new EvidenceTargetRef(EvidenceTargetKind.CatalogSku, selected.SkuId) ||
            bundle.Reservation["admittedTargetRevision"] != selected.PhotoTargetRevision ||
            bundle.Reservation["admittedDefinitionId"] != selected.DefinitionId ||
            bundle.Reservation["admittedDefinitionVersion"] != selected.DefinitionVersion ||
            bundle.Version["storeId"] != selected.Proof.StoreId ||
            bundle.Version["storeId"] != registration.StoreId ||
            bundle.Version["generation"] != selected.Proof.Generation)
            throw Conflict("catalog_photo_source_unavailable");
        await CompletionAsync(tx, bundle, ct);
        var job = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", SourceKey(bundle))).SingleOrDefaultAsync(ct)
            ?? throw Conflict("catalog_photo_source_unavailable");
        var publication = Publication(bundle);
        var firstValidation = await EvidenceValidationRecords.ReadAsync(database, tx, job, publication, Payload(bundle), ct);
        if (firstValidation is null || firstValidation["status"] != "passed" ||
            firstValidation["mime"].AsString is not ("image/png" or "image/jpeg") ||
            firstValidation["resultHash"] != selected.Proof.ValidationHash ||
            firstValidation["mime"] != selected.Proof.Mime ||
            publication.Content.Sha256 != selected.Proof.Reference.Sha256)
            throw Conflict("catalog_photo_source_unavailable");
        // M81 is an additive current-proof owner. Its pending/rejected head revokes a
        // previous clean result; the original association/lifecycle retain firstValidation.
        await EvidenceImageRevalidationStorage.RequireAsync(database, ct);
        var (currentHead, currentResult) = await EvidenceImageRevalidationRecords.CurrentAsync(database, tx,
            bundle.Version["_id"].AsInt64, ct);
        var validation = firstValidation;
        var currentPolicyCollection = "evidenceValidationPolicies";
        if (currentHead is not null)
        {
            if (currentHead["status"] != "passed" || currentResult is null) throw Conflict("evidence_read_not_eligible");
            var request = await Rows(EvidenceImageRevalidationSchemas.Requests).Find(tx,
                F.Eq("_id", currentHead["requestId"])).SingleOrDefaultAsync(ct)
                ?? throw EvidenceImageRevalidationRecords.Broken();
            if (request["source"]["versionRowId"] != bundle.Version["_id"] ||
                request["source"]["reservationId"] != selected.Proof.ReservationId ||
                request["source"]["originalValidationHash"] != firstValidation["resultHash"] ||
                request["source"]["storeId"] != bundle.Version["storeId"] ||
                request["source"]["generation"] != bundle.Version["generation"] ||
                request["source"]["reference"]["sha256"] != selected.Proof.Reference.Sha256 ||
                currentResult["mime"] != firstValidation["mime"] ||
                currentResult["source"]["skuId"] != selected.SkuId ||
                currentResult["source"]["definitionId"] != selected.DefinitionId ||
                currentResult["source"]["definitionVersion"] != selected.DefinitionVersion)
                throw EvidenceImageRevalidationRecords.Broken();
            validation = currentResult;
            currentPolicyCollection = EvidenceImageRevalidationSchemas.Policies;
        }
        var policy = await TouchReadPolicyAsync(tx, bundle.Version["storeId"].AsString, false, ct);
        if (bundle.Version["length"].AsInt64 > policy["maximumBytes"].AsInt64 ||
            Date(validation["checkedAt"]) > clock.UtcNow ||
            Date(validation["checkedAt"]).AddMilliseconds(policy["maximumValidationAgeMs"].AsInt64) <= clock.UtcNow ||
            Date(validation["scannerDefinitionsAt"]) > clock.UtcNow ||
            Date(validation["scannerDefinitionsAt"]).AddMilliseconds(policy["maximumSignatureAgeMs"].AsInt64) <= clock.UtcNow)
            throw Conflict("evidence_read_not_eligible");
        var validationPolicy = await Rows(currentPolicyCollection).FindOneAndUpdateAsync(tx,
            new BsonDocument("$and", new BsonArray {
                currentHead is null ? EvidenceValidationSchemas.Validator("evidenceValidationPolicies") :
                    EvidenceImageRevalidationSchemas.Validator(EvidenceImageRevalidationSchemas.Policies), new BsonDocument {
                { "_id", validation["policyId"] }, { "revision", validation["policyRevision"] },
                { "storeId", validation["storeId"] }, { "consumerId", validation["consumerId"] },
                { "pipelineId", validation["pipelineId"] }, { "active", true },
                { "approvedAt", new BsonDocument("$lte", validation["checkedAt"]) },
                { "expiresAt", new BsonDocument("$gt", Now) }, { "fence", new BsonDocument("$lt", long.MaxValue) } } }),
            new BsonDocument("$inc", new BsonDocument("fence", 1L)),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct)
            ?? throw Conflict("evidence_read_not_eligible");
        var lifecycle = await new EvidenceLifecycleStore(database, ids, clock).EnsureAsync(tx, firstValidation, publication, ct);
        if (lifecycle["state"] != "validated") throw Conflict("catalog_photo_source_unavailable");
        var version = Clone(bundle.Version);
        version["fence"] = NextFence(version["fence"]); version["revision"] = Next(version["revision"]); version["updatedAt"] = Now;
        await ReplaceAsync(tx, "documentVersions", bundle.Version, version, ct);
        return new ReadEligibility(bundle, validation, lifecycle, policy, validationPolicy);
    }

    private Task<EvidenceReadClaim> AcquireCatalogAssociationAsync(IClientSessionHandle tx, ActorContext actor,
        EvidenceTargetRef target, long expectedRevision, long reservationId, EvidenceRef reference,
        AuthorizationReadMetadata metadata, CancellationToken ct) => GuardAsync<EvidenceReadClaim>(tx, async () => {
        await CatalogEvidenceReadSchemas.RequireStorageAsync(database, ct);
        if (target.Kind != EvidenceTargetKind.CatalogSku || expectedRevision <= 0 ||
            metadata.Purpose != "catalog-photo-association" || !Text(metadata.CorrelationId, 128)) throw Invalid();
        DateTimeOffset? until = null;
        var claim = await WithReadTargetAsync(tx, actor, target, expectedRevision, reference, async (authority, token) => {
            var bundle = await LoadAsync(tx, reservationId, token);
            RequireReadAuthority(bundle, authority, actor);
            if (Reference(bundle) != reference) throw Conflict("evidence_read_reference_changed");
            var eligible = await ReadEligibilityAsync(tx, bundle, token);
            var started = Stopwatch.GetTimestamp();
            var now = LeaseNow();
            var deadline = now.AddMilliseconds(eligible.Policy["requestDurationMs"].AsInt64);
            until = ReadUntil(eligible, deadline);
            var row = new BsonDocument {
                { "_id", (await ids.NextAsync(CatalogEvidenceReadSchemas.Leases, token)).Value }, { "schemaVersion", 1 },
                { "versionRowId", bundle.Version["_id"] }, { "reservationId", reservationId },
                { "reference", ReadReference(reference) }, { "storeId", bundle.Version["storeId"] },
                { "generation", bundle.Version["generation"] }, { "validationHash", eligible.Validation["resultHash"] },
                { "lifecycleRevision", eligible.Lifecycle["revision"] },
                { "target", new BsonDocument { { "kind", "CatalogSku" }, { "id", target.Id }, { "revision", authority.Revision } } },
                { "purpose", "catalog-photo-association" }, { "actor", ReadActor(actor) }, { "token", Token() },
                { "state", "provisional" }, { "revision", 1L }, { "fence", 0L },
                { "lastAuditId", (await ids.NextAsync(CatalogEvidenceReadSchemas.Audits, token)).Value },
                { "readPolicyId", eligible.Policy["_id"] }, { "readPolicyRevision", eligible.Policy["revision"] },
                { "validationPolicyId", eligible.ValidationPolicy["_id"] },
                { "validationPolicyRevision", eligible.ValidationPolicy["revision"] },
                { "createdAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime },
                { "deadline", deadline.UtcDateTime }, { "until", until.Value.UtcDateTime },
                { "correlationId", metadata.CorrelationId } };
            await Rows(CatalogEvidenceReadSchemas.Leases).InsertOneAsync(tx, row, cancellationToken: token);
            await Rows(CatalogEvidenceReadSchemas.Audits).InsertOneAsync(tx, ReadAudit(row, "acquired"), cancellationToken: token);
            return new EvidenceReadClaim(row["_id"].AsInt64, row["token"].AsString, target, authority.Revision,
                reservationId, reference, Publication(bundle),
                new StorageIoBudget(eligible.Policy["maximumBytes"].AsInt64,
                    TimeSpan.FromMilliseconds(eligible.Policy["verificationDurationMs"].AsInt64),
                    (int)bundle.Reservation["admittedLimits"]["bufferBytes"].AsInt64),
                deadline, TimeSpan.FromMilliseconds(eligible.Policy["requestDurationMs"].AsInt64),
                eligible.Validation["mime"].AsString, until.Value - now, started);
        }, ct);
        FinalCheck(until, ct);
        return claim;
    });

    private Task<EvidenceReadWindow> AuthorizeCatalogAssociationAsync(IClientSessionHandle tx, ActorContext actor,
        EvidenceReadClaim claim, VerifiedOriginalHandle handle, bool renew, CancellationToken ct) =>
        GuardAsync<EvidenceReadWindow>(tx, async () => {
            if (claim.Target.Kind != EvidenceTargetKind.CatalogSku) throw Invalid();
            DateTimeOffset? finalUntil = null;
            var result = await WithReadTargetAsync(tx, actor, claim.Target, claim.TargetRevision, claim.Reference,
                async (authority, token) => {
                    var bundle = await LoadAsync(tx, claim.ReservationId, token);
                    RequireReadAuthority(bundle, authority, actor);
                    var row = await LoadCatalogReadLeaseAsync(tx, claim, null, token);
                    if (row["purpose"] != "catalog-photo-association" ||
                        row["state"] != (renew ? "active" : "provisional") || !row["actor"].Equals(ReadActor(actor)) ||
                        Date(row["until"]) <= clock.UtcNow || Date(row["deadline"]) <= clock.UtcNow ||
                        Date(row["deadline"]) != claim.Deadline || !row["reference"].Equals(ReadReference(claim.Reference)) ||
                        Reference(bundle) != claim.Reference || handle is null || !handle.Stream.CanRead ||
                        handle.Attempt != claim.Original.Attempt || handle.Content != claim.Original.Content ||
                        Publication(bundle) != claim.Original) throw Conflict("evidence_read_lease_expired");
                    var eligible = await ReadEligibilityAsync(tx, bundle, token);
                    if (row["readPolicyId"] != eligible.Policy["_id"] || row["readPolicyRevision"] != eligible.Policy["revision"] ||
                        row["validationPolicyId"] != eligible.ValidationPolicy["_id"] ||
                        row["validationPolicyRevision"] != eligible.ValidationPolicy["revision"] ||
                        row["validationHash"] != eligible.Validation["resultHash"] ||
                        row["lifecycleRevision"] != eligible.Lifecycle["revision"] ||
                        row["versionRowId"] != bundle.Version["_id"] || row["storeId"] != bundle.Version["storeId"] ||
                        row["generation"] != bundle.Version["generation"]) throw Conflict("evidence_read_reference_changed");
                    var next = Clone(row); next["state"] = "active";
                    next["until"] = ReadUntil(eligible, Date(row["deadline"])).UtcDateTime;
                    await SaveReadLeaseAsync(tx, row, next, renew ? "renewed" : "activated", token);
                    finalUntil = Date(next["until"]);
                    return new EvidenceReadWindow(finalUntil.Value, Date(next["deadline"]),
                        TimeSpan.FromMilliseconds(eligible.Policy["leaseDurationMs"].AsInt64));
                }, ct);
            FinalCheck(finalUntil, ct);
            return result;
        });

    internal Task ReleaseCatalogAssociationAsync(IClientSessionHandle tx, EvidenceReadClaim claim, CancellationToken ct) =>
        GuardAsync(tx, async () => {
            var row = await LoadCatalogReadLeaseAsync(tx, claim, null, ct);
            if (row["purpose"] != "catalog-photo-association") throw Conflict("evidence_read_reference_changed");
            if (row["state"] == "released") return true;
            var next = Clone(row); next["state"] = "released";
            await SaveReadLeaseAsync(tx, row, next, "released", ct);
            return true;
        });

    internal Task<EvidenceReadClaim> AcquireCurrentCatalogPhotoAsync(IClientSessionHandle tx, ActorContext actor,
        CatalogPhotoSelected selected, AuthorizationReadMetadata metadata, CancellationToken ct) =>
        GuardAsync<EvidenceReadClaim>(tx, async () => {
            await CatalogEvidenceReadSchemas.RequireStorageAsync(database, ct);
            if (metadata is null || metadata.Purpose != "catalog-photo-current" || !Text(metadata.CorrelationId, 128)) throw Invalid();
            var bundle = await LoadAsync(tx, selected.Proof.ReservationId, ct);
            var eligible = await DurableCatalogEligibilityAsync(tx, selected, bundle, ct);
            var now = LeaseNow();
            var leaseStarted = Stopwatch.GetTimestamp();
            var deadline = now.AddMilliseconds(eligible.Policy["requestDurationMs"].AsInt64);
            var until = ReadUntil(eligible, deadline);
            var target = new EvidenceTargetRef(EvidenceTargetKind.CatalogSku, selected.SkuId);
            var row = new BsonDocument {
                { "_id", (await ids.NextAsync("evidenceReadLeases", ct)).Value }, { "schemaVersion", 1 },
                { "versionRowId", bundle.Version["_id"] }, { "reservationId", selected.Proof.ReservationId },
                { "reference", ReadReference(selected.Proof.Reference) }, { "storeId", bundle.Version["storeId"] },
                { "generation", bundle.Version["generation"] }, { "validationHash", eligible.Validation["resultHash"] },
                { "lifecycleRevision", eligible.Lifecycle["revision"] },
                { "target", new BsonDocument { { "kind", target.Kind.ToString() }, { "id", target.Id },
                    { "revision", selected.PhotoTargetRevision } } },
                { "associationId", selected.AssociationId }, { "associationHash", selected.AssociationHash },
                { "purpose", "catalog-photo-current" },
                { "actor", ReadActor(actor) }, { "token", Token() }, { "state", "provisional" },
                { "revision", 1L }, { "fence", 0L }, { "lastAuditId", (await ids.NextAsync("evidenceReadAudits", ct)).Value },
                { "readPolicyId", eligible.Policy["_id"] }, { "readPolicyRevision", eligible.Policy["revision"] },
                { "validationPolicyId", eligible.ValidationPolicy["_id"] },
                { "validationPolicyRevision", eligible.ValidationPolicy["revision"] },
                { "createdAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime },
                { "deadline", deadline.UtcDateTime }, { "until", until.UtcDateTime },
                { "correlationId", metadata.CorrelationId } };
            await Rows("evidenceReadLeases").InsertOneAsync(tx, row, cancellationToken: ct);
            await Rows("evidenceReadAudits").InsertOneAsync(tx, ReadAudit(row, "acquired"), cancellationToken: ct);
            FinalCheck(until, ct);
            return new EvidenceReadClaim(row["_id"].AsInt64, row["token"].AsString, target,
                selected.PhotoTargetRevision, selected.Proof.ReservationId, selected.Proof.Reference, Publication(bundle),
                new StorageIoBudget(eligible.Policy["maximumBytes"].AsInt64,
                    TimeSpan.FromMilliseconds(eligible.Policy["verificationDurationMs"].AsInt64),
                    (int)bundle.Reservation["admittedLimits"]["bufferBytes"].AsInt64),
                deadline, TimeSpan.FromMilliseconds(eligible.Policy["requestDurationMs"].AsInt64),
                eligible.Validation["mime"].AsString, until - now, leaseStarted);
        });

    internal Task<EvidenceReadWindow> AuthorizeCurrentCatalogPhotoAsync(IClientSessionHandle tx, ActorContext actor,
        CatalogPhotoSelected selected, EvidenceReadClaim claim, VerifiedOriginalHandle handle, bool renew, CancellationToken ct) =>
        GuardAsync<EvidenceReadWindow>(tx, async () => {
            if (claim.Target.Kind != EvidenceTargetKind.CatalogSku || claim.Target.Id != selected.SkuId ||
                claim.TargetRevision != selected.PhotoTargetRevision || claim.ReservationId != selected.Proof.ReservationId ||
                claim.Reference != selected.Proof.Reference) throw Conflict("evidence_read_reference_changed");
            var bundle = await LoadAsync(tx, claim.ReservationId, ct);
            var row = await LoadCatalogReadLeaseAsync(tx, claim, selected, ct);
            if (row["state"] != (renew ? "active" : "provisional") || !row["actor"].Equals(ReadActor(actor)) ||
                Date(row["until"]) <= clock.UtcNow || Date(row["deadline"]) <= clock.UtcNow ||
                row["target"]["kind"] != claim.Target.Kind.ToString() || row["target"]["id"] != claim.Target.Id ||
                row["target"]["revision"] != claim.TargetRevision || Date(row["deadline"]) != claim.Deadline ||
                !row["reference"].Equals(ReadReference(claim.Reference)) || Reference(bundle) != claim.Reference ||
                handle is null || !handle.Stream.CanRead || handle.Attempt != claim.Original.Attempt ||
                handle.Content != claim.Original.Content || Publication(bundle) != claim.Original)
                throw Conflict("evidence_read_lease_expired");
            var eligible = await DurableCatalogEligibilityAsync(tx, selected, bundle, ct);
            if (row["readPolicyId"] != eligible.Policy["_id"] ||
                row["readPolicyRevision"] != eligible.Policy["revision"] ||
                row["validationPolicyId"] != eligible.ValidationPolicy["_id"] ||
                row["validationPolicyRevision"] != eligible.ValidationPolicy["revision"] ||
                row["validationHash"] != eligible.Validation["resultHash"] ||
                row["lifecycleRevision"] != eligible.Lifecycle["revision"] ||
                row["versionRowId"] != bundle.Version["_id"] || row["storeId"] != bundle.Version["storeId"] ||
                row["generation"] != bundle.Version["generation"])
                throw Conflict("evidence_read_reference_changed");
            var next = Clone(row); next["state"] = "active"; next["until"] = ReadUntil(eligible, Date(row["deadline"])).UtcDateTime;
            await SaveReadLeaseAsync(tx, row, next, renew ? "renewed" : "activated", ct);
            var window = new EvidenceReadWindow(Date(next["until"]), Date(next["deadline"]),
                TimeSpan.FromMilliseconds(eligible.Policy["leaseDurationMs"].AsInt64));
            FinalCheck(window.Until, ct);
            return window;
        });

    internal async Task<VerifiedOriginalHandle> OpenCurrentCatalogOriginalAsync(EvidenceReadClaim claim, CancellationToken ct)
    {
        RequireStore();
        var opened = await registration.Store!.OpenVerifiedAsync(claim.Original, claim.VerificationBudget, ct);
        return opened is OpenOutcome.Opened success ? success.Handle :
            throw new PersistenceConflictException("catalog_photo_source_unavailable", "The exact private image cannot be verified");
    }

    internal async Task ReleaseCurrentCatalogReadAsync(EvidenceReadClaim claim)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await unit.ExecuteAsync(async (tx, token) => {
            await GuardAsync(tx, async () => {
                var row = await LoadCatalogReadLeaseAsync(tx, claim, null, token);
                if (row["state"] == "released") return true;
                var next = Clone(row); next["state"] = "released";
                await SaveReadLeaseAsync(tx, row, next, "released", token);
                return true;
            });
            return true;
        }, cleanup.Token);
    }

    // Do not widen the financial LoadReadLeaseAsync reader to CatalogSku.
    // The association ID/hash are retained in the lease, while every active
    // content window still reselects the current head and owner independently.
    private async Task<BsonDocument> LoadCatalogReadLeaseAsync(IClientSessionHandle tx, EvidenceReadClaim claim,
        CatalogPhotoSelected? selected, CancellationToken ct)
    {
        var row = await Rows(CatalogEvidenceReadSchemas.Leases).Find(tx, new BsonDocument("$and", new BsonArray {
            new BsonDocument { { "_id", claim.Id }, { "token", claim.Token }, { "reservationId", claim.ReservationId } },
            CatalogEvidenceReadSchemas.CatalogLeaseValidator() })).SingleOrDefaultAsync(ct)
            ?? throw Conflict("evidence_read_lease_expired");
        if (row["target"]["kind"] != "CatalogSku" ||
            row["target"]["id"] != claim.Target.Id || row["target"]["revision"] != claim.TargetRevision ||
            selected is not null && (row["purpose"] != "catalog-photo-current" ||
                row["associationId"] != selected.AssociationId ||
                row["associationHash"] != selected.AssociationHash)) throw Conflict("evidence_read_reference_changed");
        var audit = await Rows(CatalogEvidenceReadSchemas.Audits).Find(tx, F.Eq("_id", row["lastAuditId"]))
            .SingleOrDefaultAsync(ct);
        var kind = row["state"] == "provisional" ? "acquired" : row["state"] == "released" ? "released" :
            row["revision"] == 2L ? "activated" : "renewed";
        if (audit is null || !audit.Equals(ReadAudit(row, kind))) throw EvidenceValidationRecords.Broken();
        var history = await Rows(CatalogEvidenceReadSchemas.Audits).Find(tx, F.Eq("leaseId", row["_id"]))
            .Sort(new BsonDocument("revision", 1)).Limit(257).ToListAsync(ct);
        if (history.Count > 256 || history.Count != row["revision"].AsInt64) throw EvidenceValidationRecords.Broken();
        for (var index = 0; index < history.Count; index++) {
            var item = history[index];
            var expectedKind = index == 0 ? "acquired" : index == history.Count - 1 ? kind : index == 1 ? "activated" : "renewed";
            if (item.GetValue("revision", BsonNull.Value) != new BsonInt64(index + 1L) ||
                item.GetValue("kind", "") != expectedKind || item.GetValue("actorId", BsonNull.Value) != row["actor"]["userId"] ||
                item.GetValue("versionRowId", BsonNull.Value) != row["versionRowId"] ||
                item.GetValue("correlationId", "") != row["correlationId"] ||
                item.GetValue("createdAt", BsonNull.Value).BsonType != BsonType.DateTime ||
                Date(item["createdAt"]) < Date(row["createdAt"]) || Date(item["createdAt"]) > Date(row["updatedAt"]) ||
                index > 0 && Date(item["createdAt"]) < Date(history[index - 1]["createdAt"]))
                throw EvidenceValidationRecords.Broken();
        }
        return row;
    }

    internal async Task<ProtectedEvidenceRead> OpenCatalogStaffOriginalAsync(ActorContext actor, EvidenceTargetRef target,
        long expectedRevision, long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata,
        CancellationToken ct = default)
    {
        RequireStore();
        var started = Stopwatch.GetTimestamp();
        var claim = await unit.ExecuteAsync((tx, token) => AcquireCatalogAssociationAsync(tx, actor, target,
            expectedRevision, reservationId,
            reference, metadata, token), ct);
        VerifiedOriginalHandle? handle = null;
        async Task ReleaseAsync() {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await unit.ExecuteAsync(async (tx, token) => { await ReleaseCatalogAssociationAsync(tx, claim, token); return true; }, cleanup.Token);
        }
        try {
            var opened = await registration.Store!.OpenVerifiedAsync(claim.Original, claim.VerificationBudget, ct);
            handle = opened is OpenOutcome.Opened success ? success.Handle :
                throw new PersistenceConflictException("catalog_photo_source_unavailable", "The exact private image cannot be verified");
            var window = await unit.ExecuteAsync((tx, token) => AuthorizeCatalogAssociationAsync(tx, actor, claim,
                handle, false, token), ct);
            var held = handle;
            var content = new EvidenceReadStream(held, clock, window, claim.RequestDuration - Stopwatch.GetElapsedTime(started),
                token => unit.ExecuteAsync((tx, inner) => AuthorizeCatalogAssociationAsync(tx, actor, claim,
                    held, true, inner), token), ReleaseAsync, ct);
            handle = null;
            return new ProtectedEvidenceRead(content, claim.Mime, claim.Original.Content.Length, claim.Reference);
        }
        catch {
            try { if (handle is not null) await handle.DisposeAsync(); } catch { }
            try { await ReleaseAsync(); } catch { }
            throw;
        }
    }

    internal async Task<T> WithCatalogImageOriginalAsync<T>(ActorContext actor, EvidenceTargetRef target, long expectedRevision,
        long reservationId, EvidenceRef reference, Func<EvidenceReadClaim, VerifiedOriginalHandle, long, CancellationToken, Task<T>> command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireStore();
        var started = Stopwatch.GetTimestamp();
        EvidenceReadClaim claim;
        try {
            claim = await unit.ExecuteAsync((tx, token) => AcquireCatalogAssociationAsync(tx, actor, target,
                expectedRevision, reservationId, reference,
                new AuthorizationReadMetadata("catalog-photo-association", "catalog-photo-association"), token), ct);
        }
        catch (PersistenceConflictException error) when (error.Code is "evidence_policy_changed" or "evidence_read_not_eligible" or "evidence_deadline_expired") {
            throw new PersistenceConflictException("catalog_photo_upload_again", "The completed image is no longer current; upload and scan a new photo");
        }
        VerifiedOriginalHandle? handle = null;
        try {
            var opened = await registration.Store!.OpenVerifiedAsync(claim.Original, claim.VerificationBudget, ct);
            handle = opened is OpenOutcome.Opened success ? success.Handle :
                throw new PersistenceConflictException("catalog_photo_source_unavailable", "The exact private image cannot be verified");
            return await command(claim, handle, started, ct);
        }
        finally {
            try { if (handle is not null) await handle.DisposeAsync(); }
            finally {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await unit.ExecuteAsync(async (tx, token) => { await ReleaseCatalogAssociationAsync(tx, claim, token); return true; }, cleanup.Token); }
                catch { /* The original command key, not lease cleanup, resolves the commit outcome. */ }
            }
        }
    }

    internal async Task<(CatalogImageProof Proof, Action Final)> RequireVerifiedCatalogImageAsync(IClientSessionHandle tx,
        ActorContext actor, EvidenceReadClaim claim, VerifiedOriginalHandle handle,
        long verificationStarted, CancellationToken ct)
    {
        if (claim.Target.Kind != EvidenceTargetKind.CatalogSku || !handle.Stream.CanRead)
            throw Conflict("catalog_photo_source_unavailable");
        var window = await AuthorizeCatalogAssociationAsync(tx, actor, claim, handle, false, ct);
        var bundle = await LoadAsync(tx, claim.ReservationId, ct);
        var source = new CatalogImageUploadTarget(claim.Target,
            CatalogRepository.Owner(bundle.Reservation["owner"].AsBsonDocument), claim.TargetRevision,
            bundle.Reservation["admittedDefinitionId"].AsInt64, bundle.Reservation["admittedDefinitionVersion"].AsInt64);
        var proof = await RequireCleanCatalogImageAsync(tx, source, claim.ReservationId, claim.Reference, ct);
        void Final() {
            FinalCheck(window.Until, ct); FinalCheck(claim.Deadline, ct);
            if (!handle.Stream.CanRead || Stopwatch.GetElapsedTime(claim.LeaseStarted) >= claim.InitialLeaseDuration ||
                Stopwatch.GetElapsedTime(verificationStarted) >= claim.RequestDuration)
                throw Conflict("evidence_read_lease_expired");
        }
        Final(); return (proof, Final);
    }

    internal Task<CatalogImageProof> RequireHistoricalCatalogImageAsync(IClientSessionHandle tx,
        CatalogImageUploadTarget catalogTarget, long reservationId, EvidenceRef reference, CancellationToken ct) =>
        GuardAsync<CatalogImageProof>(tx, async () =>
        {
            var bundle = await CatalogBundleAsync(tx, catalogTarget, reservationId, reference, ct);
            var validation = await HistoricalValidationAsync(tx, bundle, ct);
            if (validation is null || validation["status"] != "passed" ||
                validation["mime"].AsString is not ("image/png" or "image/jpeg"))
                throw Conflict("catalog_photo_source_unavailable");
            var lifecycle = await RequireHistoricalLifecycleAsync(tx, bundle, validation, ct);
            if (lifecycle is null || lifecycle["state"] != "validated") throw Conflict("catalog_photo_source_unavailable");
            return new(reservationId, bundle.Version["_id"].AsInt64, reference,
                validation["resultHash"].AsString, validation["mime"].AsString,
                bundle.Version["storeId"].AsString, bundle.Version["generation"].AsString);
        });

    internal Task<CatalogImageProof> RequireCleanCatalogImageAsync(IClientSessionHandle tx,
        CatalogImageUploadTarget catalogTarget, long reservationId, EvidenceRef reference, CancellationToken ct) =>
        GuardAsync<CatalogImageProof>(tx, async () =>
        {
            var bundle = await CatalogBundleAsync(tx, catalogTarget, reservationId, reference, ct);
            var eligible = await ReadEligibilityAsync(tx, bundle, ct);
            if (eligible.Validation["mime"].AsString is not ("image/png" or "image/jpeg") ||
                eligible.Validation["status"] != "passed" || eligible.Lifecycle["state"] != "validated" ||
                Publication(bundle).Content.Sha256 != reference.Sha256)
                throw Conflict("catalog_photo_source_unavailable");
            return new(reservationId, bundle.Version["_id"].AsInt64, reference,
                eligible.Validation["resultHash"].AsString, eligible.Validation["mime"].AsString,
                bundle.Version["storeId"].AsString, bundle.Version["generation"].AsString);
        });

    private async Task<Bundle> CatalogBundleAsync(IClientSessionHandle tx, CatalogImageUploadTarget catalogTarget,
        long reservationId, EvidenceRef reference, CancellationToken ct)
    {
        if (tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
                catalogTarget.Target.Kind != EvidenceTargetKind.CatalogSku || catalogTarget.DefinitionId is not > 0 ||
                catalogTarget.DefinitionVersion is not > 0 || reservationId <= 0 || reference is null)
            throw Invalid("catalog_photo_source_unavailable");
        var bundle = await LoadAsync(tx, reservationId, ct);
        if (ReadTarget(bundle.Reservation) != catalogTarget.Target ||
                CatalogRepository.Owner(bundle.Reservation["owner"].AsBsonDocument) != catalogTarget.Owner ||
                bundle.Reservation["purpose"] != EvidencePurpose.CatalogImage.ToString() ||
                bundle.Reservation["admittedTargetRevision"] != catalogTarget.Revision ||
                bundle.Reservation["admittedDefinitionId"] != catalogTarget.DefinitionId ||
                bundle.Reservation["admittedDefinitionVersion"] != catalogTarget.DefinitionVersion ||
                Reference(bundle) != reference)
            throw Invalid("catalog_photo_source_unavailable");
        return bundle;
    }
}
