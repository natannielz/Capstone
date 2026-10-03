using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

internal static class VideoValidationRecords
{
    internal const string Kind = "evidence.video-validation.requested";
    internal const string Policies = "evidenceVideoValidationPolicies";
    internal const string Results = "evidenceVideoValidationResults";
    internal const string Audits = "evidenceVideoValidationAudits";
    internal static string Hash(BsonDocument result) => EvidenceValidationRecords.Hash(result);
    internal static BsonDocument Receipt(BsonDocument row) {
        var receipt = new BsonDocument("evidenceVideoValidation", true); receipt.AddRange(EvidenceValidationRecords.Receipt(row)); return receipt;
    }
    internal static BsonDocument Audit(BsonDocument row) => EvidenceValidationRecords.Audit(row);
    internal static BsonDocument Original(ExpectedPublication value) => new() {
        { "storeId", value.Attempt.StoreId }, { "reservationId", value.Attempt.ReservationId }, { "attemptId", value.Attempt.AttemptId },
        { "attemptRevision", value.Attempt.Revision }, { "stageToken", value.Attempt.StageToken }, { "generation", value.Attempt.Generation },
        { "contentRevision", value.ContentRevision }, { "length", value.Content.Length }, { "sha256", value.Content.Sha256 } };
    internal static async Task<BsonDocument?> ReadAsync(IMongoDatabase db, IClientSessionHandle tx, BsonDocument job,
        ExpectedPublication original, BsonDocument source, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, db.Client) || job.GetValue("kind", "") != Kind ||
            !job.GetValue("payload", BsonNull.Value).Equals(source)) throw Broken();
        var identity = new BsonDocument("_id", source["versionRowId"]); var results = db.GetCollection<BsonDocument>(Results);
        var row = await results.Find(tx, identity).SingleOrDefaultAsync(ct);
        var audits = await db.GetCollection<BsonDocument>(Audits).Find(tx, new BsonDocument("versionRowId", source["versionRowId"])).Limit(2).ToListAsync(ct);
        if (row is null) {
            if (audits.Count != 0 || job.Contains("processingReceipt")) throw Broken();
            return null;
        }
        if (!await results.Find(tx, new BsonDocument("$and", new BsonArray { identity, VideoValidationSchemas.Validator(Results) })).AnyAsync(ct) ||
            row["outboxId"] != job["_id"] || !row["source"].Equals(source) || !row["original"].Equals(Original(original)) ||
            row["storeId"] != original.Attempt.StoreId || row["generation"] != original.Attempt.Generation || row["length"] != original.Content.Length ||
            row["resultHash"] != Hash(row) || !job.TryGetValue("processingFence", out var fence) || !fence.IsInt64 || fence.AsInt64 <= 0 ||
            audits.Count != 1 || !audits[0].Equals(Audit(row)) || !job.GetValue("processingReceipt", BsonNull.Value).Equals(Receipt(row))) throw Broken();
        ct.ThrowIfCancellationRequested(); return row;
    }
    internal static PersistenceConflictException Broken() => new("evidence_video_validation_integrity", "Private video validation history needs operator review");
}
