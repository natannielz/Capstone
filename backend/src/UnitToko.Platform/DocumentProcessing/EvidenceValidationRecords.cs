using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

// One immutable result, service audit and queue receipt; none is financial approval.
internal static class EvidenceValidationRecords
{
    internal static string Hash(BsonDocument result) {
        var copy = result.DeepClone().AsBsonDocument; copy.Remove("resultHash");
        return Convert.ToHexString(SHA256.HashData(Canonical(copy).ToBson())).ToLowerInvariant();
    }
    private static BsonDocument Canonical(BsonDocument value) => new(value.Elements.OrderBy(e => e.Name, StringComparer.Ordinal)
        .Select(e => new BsonElement(e.Name, e.Value.IsBsonDocument ? Canonical(e.Value.AsBsonDocument) : e.Value)));
    internal static BsonDocument Receipt(BsonDocument row) => new() {
        { "versionRowId", row["_id"] }, { "resultHash", row["resultHash"] }, { "auditId", row["auditId"] }, { "pipelineId", row["pipelineId"] } };
    internal static BsonDocument Audit(BsonDocument row) {
        var audit = new BsonDocument { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "versionRowId", row["_id"] } };
        foreach (var name in new[] { "resultHash", "outboxId", "pipelineId", "consumerId", "policyId", "policyRevision", "claimToken", "claimAttempt", "checkedAt", "claimUntil" }) audit.Add(name, row[name]);
        return audit;
    }
    internal static async Task<BsonDocument?> ReadAsync(IMongoDatabase db, IClientSessionHandle tx, BsonDocument job,
        ExpectedPublication original, BsonDocument source, CancellationToken ct) {
        var identity = new BsonDocument("_id", source["versionRowId"]);
        var results = db.GetCollection<BsonDocument>("evidenceValidationResults");
        var row = await results.Find(tx, identity).SingleOrDefaultAsync(ct);
        var audits = await db.GetCollection<BsonDocument>("evidenceValidationAudits").Find(tx, new BsonDocument("versionRowId", source["versionRowId"])).Limit(2).ToListAsync(ct);
        if (row is null) {
            if (audits.Count != 0 || job.Contains("processingReceipt")) throw Broken();
            return null;
        }
        if (!await results.Find(tx, new BsonDocument("$and", new BsonArray { identity, EvidenceValidationSchemas.Validator("evidenceValidationResults") })).AnyAsync(ct) ||
            row["outboxId"] != job["_id"] || !row["source"].Equals(source) || row["storeId"] != original.Attempt.StoreId ||
            row["generation"] != original.Attempt.Generation || row["length"] != original.Content.Length || row["resultHash"] != Hash(row) ||
            !job.TryGetValue("processingFence", out var fence) || !fence.IsInt64 || fence.AsInt64 <= 0 ||
            audits.Count != 1 || !audits[0].Equals(Audit(row)) || !job.GetValue("processingReceipt", BsonNull.Value).Equals(Receipt(row))) throw Broken();
        return row;
    }
    internal static PersistenceConflictException Broken() => new("evidence_validation_integrity", "Evidence validation record needs operator review");
}
