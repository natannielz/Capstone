using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

// Current lifecycle is separate from the immutable v13 admission snapshot and its original receipts.
internal sealed class EvidenceLifecycleStore(IMongoDatabase database, IdAllocator ids, IClock clock)
{
    internal async Task<BsonDocument> EnsureAsync(IClientSessionHandle tx, BsonDocument validation, ExpectedPublication original, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || validation["storeId"] != original.Attempt.StoreId ||
            validation["generation"] != original.Attempt.Generation || validation["length"] != original.Content.Length || validation["source"]["sha256"] != original.Content.Sha256)
            throw EvidenceValidationRecords.Broken();
        var versionId = validation["_id"]; var row = await Rows("evidenceLifecycles").Find(tx, new BsonDocument("_id", versionId)).SingleOrDefaultAsync(ct);
        if (row is null) {
            if (await Rows("evidenceLifecycleAudits").Find(tx, new BsonDocument("versionRowId", versionId)).AnyAsync(ct)) throw EvidenceValidationRecords.Broken();
            var now = new BsonDateTime(clock.UtcNow.UtcDateTime);
            row = new BsonDocument {
                { "_id", versionId }, { "schemaVersion", 1 }, { "source", validation["source"] }, { "validationHash", validation["resultHash"] },
                { "storeId", validation["storeId"] }, { "generation", validation["generation"] }, { "state", validation["status"] == "passed" ? "validated" : "failed" },
                { "revision", checked(validation["source"]["lifecycleRevision"].AsInt64 + 1) }, { "fence", 0L },
                { "lastAuditId", (await ids.NextAsync("evidenceLifecycleAudits", ct)).Value }, { "createdAt", now }, { "updatedAt", now } };
            await Rows("evidenceLifecycles").InsertOneAsync(tx, row, cancellationToken: ct);
            await Rows("evidenceLifecycleAudits").InsertOneAsync(tx, Audit(row), cancellationToken: ct);
        }
        if (!await Rows("evidenceLifecycles").Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("_id", versionId), EvidenceAccessSchemas.Validator("evidenceLifecycles") })).AnyAsync(ct) ||
            row["validationHash"] != validation["resultHash"] || !row["source"].Equals(validation["source"]) || row["storeId"] != original.Attempt.StoreId || row["generation"] != original.Attempt.Generation ||
            row["state"] != (validation["status"] == "passed" ? "validated" : "failed")) throw EvidenceValidationRecords.Broken();
        var audit = await Rows("evidenceLifecycleAudits").Find(tx, new BsonDocument("_id", row["lastAuditId"])).SingleOrDefaultAsync(ct);
        if (audit is null || !audit.Equals(Audit(row))) throw EvidenceValidationRecords.Broken();
        var updated = await Rows("evidenceLifecycles").FindOneAndUpdateAsync(tx,
            new BsonDocument { { "_id", versionId }, { "revision", row["revision"] }, { "fence", new BsonDocument("$lt", long.MaxValue) }, { "lastAuditId", row["lastAuditId"] } },
            new BsonDocument("$inc", new BsonDocument("fence", 1L)), new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
        return updated ?? throw EvidenceValidationRecords.Broken();
    }
    internal static string Hash(BsonDocument row) { var value = row.DeepClone().AsBsonDocument; value.Remove("fence"); return EvidenceValidationRecords.Hash(value); }
    private static BsonDocument Audit(BsonDocument row) => new() {
        { "_id", row["lastAuditId"] }, { "schemaVersion", 1 }, { "versionRowId", row["_id"] }, { "revision", row["revision"] },
        { "state", row["state"] }, { "validationHash", row["validationHash"] }, { "recordHash", Hash(row) }, { "createdAt", row["updatedAt"] } };
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
}
