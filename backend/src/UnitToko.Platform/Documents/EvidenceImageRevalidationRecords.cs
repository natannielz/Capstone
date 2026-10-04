using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

/// <summary>Strict retained owner of new image scans. Original validation remains independent and immutable.</summary>
internal static class EvidenceImageRevalidationRecords
{
    internal const string Operation = "evidence.image-revalidation.request";
    internal const string Kind = "evidence.image-revalidation.requested";
    internal static string Hash(BsonDocument row, string hashField)
    {
        var copy = row.DeepClone().AsBsonDocument;
        copy.Remove(hashField);
        return Convert.ToHexString(SHA256.HashData(Canonical(copy).ToBson())).ToLowerInvariant();
    }
    private static BsonDocument Canonical(BsonDocument row) => new(row.Elements.OrderBy(e => e.Name, StringComparer.Ordinal)
        .Select(e => new BsonElement(e.Name, e.Value.IsBsonDocument ? Canonical(e.Value.AsBsonDocument) : e.Value)));
    internal static PersistenceConflictException Broken() => new("evidence_image_revalidation_integrity", "Image revalidation history needs operator review");
    private static IMongoCollection<BsonDocument> Rows(IMongoDatabase db, string name) => db.GetCollection<BsonDocument>(name);

    internal static async Task<(BsonDocument? Head, BsonDocument? Current)> CurrentAsync(IMongoDatabase db,
        IClientSessionHandle tx, long versionRowId, CancellationToken ct)
    {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, db.Client) || versionRowId <= 0) throw Broken();
        var head = await Rows(db, EvidenceImageRevalidationSchemas.Heads).Find(tx, new BsonDocument("_id", versionRowId))
            .SingleOrDefaultAsync(ct);
        var requests = await Rows(db, EvidenceImageRevalidationSchemas.Requests).Find(tx,
            new BsonDocument("versionRowId", versionRowId)).Sort(new BsonDocument("headRevision", 1)).Limit(257).ToListAsync(ct);
        var receipts = await Rows(db, EvidenceImageRevalidationSchemas.Receipts).Find(tx,
            new BsonDocument("versionRowId", versionRowId)).Limit(257).ToListAsync(ct);
        var jobs = await Rows(db, EvidenceImageRevalidationSchemas.Jobs).Find(tx,
            new BsonDocument("versionRowId", versionRowId)).Limit(257).ToListAsync(ct);
        var results = await Rows(db, EvidenceImageRevalidationSchemas.Results).Find(tx,
            new BsonDocument("source.versionRowId", versionRowId)).Limit(257).ToListAsync(ct);
        var audits = await Rows(db, EvidenceImageRevalidationSchemas.Audits).Find(tx,
            new BsonDocument("versionRowId", versionRowId)).Limit(257).ToListAsync(ct);
        if (requests.Count > 256 || receipts.Count != requests.Count || head is null && requests.Count != 0 ||
            head is not null && requests.Count == 0 || jobs.Count != requests.Count ||
            results.Count != audits.Count || jobs.Count > 256 || results.Count > 256 || audits.Count > 256 ||
            head is null && (jobs.Count != 0 || results.Count != 0 || audits.Count != 0)) throw Broken();
        if (head is null) return (null, null);
        if (!await Rows(db, EvidenceImageRevalidationSchemas.Heads).Find(tx, new BsonDocument("$and", new BsonArray {
            new BsonDocument("_id", versionRowId), EvidenceImageRevalidationSchemas.Validator(EvidenceImageRevalidationSchemas.Heads) })).AnyAsync(ct) ||
            head["revision"].AsInt64 != requests.Count || head["requestId"] != requests[^1]["_id"] ||
            head["requestHash"] != requests[^1]["requestHash"] || head["fence"].AsInt64 < requests.Count - 1) throw Broken();
        BsonDocument? previous = null;
        foreach (var request in requests)
        {
            if (!await Rows(db, EvidenceImageRevalidationSchemas.Requests).Find(tx, new BsonDocument("$and", new BsonArray {
                new BsonDocument("_id", request["_id"]), EvidenceImageRevalidationSchemas.Validator(EvidenceImageRevalidationSchemas.Requests) })).AnyAsync(ct) ||
                request["requestHash"] != Hash(request, "requestHash") || request["headRevision"].AsInt64 != (previous?["headRevision"].AsInt64 + 1 ?? 0) ||
                request["previousRequestId"] != (previous is null ? BsonNull.Value : previous["_id"]) ||
                request["previousRequestHash"] != (previous is null ? BsonNull.Value : previous["requestHash"])) throw Broken();
            var receipt = receipts.SingleOrDefault(row => row.GetValue("requestId", BsonNull.Value) == request["_id"]);
            if (receipt is null || !await Rows(db, EvidenceImageRevalidationSchemas.Receipts).Find(tx,
                    new BsonDocument("$and", new BsonArray { new BsonDocument("_id", receipt["_id"]),
                        EvidenceImageRevalidationSchemas.Validator(EvidenceImageRevalidationSchemas.Receipts) })).AnyAsync(ct) ||
                receipt["requestHash"] != request["requestHash"] || receipt["bodyHash"] != request["bodyHash"] ||
                receipt["actorId"] != request["actorId"] || receipt["key"] != request["requestKey"] ||
                receipt["recordedAt"] != request["recordedAt"]) throw Broken();
            if (jobs.Count(row => row.GetValue("requestId", BsonNull.Value) == request["_id"]) != 1 ||
                results.Count(row => row.GetValue("requestId", BsonNull.Value) == request["_id"]) > 1 ||
                audits.Count(row => row.GetValue("requestId", BsonNull.Value) == request["_id"]) > 1) throw Broken();
            if (previous is not null && await ResultAsync(db, tx, previous, ct) is null) throw Broken();
            previous = request;
        }
        var current = requests[^1];
        var result = await ResultAsync(db, tx, current, ct);
        if (head["status"] == "pending")
        {
            if (result is not null || !head["resultId"].IsBsonNull || !head["resultHash"].IsBsonNull) throw Broken();
        }
        else if (result is null || head["resultId"] != result["_id"] || head["resultHash"] != result["resultHash"] ||
            head["status"] != result["status"]) throw Broken();
        return (head, result);
    }

    internal static async Task<BsonDocument?> ResultAsync(IMongoDatabase db, IClientSessionHandle tx,
        BsonDocument request, CancellationToken ct)
    {
        var result = await Rows(db, EvidenceImageRevalidationSchemas.Results).Find(tx,
            new BsonDocument("requestId", request["_id"])).SingleOrDefaultAsync(ct);
        var audits = await Rows(db, EvidenceImageRevalidationSchemas.Audits).Find(tx,
            new BsonDocument("requestId", request["_id"])).Limit(2).ToListAsync(ct);
        var job = await Rows(db, EvidenceImageRevalidationSchemas.Jobs).Find(tx,
            new BsonDocument("requestId", request["_id"])).SingleOrDefaultAsync(ct);
        if (job is null || !await Rows(db, EvidenceImageRevalidationSchemas.Jobs).Find(tx,
                new BsonDocument("$and", new BsonArray { new BsonDocument("_id", job["_id"]),
                    EvidenceImageRevalidationSchemas.Validator(EvidenceImageRevalidationSchemas.Jobs) })).AnyAsync(ct) ||
            job["sourceKey"] != $"evidence.image-revalidation:{request["_id"].AsInt64}" || job["kind"] != Kind ||
            job["consumerId"] != request["consumerId"] || job["pipelineId"] != request["pipelineId"] ||
            job["storeId"] != request["source"]["storeId"] || job["policyId"] != request["policyId"] ||
            job["policyRevision"] != request["policyRevision"] ||
            job["createdAt"] != request["recordedAt"] || job["versionRowId"] != request["versionRowId"]) throw Broken();
        if (result is null)
        {
            if (audits.Count != 0 || !job["resultHash"].IsBsonNull || job["status"] == "completed") throw Broken();
            return null;
        }
        if (!await Rows(db, EvidenceImageRevalidationSchemas.Results).Find(tx, new BsonDocument("$and", new BsonArray {
                new BsonDocument("_id", result["_id"]), EvidenceImageRevalidationSchemas.Validator(EvidenceImageRevalidationSchemas.Results) })).AnyAsync(ct) ||
            result["requestHash"] != request["requestHash"] || !result["source"].Equals(request["source"]) ||
            result["resultHash"] != Hash(result, "resultHash") || result["jobId"] != job["_id"] ||
            result["policyId"] != request["policyId"] || result["policyRevision"] != request["policyRevision"] ||
            result["consumerId"] != request["consumerId"] || result["pipelineId"] != request["pipelineId"] ||
            result["claimAttempt"] != job["attempts"] || result["storeId"] != request["source"]["storeId"] ||
            result["generation"] != request["source"]["generation"] ||
            result["length"] != request["source"]["length"] ||
            result["scannerDefinitionsAt"].ToUniversalTime() > result["checkedAt"].ToUniversalTime() ||
            result["checkedAt"].ToUniversalTime() >= result["claimUntil"].ToUniversalTime() ||
            !Semantic(result) || job["resultHash"] != result["resultHash"] || job["status"] != "completed" ||
            audits.Count != 1) throw Broken();
        var audit = new BsonDocument { { "_id", result["auditId"] }, { "schemaVersion", 1 },
            { "requestId", request["_id"] }, { "versionRowId", request["versionRowId"] },
            { "resultId", result["_id"] }, { "resultHash", result["resultHash"] },
            { "jobId", job["_id"] }, { "checkedAt", result["checkedAt"] } };
        if (!audits[0].Equals(audit)) throw Broken();
        return result;
    }

    private static bool Semantic(BsonDocument result)
    {
        if (result["status"] == "passed")
            return result["scanStatus"] == "clean" && result["decoderStatus"] == "decoded" &&
                result["safeFailure"].IsBsonNull && result["mime"].AsString is "image/png" or "image/jpeg" &&
                result["width"].IsInt32 && result["width"].AsInt32 > 0 &&
                result["height"].IsInt32 && result["height"].AsInt32 > 0 &&
                (long)result["width"].AsInt32 * result["height"].AsInt32 <= 25_000_000;
        if (result["status"] != "rejected" || !result["mime"].IsBsonNull ||
            !result["width"].IsBsonNull || !result["height"].IsBsonNull) return false;
        return result["safeFailure"] == "infected_media" && result["scanStatus"] == "infected" &&
                result["decoderStatus"] == "unavailable" ||
            result["safeFailure"] == "invalid_media" && result["scanStatus"] == "clean" &&
                result["decoderStatus"] == "invalid" ||
            result["safeFailure"] == "integrity_mismatch" && result["decoderStatus"] == "unavailable";
    }
}

