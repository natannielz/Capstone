using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed class ExtractionRecords(IMongoDatabase database)
{
    internal static string Hash(BsonDocument row) {
        var value = row.DeepClone().AsBsonDocument; value.Remove("resultHash"); value.Remove("recordHash"); return EvidenceValidationRecords.Hash(value);
    }
    internal static BsonDocument Provenance(ExtractionProvenance value) => new() { { "engineVersion", value.EngineVersion }, { "modelVersion", value.ModelVersion },
        { "modelSha256", value.ModelSha256 }, { "runtimeSha256", value.RuntimeSha256 }, { "preprocessingVersion", value.PreprocessingVersion } };
    internal static ExtractionProvenance Provenance(BsonDocument value) => new(value["engineVersion"].AsString, value["modelVersion"].AsString,
        value["modelSha256"].AsString, value["runtimeSha256"].AsString, value["preprocessingVersion"].AsString);
    internal static BsonDocument Output(ExtractionResult value) => new() {
        { "lines", new BsonArray(value.Lines.Select(line => new BsonDocument { { "text", line.Text }, { "confidence", new Decimal128(line.Confidence) }, { "box", Box(line.Box.Values) } })) },
        { "fields", new BsonArray(value.Fields.Select(field => new BsonDocument { { "field", field.Field }, { "value", field.Value is null ? BsonNull.Value : new BsonString(field.Value) },
            { "confidence", field.Confidence is { } c ? new BsonDecimal128(c) : BsonNull.Value }, { "box", field.BoundingBox is { } box ? Box(box) : BsonNull.Value },
            { "flags", new BsonArray(field.Flags) } })) }, { "qualityFlags", new BsonArray(value.QualityFlags) } };
    private static BsonArray Box(IReadOnlyList<decimal> values) => new(values.Select(v => new BsonDecimal128(v)));
    internal static ExtractionResult Extract(BsonDocument row) {
        var reference = row["binding"]["reference"];
        var lines = row["output"]["lines"].AsBsonArray.Select(value => {
            var box = value["box"].AsBsonArray;
            return new RecognizedLine(value["text"].AsString, value["confidence"].AsDecimal, new(box[0].AsDecimal, box[1].AsDecimal, box[2].AsDecimal, box[3].AsDecimal));
        }).ToArray();
        var output = new ExtractionResult(row["outboxId"].AsObjectId.ToString(), new(reference["id"].AsInt64, reference["version"].AsInt64, reference["sha256"].AsString),
            ExtractionPurpose.Payment, Provenance(row["provenance"].AsBsonDocument), "succeeded", null, lines);
        if (!Output(output).Equals(row["output"])) throw Broken(); return output;
    }
    internal static BsonDocument Receipt(BsonDocument row) => new() { { "resultId", row["_id"] }, { "resultHash", row["resultHash"] }, { "readAuditId", row["readAuditId"] },
        { "auditId", row["auditId"] }, { "bindingHash", Hash(row["binding"].AsBsonDocument) }, { "pipelineId", row["pipelineId"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "resultId", row["_id"] }, { "readAuditId", row["readAuditId"] },
        { "outboxId", row["outboxId"] }, { "resultHash", row["resultHash"] }, { "bindingHash", Hash(row["binding"].AsBsonDocument) },
        { "consumerId", row["consumerId"] }, { "pipelineId", row["pipelineId"] }, { "createdAt", row["createdAt"] } };
    internal async Task<BsonDocument> ReadAuditAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows("documentProcessingReadAudits").Find(tx, Shape("documentProcessingReadAudits", new("_id", id))).SingleOrDefaultAsync(ct) ?? throw Broken();
        if (row["recordHash"] != Hash(row)) throw Broken(); return row;
    }
    internal async Task<BsonDocument?> ResultAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct) {
        var rows = await Rows("documentExtractionResults").Find(tx, new BsonDocument("outboxId", job["_id"])).Limit(2).ToListAsync(ct);
        var audits = await Rows("documentExtractionAudits").Find(tx, new BsonDocument("outboxId", job["_id"])).Limit(2).ToListAsync(ct);
        if (rows.Count == 0 && audits.Count == 0 && !job.Contains("processingReceipt")) return null;
        if (rows.Count != 1 || audits.Count != 1 || !job.Contains("processingReceipt")) throw Broken();
        var row = await Rows("documentExtractionResults").Find(tx, Shape("documentExtractionResults", new("_id", rows[0]["_id"]))).SingleOrDefaultAsync(ct) ?? throw Broken();
        if (row["resultHash"] != Hash(row) || !audits[0].Equals(Audit(row)) || !job["processingReceipt"].Equals(Receipt(row))) throw Broken();
        var read = await ReadAuditAsync(tx, row["readAuditId"].AsInt64, ct);
        foreach (var name in new[] { "outboxId", "consumerId", "pipelineId", "policyId", "policyRevision", "claimToken", "claimAttempt", "binding" })
            if (!read[name].Equals(row[name])) throw Broken();
        if (read["createdAt"].CompareTo(row["createdAt"]) > 0 || read["until"].CompareTo(row["until"]) < 0) throw Broken();
        var binding = row["binding"];
        var payload = new BsonDocument { { "submissionId", binding["submissionId"] }, { "reviewRevision", binding["reviewRevision"] }, { "declarationVersionId", binding["declarationVersionId"] },
            { "evidence", binding["reference"] }, { "pinId", binding["pinId"] }, { "submissionHash", binding["submissionHash"] } };
        if (job["kind"] != "payment.ocr.requested" || !payload.Equals(job["payload"]) || job["createdAt"].CompareTo(read["createdAt"]) > 0) throw Broken();
        _ = Extract(row); return row;
    }
    internal async Task<BsonDocument?> FailureAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct) {
        var filter = new BsonDocument { { "outboxId", job["_id"] }, { "claimToken", job["leaseToken"] } };
        var raw = await Rows("documentProcessingFailures").Find(tx, filter).SingleOrDefaultAsync(ct); if (raw is null) return null;
        var row = await Rows("documentProcessingFailures").Find(tx, Shape("documentProcessingFailures", filter)).SingleOrDefaultAsync(ct) ?? throw Broken();
        var read = await ReadAuditAsync(tx, row["readAuditId"].AsInt64, ct);
        if (row["recordHash"] != Hash(row) || row["bindingHash"] != Hash(read["binding"].AsBsonDocument) || read["createdAt"].CompareTo(row["createdAt"]) > 0) throw Broken();
        foreach (var name in new[] { "outboxId", "consumerId", "pipelineId", "policyId", "policyRevision", "claimToken", "claimAttempt" }) if (row[name] != read[name]) throw Broken();
        return row;
    }
    internal static BsonDocument Shape(string name, BsonDocument identity) => new("$and", new BsonArray { identity, DocumentProcessingSchemas.Validator(name) });
    // A manual-review consumer keeps the actual failed attempt; it never manufactures extraction output.
    internal async Task<(BsonDocument Failure, BsonDocument Read)> HistoricalFailureAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows("documentProcessingFailures").Find(tx, Shape("documentProcessingFailures", new("_id", id))).SingleOrDefaultAsync(ct) ?? throw Broken();
        var read = await ReadAuditAsync(tx, row["readAuditId"].AsInt64, ct);
        if (row["recordHash"] != Hash(row) || row["bindingHash"] != Hash(read["binding"].AsBsonDocument) || read["createdAt"].CompareTo(row["createdAt"]) > 0) throw Broken();
        foreach (var name in new[] { "outboxId", "consumerId", "pipelineId", "policyId", "policyRevision", "claimToken", "claimAttempt" }) if (row[name] != read[name]) throw Broken();
        return (row, read);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static PersistenceConflictException Broken() => new("document_processing_history_invalid", "Document processing history is incomplete or inconsistent");
}
