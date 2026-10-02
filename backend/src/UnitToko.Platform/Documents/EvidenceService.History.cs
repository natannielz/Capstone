using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

internal sealed partial class EvidenceService
{
    // Migration-only immutable reconstruction. No historical actor is promoted to current
    // authority and no current policy/grant/expiry is inferred from a retained snapshot.
    internal Task ValidateHistoricalAdmissionAsync(IClientSessionHandle tx, long reservationId, CancellationToken ct) => GuardAsync(tx, async () => {
        var bundle = await LoadAsync(tx, reservationId, ct);
        if (bundle.Reservation["state"] == "quarantined") _ = await HistoricalValidationAsync(tx, bundle, ct);
        return true;
    });
    internal Task ValidateHistoricalLeaseAsync(IClientSessionHandle tx, BsonDocument lease, CancellationToken ct) => GuardAsync(tx, async () => {
        var bundle = await LoadAsync(tx, lease["reservationId"].AsInt64, ct);
        var reference = Reference(bundle) ?? throw EvidenceValidationRecords.Broken();
        var kind = Enum.Parse<EvidenceTargetKind>(lease["target"]["kind"].AsString);
        var target = new EvidenceTargetRef(kind, lease["target"]["id"].AsInt64);
        if (target != ReadTarget(bundle.Reservation) || !ReadReference(reference).Equals(lease["reference"]) || lease["versionRowId"] != bundle.Version["_id"] ||
            lease["storeId"] != bundle.Version["storeId"] || lease["generation"] != bundle.Version["generation"] ||
            lease["actor"]["userId"] == bundle.Reservation["uploader"]["userId"] &&
            (lease["actor"]["issuer"] != bundle.Reservation["uploader"]["issuer"] || lease["actor"]["subject"] != bundle.Reservation["uploader"]["subject"])) throw EvidenceValidationRecords.Broken();
        var validation = await HistoricalValidationAsync(tx, bundle, ct) ?? throw EvidenceValidationRecords.Broken();
        var lifecycle = await RequireHistoricalLifecycleAsync(tx, bundle, validation, ct) ?? throw EvidenceValidationRecords.Broken();
        if (validation["status"] != "passed" || lease["validationHash"] != validation["resultHash"] || lease["lifecycleRevision"] != lifecycle["revision"]) throw EvidenceValidationRecords.Broken();
        foreach (var (collection, id, revision) in new[] {
            ("evidenceReadPolicies", "readPolicyId", "readPolicyRevision"), ("evidenceValidationPolicies", "validationPolicyId", "validationPolicyRevision") }) {
            var policy = await Rows(collection).Find(tx, F.Eq("_id", lease[id])).SingleOrDefaultAsync(ct) ?? throw EvidenceValidationRecords.Broken();
            if (policy["storeId"] != lease["storeId"] || policy["revision"].AsInt64 < lease[revision].AsInt64 ||
                collection == "evidenceValidationPolicies" && (validation["policyId"] != lease[id] || validation["policyRevision"] != lease[revision])) throw EvidenceValidationRecords.Broken();
        }
        var saved = await LoadReadLeaseAsync(tx, lease["_id"].AsInt64, lease["token"].AsString, lease["reservationId"].AsInt64, ct);
        if (!saved.Equals(lease)) throw EvidenceValidationRecords.Broken();
        return true;
    });
    internal Task ValidateHistoricalLifecycleAsync(IClientSessionHandle tx, BsonDocument lifecycle, CancellationToken ct) => GuardAsync(tx, async () => {
        var bundle = await LoadAsync(tx, lifecycle["source"]["reservationId"].AsInt64, ct);
        var validation = await HistoricalValidationAsync(tx, bundle, ct) ?? throw EvidenceValidationRecords.Broken();
        var saved = await RequireHistoricalLifecycleAsync(tx, bundle, validation, ct);
        if (saved is null || !saved.Equals(lifecycle)) throw EvidenceValidationRecords.Broken();
        return true;
    });
    internal Task ValidateHistoricalPinAsync(IClientSessionHandle tx, BsonDocument pin, CancellationToken ct) => GuardAsync(tx, async () => {
        var bundle = await LoadAsync(tx, pin["reservationId"].AsInt64, ct);
        var reference = Reference(bundle) ?? throw EvidenceValidationRecords.Broken();
        var validation = await HistoricalValidationAsync(tx, bundle, ct) ?? throw EvidenceValidationRecords.Broken();
        var lifecycle = await RequireHistoricalLifecycleAsync(tx, bundle, validation, ct) ?? throw EvidenceValidationRecords.Broken();
        var target = ReadTarget(bundle.Reservation);
        var bound = pin["associationKind"] == "PaymentSubmission" ? target.Kind == EvidenceTargetKind.PaymentDeclaration && target.Id == pin["declarationId"].AsInt64 :
            pin["associationKind"] == "RefundPayout" && target.Kind == EvidenceTargetKind.RefundPayout && target.Id == pin["refundPayoutDraftId"].AsInt64;
        if (!bound || !pin["reference"].Equals(ReadReference(reference)) || pin["actorId"] != bundle.Reservation["uploader"]["userId"] ||
            pin["versionRowId"] != bundle.Version["_id"] || pin["storeId"] != bundle.Version["storeId"] || pin["generation"] != bundle.Version["generation"] ||
            validation["status"] != "passed" || pin["validationHash"] != validation["resultHash"] || pin["lifecycleRevision"] != lifecycle["revision"] ||
            Date(pin["createdAt"]) < Date(validation["checkedAt"])) throw EvidenceValidationRecords.Broken();
        foreach (var (collection, id, revision) in new[] {
            ("evidenceReadPolicies", "readPolicyId", "readPolicyRevision"), ("evidenceValidationPolicies", "validationPolicyId", "validationPolicyRevision") }) {
            var policy = await Rows(collection).Find(tx, F.Eq("_id", pin[id])).SingleOrDefaultAsync(ct) ?? throw EvidenceValidationRecords.Broken();
            if (policy["storeId"] != pin["storeId"] || policy["revision"].AsInt64 < pin[revision].AsInt64 ||
                collection == "evidenceValidationPolicies" && (validation["policyId"] != pin[id] || validation["policyRevision"] != pin[revision])) throw EvidenceValidationRecords.Broken();
        }
        return true;
    });
    private async Task<BsonDocument?> HistoricalValidationAsync(IClientSessionHandle tx, Bundle bundle, CancellationToken ct) {
        await CompletionAsync(tx, bundle, ct);
        var job = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", SourceKey(bundle))).SingleAsync(ct);
        var validation = await EvidenceValidationRecords.ReadAsync(database, tx, job, Publication(bundle), Payload(bundle), ct);
        if (validation is not null) _ = await RequireHistoricalLifecycleAsync(tx, bundle, validation, ct);
        return validation;
    }
    private async Task<BsonDocument?> RequireHistoricalLifecycleAsync(IClientSessionHandle tx, Bundle bundle, BsonDocument validation, CancellationToken ct) {
        var lifecycle = await Rows("evidenceLifecycles").Find(tx, F.Eq("_id", bundle.Version["_id"])).SingleOrDefaultAsync(ct);
        var audits = await Rows("evidenceLifecycleAudits").Find(tx, F.Eq("versionRowId", bundle.Version["_id"])).Limit(2).ToListAsync(ct);
        if (lifecycle is null) { if (audits.Count != 0) throw EvidenceValidationRecords.Broken(); return null; }
        var audit = new BsonDocument { { "_id", lifecycle["lastAuditId"] }, { "schemaVersion", 1 }, { "versionRowId", lifecycle["_id"] },
            { "revision", lifecycle["revision"] }, { "state", lifecycle["state"] }, { "validationHash", lifecycle["validationHash"] },
            { "recordHash", EvidenceLifecycleStore.Hash(lifecycle) }, { "createdAt", lifecycle["updatedAt"] } };
        if (!await Rows("evidenceLifecycles").Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("_id", lifecycle["_id"]), EvidenceAccessSchemas.Validator("evidenceLifecycles") })).AnyAsync(ct) ||
            !lifecycle["source"].Equals(validation["source"]) || lifecycle["validationHash"] != validation["resultHash"] || lifecycle["storeId"] != bundle.Version["storeId"] || lifecycle["generation"] != bundle.Version["generation"] ||
            lifecycle["state"] != (validation["status"] == "passed" ? "validated" : "failed") || audits.Count != 1 || !audits[0].Equals(audit)) throw EvidenceValidationRecords.Broken();
        return lifecycle;
    }
}