internal static class EvidenceImageRevalidationStorage
{
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var name in EvidenceImageRevalidationSchemas.Collections)
        {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions {
                Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct)
                ?? throw EvidenceImageRevalidationRecords.Broken();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
                options.GetValue("validationLevel", "") != "strict" || options.GetValue("validationAction", "") != "error" ||
                options.GetValue("validator", BsonNull.Value) != EvidenceImageRevalidationSchemas.Validator(name))
                throw EvidenceImageRevalidationRecords.Broken();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (!indexes.Any(row => row.GetValue("name", "") == "_id_" && row.GetValue("key", BsonNull.Value) == new BsonDocument("_id", 1)))
                throw EvidenceImageRevalidationRecords.Broken();
            foreach (var key in EvidenceImageRevalidationSchemas.Keys(name))
                if (!indexes.Any(row => row.GetValue("name", "") == key.Name && row.GetValue("key", BsonNull.Value) == key.Key &&
                    row.GetValue("unique", false).AsBoolean == key.Unique && !row.Contains("partialFilterExpression") &&
                    !row.Contains("expireAfterSeconds"))) throw EvidenceImageRevalidationRecords.Broken();
            if (indexes.Any(row => row.GetValue("name", "") != "_id_" &&
                !EvidenceImageRevalidationSchemas.Keys(name).Any(key => row.GetValue("name", "") == key.Name &&
                    row.GetValue("key", BsonNull.Value) == key.Key))) throw EvidenceImageRevalidationRecords.Broken();
        }
    }
}
