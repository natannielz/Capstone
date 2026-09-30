using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Storage;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

internal sealed record ValidationOriginal(ExpectedPublication Publication, StorageIoBudget Budget, BsonDocument Source);

internal sealed partial class EvidenceService
{
    private async Task<(string Scan, string Decoder, string? Failure)> ValidationStatusAsync(IClientSessionHandle tx, Bundle bundle, CancellationToken ct) {
        if (bundle.Reservation["state"] != "quarantined") return ("unavailable", "unavailable", bundle.Attempt["lastSafeFailure"].IsBsonNull ? null : bundle.Attempt["lastSafeFailure"].AsString);
        var job = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", SourceKey(bundle))).SingleAsync(ct);
        var row = await EvidenceValidationRecords.ReadAsync(database, tx, job, Publication(bundle), Payload(bundle), ct);
        if (row is null) return ("unavailable", "unavailable", job.TryGetValue("lastFailureCode", out var failure) && failure.IsString ? failure.AsString : null);
        var policy = await Rows("evidenceValidationPolicies").Find(tx, new BsonDocument("$and", new BsonArray {
            EvidenceValidationSchemas.Validator("evidenceValidationPolicies"), new BsonDocument {
                { "_id", row["policyId"] }, { "revision", row["policyRevision"] }, { "consumerId", row["consumerId"] }, { "pipelineId", row["pipelineId"] },
                { "storeId", row["storeId"] }, { "active", true }, { "approvedAt", new BsonDocument("$lte", row["checkedAt"]) }, { "expiresAt", new BsonDocument("$gt", Now) } } })).SingleOrDefaultAsync(ct);
        if (policy is null || row["checkedAt"].CompareTo(Now) > 0) return ("unavailable", "unavailable", "processing_approval_unavailable");
        return (row["scanStatus"].AsString, row["decoderStatus"].AsString, row["safeFailure"].IsBsonNull ? null : row["safeFailure"].AsString);
    }
    internal Task<ValidationOriginal> ValidationOriginalAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct) => GuardAsync<ValidationOriginal>(tx, async () => {
        if (job.GetValue("kind", "") != "evidence.validation.requested" || !job.TryGetValue("payload", out var payload) || !payload.IsBsonDocument ||
            !payload.AsBsonDocument.TryGetValue("reservationId", out var id) || !id.IsInt64 || id.AsInt64 <= 0) throw Invalid();
        var bundle = await LoadAsync(tx, id.AsInt64, ct); await CompletionAsync(tx, bundle, ct);
        if (job["sourceKey"] != SourceKey(bundle) || !job["payload"].Equals(Payload(bundle))) throw Invalid();
        var policy = await TouchPolicyAsync(tx, bundle, ct);
        var next = Clone(bundle.Version); next["fence"] = NextFence(next["fence"]); next["revision"] = Next(next["revision"]); next["updatedAt"] = Now;
        await ReplaceAsync(tx, "documentVersions", bundle.Version, next, ct);
        return new(Publication(bundle), new(policy["maximumContentBytes"].AsInt64, TimeSpan.FromMilliseconds(policy["ioDurationMs"].AsInt64), (int)policy["bufferBytes"].AsInt64), Payload(bundle));
    });
}
