using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

internal sealed record StageClaim(StorageAttempt Attempt, long ReservationRevision, long AttemptRevision,
    long Epoch, string Token, string RequestId, DateTimeOffset Until);
internal sealed record PublicationClaim(ExpectedPublication Expected, long ReservationRevision, long AttemptRevision,
    long Epoch, string Token, string RequestId, DateTimeOffset Until);

/// <summary>Internal admission only. Caller owns supplied transactions; no filesystem I/O occurs in them.</summary>
internal sealed partial class EvidenceService
{
    private static readonly string[] LimitFields = ["maximumContentBytes", "reservationDurationMs", "writerDurationMs", "ioDurationMs", "bufferBytes"];
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly EvidenceStoreRegistration registration;
    private readonly CatalogAuthoringService catalog;
    private readonly IPaymentEvidenceUploadTarget? paymentTargets;
    private readonly IRefundPayoutEvidenceUploadTarget? refundPayoutTargets;
    private readonly IProcurementDisbursementEvidenceUploadTarget? procurementDisbursementTargets;
    private readonly IProcurementInspectionEvidenceUploadTarget? procurementInspectionTargets;
    private readonly ISupplierDocumentEvidenceUploadTarget? supplierDocumentTargets;
    internal EvidenceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        EvidenceStoreRegistration? registration = null, IPaymentEvidenceUploadTarget? paymentTargets = null,
        IRefundPayoutEvidenceUploadTarget? refundPayoutTargets = null,
        IProcurementDisbursementEvidenceUploadTarget? procurementDisbursementTargets = null,
        IProcurementInspectionEvidenceUploadTarget? procurementInspectionTargets = null,
        ISupplierDocumentEvidenceUploadTarget? supplierDocumentTargets = null)
    {
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock;
        this.registration = registration ?? EvidenceStoreRegistration.Unavailable();
        if (paymentTargets is not null && (!ReferenceEquals(paymentTargets.Database.Client, database.Client) || !paymentTargets.Database.DatabaseNamespace.Equals(database.DatabaseNamespace)))
            throw new ArgumentException("Evidence targets must join the same owning transaction");
        this.paymentTargets = paymentTargets;
        if (refundPayoutTargets is not null && (!ReferenceEquals(refundPayoutTargets.Database.Client, database.Client) || !refundPayoutTargets.Database.DatabaseNamespace.Equals(database.DatabaseNamespace)))
            throw new ArgumentException("Evidence targets must join the same owning transaction");
        this.refundPayoutTargets = refundPayoutTargets;
        if (procurementDisbursementTargets is not null && (!ReferenceEquals(procurementDisbursementTargets.Database.Client, database.Client) || !procurementDisbursementTargets.Database.DatabaseNamespace.Equals(database.DatabaseNamespace)))
            throw new ArgumentException("Evidence targets must join the same owning transaction");
        this.procurementDisbursementTargets = procurementDisbursementTargets;
        if (procurementInspectionTargets is not null && (!ReferenceEquals(procurementInspectionTargets.Database.Client, database.Client) || !procurementInspectionTargets.Database.DatabaseNamespace.Equals(database.DatabaseNamespace)))
            throw new ArgumentException("Evidence targets must join the same owning transaction");
        this.procurementInspectionTargets = procurementInspectionTargets;
        if (supplierDocumentTargets is not null && (!ReferenceEquals(supplierDocumentTargets.Database.Client, database.Client) || !supplierDocumentTargets.Database.DatabaseNamespace.Equals(database.DatabaseNamespace)))
            throw new ArgumentException("Evidence targets must join the same owning transaction");
        this.supplierDocumentTargets = supplierDocumentTargets;
        catalog = new(database, unit, ids, clock);
    }

    internal Task<ReservationResult> ReserveAsync(IClientSessionHandle tx, ActorContext actor, ReserveCatalogImage request,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) => GuardAsync(tx, () => {
            ArgumentNullException.ThrowIfNull(request);
            return ReserveImageAsync(tx, actor, new ReserveEvidenceImage(request.Target, request.Purpose, request.ExpectedTargetRevision), metadata, ct);
        });

    internal Task<ReservationResult> ReserveImageAsync(IClientSessionHandle tx, ActorContext actor, ReserveEvidenceImage request,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) => GuardAsync(tx, () => {
            ArgumentNullException.ThrowIfNull(request);
            return ReserveMediaAsync(tx, actor, (request.Target, request.Purpose, request.ExpectedTargetRevision), metadata, ct);
        });

    internal Task<ReservationResult> ReserveVideoAsync(IClientSessionHandle tx, ActorContext actor, ReserveEvidenceVideo request,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) => GuardAsync(tx, () => {
            ArgumentNullException.ThrowIfNull(request);
            return ReserveMediaAsync(tx, actor, (request.Target, request.Purpose, request.ExpectedTargetRevision), metadata, ct);
        });

    private async Task<ReservationResult> ReserveMediaAsync(IClientSessionHandle tx, ActorContext actor,
        (EvidenceTargetRef Target, EvidencePurpose Purpose, long? ExpectedTargetRevision) request,
        AuthorizationCommandMetadata metadata, CancellationToken ct)
    {
        ValidateMetadata(metadata);
        var operation = ReserveOperation(request.Purpose);
        var hash = Hash(JsonSerializer.Serialize(new { Format = operation + ":v1",
            Kind = request.Target.Kind.ToString(), request.Target.Id, Purpose = request.Purpose.ToString(),
            request.ExpectedTargetRevision, Reason = metadata.Reason.Trim() }));
        DateTimeOffset? deadline = null;
        var result = await WithTargetAsync(tx, actor, request.Target, null, async (target, token) =>
        {
            var existing = await Rows("idempotencyRecords").Find(tx, ReceiptFilter(actor.UserId, operation, metadata.IdempotencyKey)).FirstOrDefaultAsync(token);
            if (existing is not null)
            {
                if (existing.GetValue("bodyHash", BsonNull.Value) != hash) throw Conflict("idempotency_key_reused");
                var saved = await LoadAsync(tx, existing["evidenceAdmission"]["reservationId"].AsInt64, token);
                RequireAuthority(saved, target, actor, true);
                await RequireReceiptAsync(tx, saved, existing, false, token);
                return Reservation(existing["result"].AsBsonDocument);
            }
            if (request.Purpose != Purpose(target.Target)) throw Invalid();
            if (request.ExpectedTargetRevision is { } expected && expected != target.Revision) throw Conflict("evidence_target_revision_conflict");
            RequireStore();
            var policyCollection = StorePolicyCollection(request.Purpose);
            var policy = await Rows(policyCollection).Find(tx, F.Eq("storeId", registration.StoreId)).SingleOrDefaultAsync(token);
            RequirePolicy(policy);
            var charge = checked(3 * policy!["maximumContentBytes"].AsInt64);
            var bytes = policy["chargedBytes"].AsInt64; var count = policy["chargedReservations"].AsInt64;
            if (count >= policy["maximumHeldReservations"].AsInt64 || charge > policy["maximumHeldBytes"].AsInt64 - bytes)
                throw Conflict("evidence_capacity_exhausted");
            var policyAfter = Clone(policy);
            policyAfter["chargedBytes"] = checked(bytes + charge); policyAfter["chargedReservations"] = checked(count + 1);
            policyAfter["revision"] = Next(policy["revision"]); policyAfter["fence"] = NextFence(policy["fence"]);
            policyAfter["updatedAt"] = Now;
            await ReplaceAsync(tx, policyCollection, policy, policyAfter, token);
            var lineageId = (await ids.NextAsync("evidenceIds", token)).Value;
            var versionId = (await ids.NextAsync("evidenceIds", token)).Value;
            var reservationId = (await ids.NextAsync("evidenceIds", token)).Value;
            var attemptId = (await ids.NextAsync("evidenceIds", token)).Value;
            var auditId = (await ids.NextAsync("auditEvents", token)).Value;
            var now = Now;
            deadline = Date(now).AddMilliseconds(policy["reservationDurationMs"].AsInt64);
            var owner = CatalogRepository.Owner(target.Owner);
            var uploader = Uploader(actor);
            var limits = new BsonDocument(LimitFields.Select(name => new BsonElement(name, policy[name])));
            var stageToken = Token(); var generation = Token();
            var lineage = Base(lineageId, now, false);
            lineage.Add("target", Target(request.Target)).Add("purpose", request.Purpose.ToString()).Add("owner", owner)
                .Add("uploader", uploader).Add("firstVersionRowId", versionId);
            var reservation = Base(reservationId, now);
            reservation.Add("lineageId", lineageId).Add("versionRowId", versionId).Add("contentVersion", 1L).Add("attemptId", attemptId)
                .Add("target", Target(request.Target)).Add("purpose", request.Purpose.ToString()).Add("owner", owner).Add("uploader", uploader)
                .Add("policyId", policy["_id"]).Add("policyVersion", policy["policyVersion"]).Add("admittedTargetRevision", target.Revision)
                .Add("admittedDefinitionId", Number(target.DefinitionId)).Add("admittedDefinitionVersion", Number(target.DefinitionVersion))
                .Add("admittedLimits", limits).Add("operation", operation).Add("key", metadata.IdempotencyKey).Add("bodyHash", hash)
                .Add("expiresAt", deadline.Value.UtcDateTime).Add("chargedBytes", charge).Add("chargedReservations", 1L)
                .Add("state", "reserved").Add("reserveAuditId", auditId).Add("completionAuditId", BsonNull.Value);
            var attempt = Base(attemptId, now);
            attempt.Add("reservationId", reservationId).Add("ticketRevision", 1L).Add("storeId", registration.StoreId)
                .Add("stageToken", stageToken).Add("generation", generation).Add("state", "fresh").Add("claimEpoch", 0L)
                .Add("claimToken", BsonNull.Value).Add("claimKind", BsonNull.Value).Add("claimUntil", BsonNull.Value)
                .Add("claimRequestId", BsonNull.Value).Add("writerUntil", BsonNull.Value).Add("writerRequestId", BsonNull.Value)
                .Add("contentRevision", BsonNull.Value).Add("length", BsonNull.Value).Add("sha256", BsonNull.Value)
                .Add("lastSafeFailure", BsonNull.Value).Add("completionAuditId", BsonNull.Value);
            var version = Base(versionId, now);
            version.Add("lineageId", lineageId).Add("contentVersion", 1L).Add("reservationId", reservationId).Add("attemptId", attemptId)
                .Add("state", "reserved").Add("lifecycleRevision", 1L).Add("fence", 0L).Add("contentRevision", BsonNull.Value)
                .Add("length", BsonNull.Value).Add("sha256", BsonNull.Value).Add("storeId", registration.StoreId).Add("generation", generation)
                .Add("detectedMime", BsonNull.Value).Add("scanStatus", "unavailable").Add("decoderStatus", "unavailable").Add("quarantinedAt", BsonNull.Value);
            var bundle = new Bundle(lineage, reservation, attempt, version);
            await Rows("evidenceLineages").InsertOneAsync(tx, lineage, cancellationToken: token);
            await Rows("documentVersions").InsertOneAsync(tx, version, cancellationToken: token);
            await Rows("evidenceUploadReservations").InsertOneAsync(tx, reservation, cancellationToken: token);
            await Rows("evidenceUploadAttempts").InsertOneAsync(tx, attempt, cancellationToken: token);
            await WriteAuditAsync(tx, actor, bundle, "evidence.reserve", auditId, ZeroRevisions(), metadata, hash, token);
            await WriteReceiptAsync(tx, actor, bundle, false, auditId, metadata.IdempotencyKey, hash, token);
            return Reservation(Result(bundle, false));
        }, ct);
        FinalCheck(deadline, ct);
        return result;
    }
    internal Task<SafeStatus> GetStatusAsync(IClientSessionHandle tx, ActorContext actor, long reservationId,
        AuthorizationReadMetadata readMetadata, CancellationToken ct = default, EvidenceTargetRef? requestedTarget = null) => GuardAsync(tx, async () =>
    {
        if (readMetadata is null || readMetadata.Purpose != "evidence-status" || !Text(readMetadata.CorrelationId, 128)) throw Invalid();
        var bundle = await LoadAsync(tx, reservationId, ct);
        if (requestedTarget is not null && ReadTarget(bundle.Reservation) != requestedTarget) throw Invalid("evidence_actor_or_owner_denied");
        var result = await WithReadTargetAsync(tx, actor, ReadTarget(bundle.Reservation), null, null, async (target, token) =>
        {
            if (requestedTarget is not null) RequireReadAuthority(bundle, target, actor);
            else RequireAuthority(bundle, target, actor, false);
            if (bundle.Reservation["state"] == "quarantined") await CompletionAsync(tx, bundle, token);
            var validation = await ValidationStatusAsync(tx, bundle, token);
            var auditId = (await ids.NextAsync("auditEvents", token)).Value;
            await WriteAuditAsync(tx, actor, bundle, "evidence.status", auditId, Revisions(bundle),
                new("evidence-status", "evidence-status", readMetadata.CorrelationId), null, token);
            return new SafeStatus(reservationId, bundle.Reservation["state"] == "quarantined" ? "quarantined" : bundle.Attempt["state"].AsString,
                bundle.Reservation["revision"].AsInt64, Date(bundle.Reservation["expiresAt"]), Reference(bundle),
                validation.Scan, validation.Decoder, validation.Failure);
        }, ct);
        ct.ThrowIfCancellationRequested(); return result;
    });

    internal Task<ReservationResult?> RecoverReservationAsync(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef requestedTarget,
        string key, CancellationToken ct) => GuardAsync(tx, () => WithReadTargetAsync<ReservationResult?>(tx, actor, requestedTarget, null, null, async (target, token) => {
            ValidateMetadata(new(key, "Recover original upload reservation", "evidence-reservation-recovery"));
            var receipt = await Rows("idempotencyRecords").Find(tx, ReceiptFilter(actor.UserId, ReserveOperation(Purpose(requestedTarget)), key)).SingleOrDefaultAsync(token);
            var reservations = await Rows("evidenceUploadReservations").Find(tx, new MongoDB.Bson.BsonDocument {
                { "uploader.userId", actor.UserId }, { "operation", ReserveOperation(Purpose(requestedTarget)) }, { "key", key } }).Limit(2).ToListAsync(token);
            if (receipt is null && reservations.Count == 0) return null;
            if (receipt is null || reservations.Count != 1) throw Invalid();
            var bundle = await LoadAsync(tx, reservations[0]["_id"].AsInt64, token); RequireAuthority(bundle, target, actor, true);
            await RequireReceiptAsync(tx, bundle, receipt, false, token); return Reservation(receipt["result"].AsBsonDocument);
        }, ct));

    internal async Task RequireBindingAsync(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long reservationId, CancellationToken ct) {
        var row = await Rows("evidenceUploadReservations").Find(tx, F.Eq("_id", reservationId)).SingleOrDefaultAsync(ct);
        if (row is null || !row.GetValue("target", BsonNull.Value).Equals(Target(target)) ||
            row.GetValue("purpose", BsonNull.Value) != Purpose(target).ToString() || !row.GetValue("uploader", BsonNull.Value).Equals(Uploader(actor))) throw Invalid("evidence_actor_or_owner_denied");
    }

    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private BsonDateTime Now => new(clock.UtcNow.UtcDateTime);
    private static BsonDocument Clone(BsonDocument row) => row.DeepClone().AsBsonDocument;
    private static BsonDocument Base(long id, BsonDateTime now, bool mutable = true)
    {
        var result = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "createdAt", now } };
        if (mutable) result.Add("revision", 1L).Add("updatedAt", now);
        return result;
    }
    private static BsonValue Number(long? value) => value is { } number ? new BsonInt64(number) : BsonNull.Value;
    private static BsonDocument Uploader(ActorContext actor) => new() { { "userId", actor.UserId }, { "issuer", actor.ProviderIssuer }, { "subject", actor.ProviderSubject } };
    private static BsonDocument Target(EvidenceTargetRef target) => new() { { "kind", target.Kind.ToString() }, { "id", target.Id } };
    private static EvidenceTargetRef ReadTarget(BsonDocument row) => new(Enum.Parse<EvidenceTargetKind>(row["target"]["kind"].AsString), row["target"]["id"].AsInt64);
    private static DateTimeOffset Date(BsonValue date) => DateTimeOffset.FromUnixTimeMilliseconds(date.AsBsonDateTime.MillisecondsSinceEpoch);
    private static long Next(BsonValue value) => value.IsInt64 && value.AsInt64 is > 0 and < long.MaxValue ? value.AsInt64 + 1 : throw Conflict("evidence_revision_exhausted");
    private static long NextFence(BsonValue value) => value.IsInt64 && value.AsInt64 is >= 0 and < long.MaxValue ? value.AsInt64 + 1 : throw Conflict("evidence_revision_exhausted");
    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool Text(string? value, int maximum) => value is not null && value.Length is > 0 && value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);
    private static DomainValidationException Invalid(string code = "evidence_invalid_admission") => new(code, "Evidence request or durable binding is invalid");
    private static PersistenceConflictException Conflict(string code = "evidence_conflict") => new(code, "Evidence operation cannot proceed in the current state");
    private static void ValidateMetadata(AuthorizationCommandMetadata metadata)
    {
        if (metadata is null || !Text(metadata.IdempotencyKey, 200) || !Text(metadata.CorrelationId, 128) || !Text(metadata.Reason?.Trim(), 1000)) throw Invalid();
    }
    private void FinalCheck(DateTimeOffset? deadline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (deadline is { } until && clock.UtcNow >= until) throw Conflict("evidence_deadline_expired");
    }
    private void RequireStore() { if (registration.Store is null) throw Conflict("evidence_storage_unavailable"); }
    private static void RequirePolicy(BsonDocument? policy)
    {
        if (policy is null || !policy["active"].AsBoolean || policy["policyVersion"].AsInt64 <= 0 ||
            policy["maximumContentBytes"].AsInt64 is <= 0 or > long.MaxValue / 3 ||
            policy["maximumHeldBytes"].AsInt64 <= 0 || policy["maximumHeldReservations"].AsInt64 <= 0 ||
            policy["chargedBytes"].AsInt64 < 0 || policy["chargedReservations"].AsInt64 < 0 ||
            policy["chargedBytes"].AsInt64 > policy["maximumHeldBytes"].AsInt64 ||
            policy["chargedReservations"].AsInt64 > policy["maximumHeldReservations"].AsInt64 ||
            policy["writerDurationMs"].AsInt64 > policy["reservationDurationMs"].AsInt64 ||
            new[] { "reservationDurationMs", "writerDurationMs", "ioDurationMs" }.Any(k => policy[k].AsInt64 is <= 0 or > uint.MaxValue - 1L) ||
            policy["bufferBytes"].AsInt64 is <= 0 or > 1048576) throw Conflict("evidence_policy_unavailable");
    }
    private static void RequireAuthority(Bundle bundle, EvidenceUploadTarget target, ActorContext actor, bool uploader)
    {
        if (ReadTarget(bundle.Reservation) != target.Target || CatalogRepository.Owner(bundle.Reservation["owner"].AsBsonDocument) != target.Owner ||
            uploader && !bundle.Reservation["uploader"].Equals(Uploader(actor))) throw Invalid("evidence_actor_or_owner_denied");
    }
    private sealed record Bundle(BsonDocument Lineage, BsonDocument Reservation, BsonDocument Attempt, BsonDocument Version);
    private async Task<Bundle> LoadAsync(IClientSessionHandle tx, long reservationId, CancellationToken ct)
    {
        if (reservationId <= 0) throw Invalid();
        var reservation = await Rows("evidenceUploadReservations").Find(tx, F.Eq("_id", reservationId)).SingleOrDefaultAsync(ct) ?? throw Invalid();
        var attempt = await Rows("evidenceUploadAttempts").Find(tx, F.Eq("_id", reservation["attemptId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
        var version = await Rows("documentVersions").Find(tx, F.Eq("_id", reservation["versionRowId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
        var lineage = await Rows("evidenceLineages").Find(tx, F.Eq("_id", reservation["lineageId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
        if (new[] { reservation["_id"], attempt["_id"], version["_id"], lineage["_id"] }.Distinct().Count() != 4 ||
            attempt["reservationId"] != reservation["_id"] || version["reservationId"] != reservation["_id"] || version["attemptId"] != attempt["_id"] ||
            version["lineageId"] != lineage["_id"] || lineage["firstVersionRowId"] != version["_id"] ||
            !new[] { "target", "purpose", "owner", "uploader" }.All(k => lineage[k].Equals(reservation[k])) ||
            reservation["purpose"] != Purpose(ReadTarget(reservation)).ToString() || reservation["operation"] != ReserveOperation(reservation) || reservation["contentVersion"] != 1L || version["contentVersion"] != 1L ||
            attempt["ticketRevision"] != 1L || version["state"] != reservation["state"] || attempt["storeId"] != version["storeId"] ||
            attempt["generation"] != version["generation"] || !new[] { "contentRevision", "length", "sha256" }.All(k => attempt[k] == version[k]) ||
            (reservation["state"] == "quarantined") != (attempt["state"] == "quarantined") ||
            reservation["completionAuditId"] != attempt["completionAuditId"] ||
            version["scanStatus"] != "unavailable" || version["decoderStatus"] != "unavailable" || !version["detectedMime"].IsBsonNull)
            throw Invalid();
        var bundle = new Bundle(lineage, reservation, attempt, version);
        var receipt = await Rows("idempotencyRecords").Find(tx, ReceiptFilter(reservation["uploader"]["userId"].AsInt64,
            ReserveOperation(reservation), reservation["key"].AsString)).SingleOrDefaultAsync(ct) ?? throw Invalid();
        await RequireReceiptAsync(tx, bundle, receipt, false, ct);
        return bundle;
    }
    private async Task<BsonDocument> TouchPolicyAsync(IClientSessionHandle tx, Bundle bundle, CancellationToken ct)
    {
        RequireStore();
        var policyCollection = StorePolicyCollection(Purpose(ReadTarget(bundle.Reservation)));
        var row = await Rows(policyCollection).Find(tx, F.Eq("_id", bundle.Reservation["policyId"])).SingleOrDefaultAsync(ct);
        RequirePolicy(row);
        if (row!["storeId"] != registration.StoreId || row["storeId"] != bundle.Attempt["storeId"] || row["policyVersion"] != bundle.Reservation["policyVersion"] ||
            !LimitFields.All(name => row[name] == bundle.Reservation["admittedLimits"][name])) throw Conflict("evidence_policy_changed");
        var after = Clone(row); after["fence"] = NextFence(row["fence"]); after["revision"] = Next(row["revision"]); after["updatedAt"] = Now;
        await ReplaceAsync(tx, policyCollection, row, after, ct);
        FinalCheck(Date(bundle.Reservation["expiresAt"]), ct); return row;
    }
    private async Task ReplaceAsync(IClientSessionHandle tx, string collection, BsonDocument before, BsonDocument after, CancellationToken ct)
    {
        var filter = F.Eq("_id", before["_id"]) & F.Eq("revision", before["revision"]);
        if (before.Contains("fence")) filter &= F.Eq("fence", before["fence"]);
        if ((await Rows(collection).ReplaceOneAsync(tx, filter, after, cancellationToken: ct)).ModifiedCount != 1) throw Conflict();
    }
    private static BsonDocument Revisions(Bundle bundle) => new() {
        { "reservation", bundle.Reservation["revision"] }, { "attempt", bundle.Attempt["revision"] }, { "lifecycle", bundle.Version["lifecycleRevision"] } };
    private static BsonDocument ZeroRevisions() => new() { { "reservation", 0L }, { "attempt", 0L }, { "lifecycle", 0L } };
    private async Task WriteAuditAsync(IClientSessionHandle tx, ActorContext actor, Bundle bundle, string kind, long auditId,
        BsonDocument before, AuthorizationCommandMetadata metadata, string? bodyHash, CancellationToken ct)
    {
        var r = bundle.Reservation;
        await Rows("auditEvents").InsertOneAsync(tx, new BsonDocument {
            { "_id", auditId }, { "schemaVersion", 1 }, { "kind", kind }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
            { "reason", metadata.Reason.Trim() }, { "correlationId", metadata.CorrelationId }, { "bodyHash", bodyHash is null ? BsonNull.Value : new BsonString(bodyHash) },
            { "createdAt", kind == "evidence.quarantine" ? bundle.Version["quarantinedAt"] : Now }, { "evidence", new BsonDocument {
                { "reservationId", r["_id"] }, { "attemptId", r["attemptId"] }, { "versionRowId", r["versionRowId"] }, { "lineageId", r["lineageId"] },
                { "target", r["target"] }, { "purpose", r["purpose"] }, { "owner", r["owner"] }, { "from", before }, { "to", Revisions(bundle) } } }
        }, cancellationToken: ct);
    }
    private static FilterDefinition<BsonDocument> ReceiptFilter(long actor, string operation, string key) =>
        F.Eq("actorId", actor) & F.Eq("operation", operation) & F.Eq("key", key);
    private static string CompletionKey(Bundle bundle) => $"{bundle.Reservation["_id"].AsInt64}:{bundle.Attempt["_id"].AsInt64}:1";
    private static BsonDocument Result(Bundle bundle, bool completed) => new() {
        { "reservationId", bundle.Reservation["_id"] }, { "lineageId", bundle.Reservation["lineageId"] },
        { "versionRowId", bundle.Reservation["versionRowId"] }, { "attemptId", bundle.Reservation["attemptId"] }, { "contentVersion", 1L },
        { "revision", completed ? bundle.Reservation["revision"] : new BsonInt64(1) }, { "expiresAt", bundle.Reservation["expiresAt"] },
        { "sha256", completed ? bundle.Version["sha256"] : BsonNull.Value }, { "lifecycleRevision", completed ? bundle.Version["lifecycleRevision"] : new BsonInt64(1) } };
    private static ReservationResult Reservation(BsonDocument result) => new(result["reservationId"].AsInt64, result["lineageId"].AsInt64,
        result["versionRowId"].AsInt64, result["attemptId"].AsInt64, result["contentVersion"].AsInt64, result["revision"].AsInt64, Date(result["expiresAt"]));
    private static EvidenceRef? Reference(Bundle bundle) => bundle.Reservation["state"] == "quarantined"
        ? new(bundle.Reservation["lineageId"].AsInt64, 1, bundle.Version["sha256"].AsString) : null;
    private async Task WriteReceiptAsync(IClientSessionHandle tx, ActorContext actor, Bundle bundle, bool completed, long auditId, string key, string hash, CancellationToken ct) =>
        await Rows("idempotencyRecords").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
            { "actorId", actor.UserId }, { "operation", completed ? CompleteOperation(bundle) : ReserveOperation(bundle.Reservation) }, { "key", key }, { "bodyHash", hash },
            { "result", Result(bundle, completed) }, { "createdAt", Now }, { "evidenceAdmission", new BsonDocument {
                { "schemaVersion", 1 }, { "reservationId", bundle.Reservation["_id"] }, { "auditId", auditId } } } }, cancellationToken: ct);
    private async Task RequireReceiptAsync(IClientSessionHandle tx, Bundle bundle, BsonDocument receipt, bool completed, CancellationToken ct)
    {
        var r = bundle.Reservation;
        var auditId = completed ? r["completionAuditId"] : r["reserveAuditId"];
        if (receipt["actorId"] != r["uploader"]["userId"] || receipt["operation"] != (completed ? CompleteOperation(bundle) : ReserveOperation(r)) ||
            receipt["key"] != (completed ? CompletionKey(bundle) : r["key"].AsString) ||
            receipt["evidenceAdmission"]["reservationId"] != r["_id"] || receipt["evidenceAdmission"]["auditId"] != auditId ||
            !receipt["result"].Equals(Result(bundle, completed)) || receipt["bodyHash"] != (completed ? Hash(CompletionKey(bundle) + ":" + bundle.Version["sha256"].AsString) : r["bodyHash"].AsString)) throw Invalid();
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", auditId)).SingleOrDefaultAsync(ct) ?? throw Invalid();
        if (audit["kind"] != (completed ? "evidence.quarantine" : "evidence.reserve") || audit["actorId"] != receipt["actorId"] ||
            audit["bodyHash"] != receipt["bodyHash"] || audit["evidence"]["reservationId"] != r["_id"] ||
            audit["evidence"]["attemptId"] != r["attemptId"] || audit["evidence"]["versionRowId"] != r["versionRowId"] ||
            audit["evidence"]["lineageId"] != r["lineageId"] || !audit["evidence"]["target"].Equals(r["target"]) ||
            !audit["evidence"]["owner"].Equals(r["owner"]) || audit["evidence"]["purpose"] != r["purpose"]) throw Invalid();
    }
    private async Task<EvidenceResult> CompletionAsync(IClientSessionHandle tx, Bundle bundle, CancellationToken ct)
    {
        if (bundle.Reservation["state"] != "quarantined") throw Conflict();
        var receipt = await Rows("idempotencyRecords").Find(tx, ReceiptFilter(bundle.Reservation["uploader"]["userId"].AsInt64,
            CompleteOperation(bundle), CompletionKey(bundle))).SingleOrDefaultAsync(ct) ?? throw Invalid();
        await RequireReceiptAsync(tx, bundle, receipt, true, ct);
        var outbox = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", SourceKey(bundle))).SingleOrDefaultAsync(ct);
        if (outbox is null || outbox["kind"] != ValidationKind(bundle) || !outbox["payload"].Equals(Payload(bundle))) throw Invalid();
        return new(bundle.Reservation["_id"].AsInt64, Reference(bundle)!, bundle.Version["lifecycleRevision"].AsInt64);
    }
    private static string SourceKey(Bundle b) => $"evidence.quarantined:{b.Version["_id"].AsInt64}:1";
    private static BsonDocument Payload(Bundle b) => new() {
        { "lineageId", b.Reservation["lineageId"] }, { "versionRowId", b.Version["_id"] }, { "reservationId", b.Reservation["_id"] },
        { "attemptId", b.Attempt["_id"] }, { "contentVersion", 1L }, { "contentRevision", 1L },
        { "sha256", b.Version["sha256"] }, { "lifecycleRevision", b.Version["lifecycleRevision"] } };

    private async Task<T> GuardAsync<T>(IClientSessionHandle tx, Func<Task<T>> action)
    {
        try
        {
            if (tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw Invalid("evidence_transaction_required");
            return await action();
        }
        catch { await AbortAsync(tx); throw; }
    }
    private static async Task AbortAsync(IClientSessionHandle? tx)
    {
        if (tx is null) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (tx.IsInTransaction) await tx.AbortTransactionAsync(cleanup.Token);
            if (tx.IsInTransaction) tx.Dispose();
        }
        catch { try { tx.Dispose(); } catch { /* Preserve the original error and Mongo labels. */ } }
    }
}
