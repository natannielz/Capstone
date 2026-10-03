using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed class SupplierDocumentExtractionRecords(IMongoDatabase database)
{
    internal const string Kind = "supplierdocument.ocr.requested";
    internal static string Hash(BsonDocument row) {
        var value = row.DeepClone().AsBsonDocument; value.Remove("resultHash"); value.Remove("recordHash"); return EvidenceValidationRecords.Hash(value);
    }
    internal static BsonDocument Provenance(ExtractionProvenance value) => new() { { "engineVersion", value.EngineVersion }, { "modelVersion", value.ModelVersion },
        { "modelSha256", value.ModelSha256 }, { "runtimeSha256", value.RuntimeSha256 }, { "preprocessingVersion", value.PreprocessingVersion } };
    internal static ExtractionProvenance Provenance(BsonDocument value) => new(value["engineVersion"].AsString, value["modelVersion"].AsString,
        value["modelSha256"].AsString, value["runtimeSha256"].AsString, value["preprocessingVersion"].AsString);
    internal static BsonDocument Output(ExtractionResult value) {
        if (value.Purpose != ExtractionPurpose.SupplierInvoice || value.Status != "succeeded" || value.Fields.Count != 0) throw Broken();
        var parsed = SupplierDocumentFieldExtractor.Extract(value.Lines);
        return new() {
        { "lines", new BsonArray(value.Lines.Select(line => new BsonDocument { { "text", line.Text }, { "confidence", new Decimal128(line.Confidence) }, { "box", Box(line.Box.Values) } })) },
        { "fields", new BsonArray(parsed.Fields.Select(field => new BsonDocument { { "field", field.Field }, { "value", field.Value is null ? BsonNull.Value : new BsonString(field.Value) },
            { "confidence", field.Confidence is { } c ? new BsonDecimal128(c) : BsonNull.Value }, { "box", field.BoundingBox is { } box ? Box(box) : BsonNull.Value },
            { "flags", new BsonArray(field.Flags) } })) }, { "qualityFlags", new BsonArray(parsed.QualityFlags) } };
    }
    private static BsonArray Box(IReadOnlyList<decimal> values) => new(values.Select(v => new BsonDecimal128(v)));
    internal static ExtractionResult Extract(BsonDocument row) {
        var reference = row["binding"]["reference"];
        var lines = row["output"]["lines"].AsBsonArray.Select(value => {
            var box = value["box"].AsBsonArray;
            return new RecognizedLine(value["text"].AsString, value["confidence"].AsDecimal, new(box[0].AsDecimal, box[1].AsDecimal, box[2].AsDecimal, box[3].AsDecimal));
        }).ToArray();
        var output = new ExtractionResult(row["outboxId"].AsObjectId.ToString(), new(reference["id"].AsInt64, reference["version"].AsInt64, reference["sha256"].AsString),
            ExtractionPurpose.SupplierInvoice, Provenance(row["provenance"].AsBsonDocument), "succeeded", null, lines);
        if (!Output(output).Equals(row["output"])) throw Broken(); return output;
    }
    internal static BsonDocument Receipt(BsonDocument row) => new() { { "supplierDocumentExtraction", true }, { "resultId", row["_id"] }, { "resultHash", row["resultHash"] }, { "readAuditId", row["readAuditId"] },
        { "auditId", row["auditId"] }, { "bindingHash", Hash(row["binding"].AsBsonDocument) }, { "pipelineId", row["pipelineId"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "resultId", row["_id"] }, { "readAuditId", row["readAuditId"] },
        { "outboxId", row["outboxId"] }, { "resultHash", row["resultHash"] }, { "bindingHash", Hash(row["binding"].AsBsonDocument) },
        { "consumerId", row["consumerId"] }, { "pipelineId", row["pipelineId"] }, { "createdAt", row["createdAt"] } };
    internal async Task<BsonDocument> ReadAuditAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows("supplierDocumentProcessingReadAudits").Find(tx, Shape("supplierDocumentProcessingReadAudits", new("_id", id))).SingleOrDefaultAsync(ct) ?? throw Broken();
        if (row["recordHash"] != Hash(row)) throw Broken(); return row;
    }
    internal async Task<BsonDocument?> ResultAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct) {
        var rows = await Rows("supplierDocumentExtractionResults").Find(tx, new BsonDocument("outboxId", job["_id"])).Limit(2).ToListAsync(ct);
        var audits = await Rows("supplierDocumentExtractionAudits").Find(tx, new BsonDocument("outboxId", job["_id"])).Limit(2).ToListAsync(ct);
        if (rows.Count == 0 && audits.Count == 0 && !job.Contains("processingReceipt")) return null;
        if (rows.Count != 1 || audits.Count != 1 || !job.Contains("processingReceipt")) throw Broken();
        var row = await Rows("supplierDocumentExtractionResults").Find(tx, Shape("supplierDocumentExtractionResults", new("_id", rows[0]["_id"]))).SingleOrDefaultAsync(ct) ?? throw Broken();
        if (row["resultHash"] != Hash(row) || !audits[0].Equals(Audit(row)) || !job["processingReceipt"].Equals(Receipt(row))) throw Broken();
        var read = await ReadAuditAsync(tx, row["readAuditId"].AsInt64, ct);
        foreach (var name in new[] { "outboxId", "consumerId", "pipelineId", "policyId", "policyRevision", "claimToken", "claimAttempt", "binding" })
            if (!read[name].Equals(row[name])) throw Broken();
        if (read["createdAt"].CompareTo(row["createdAt"]) > 0 || read["until"].CompareTo(row["until"]) < 0) throw Broken();
        var binding = row["binding"];
        var payload = new BsonDocument { { "submissionId", binding["submissionId"] }, { "draftId", binding["draftId"] }, { "draftRevision", binding["draftRevision"] },
            { "evidence", binding["reference"] }, { "pinId", binding["pinId"] }, { "submissionHash", binding["submissionHash"] }, { "draftHash", binding["draftHash"] } };
        if (job["kind"] != Kind || job["sourceKey"] != $"supplierdocument.ocr:{binding["submissionId"].AsInt64}:1" ||
            !payload.Equals(job["payload"]) || job["createdAt"].CompareTo(read["createdAt"]) > 0 ||
            !job.TryGetValue("processingFence", out var fence) || !fence.IsInt64 || fence.AsInt64 <= 0) throw Broken();
        _ = Extract(row); return row;
    }
    internal async Task<BsonDocument?> FailureAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct) {
        var filter = new BsonDocument { { "outboxId", job["_id"] }, { "claimToken", job["leaseToken"] } };
        var raw = await Rows("supplierDocumentProcessingFailures").Find(tx, filter).SingleOrDefaultAsync(ct); if (raw is null) return null;
        var row = await Rows("supplierDocumentProcessingFailures").Find(tx, Shape("supplierDocumentProcessingFailures", filter)).SingleOrDefaultAsync(ct) ?? throw Broken();
        var read = await ReadAuditAsync(tx, row["readAuditId"].AsInt64, ct);
        if (row["recordHash"] != Hash(row) || row["bindingHash"] != Hash(read["binding"].AsBsonDocument) || read["createdAt"].CompareTo(row["createdAt"]) > 0) throw Broken();
        foreach (var name in new[] { "outboxId", "consumerId", "pipelineId", "policyId", "policyRevision", "claimToken", "claimAttempt" }) if (row[name] != read[name]) throw Broken();
        return row;
    }
    internal static BsonDocument Shape(string name, BsonDocument identity) => new("$and", new BsonArray { identity, SupplierDocumentProcessingSchemas.Validator(name) });
    // A manual-review consumer keeps the actual failed attempt; it never manufactures extraction output.
    internal async Task<(BsonDocument Failure, BsonDocument Read)> HistoricalFailureAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows("supplierDocumentProcessingFailures").Find(tx, Shape("supplierDocumentProcessingFailures", new("_id", id))).SingleOrDefaultAsync(ct) ?? throw Broken();
        var read = await ReadAuditAsync(tx, row["readAuditId"].AsInt64, ct);
        if (row["recordHash"] != Hash(row) || row["bindingHash"] != Hash(read["binding"].AsBsonDocument) || read["createdAt"].CompareTo(row["createdAt"]) > 0) throw Broken();
        foreach (var name in new[] { "outboxId", "consumerId", "pipelineId", "policyId", "policyRevision", "claimToken", "claimAttempt" }) if (row[name] != read[name]) throw Broken();
        return (row, read);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in SupplierDocumentProcessingSchemas.Collections)
            await UnitToko.Platform.Inventory.InventoryRepository.RequireStorageAsync(database, name, SupplierDocumentProcessingSchemas.Validator(name), SupplierDocumentProcessingSchemas.Key(name), ct);
        await DocumentProcessingStorage.RequireOutboxAsync(database, SupplierDocumentProcessingSchemas.OutboxValidator(), ct);
    }
    internal async Task<BsonDocument> RequireResultAsync(IClientSessionHandle tx, BsonDocument job, SupplierDocumentOcrSourceSnapshot source, long resultId, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || resultId <= 0) throw Broken();
        var row = await ResultAsync(tx, job, ct) ?? throw Broken(); var binding = row["binding"];
        var reference = new BsonDocument { { "id", source.Evidence.Id }, { "version", source.Evidence.Version }, { "sha256", source.Evidence.Sha256 } };
        if (row["_id"] != resultId || binding["submissionId"] != source.SubmissionId || binding["draftId"] != source.DraftId || binding["draftRevision"] != source.DraftRevision ||
            binding["submissionHash"] != source.SubmissionHash || binding["draftHash"] != source.DraftHash || binding["reservationId"] != source.ReservationId || binding["pinId"] != source.PinId ||
            !binding["reference"].Equals(reference)) throw Broken();
        return row;
    }
    internal async Task<SupplierDocumentExtractionView> ReadViewAsync(IClientSessionHandle tx, BsonDocument job, SupplierDocumentOcrSourceSnapshot source, BsonDocument currentBinding,
        long actorId, long actorGrantVersion, IdAllocator ids, IClock clock, string correlationId, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || actorId <= 0 || actorGrantVersion <= 0 ||
            string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128 || correlationId != correlationId.Trim() || correlationId.Any(char.IsControl)) throw Broken();
        var payload = new BsonDocument { { "submissionId", source.SubmissionId }, { "draftId", source.DraftId }, { "draftRevision", source.DraftRevision },
            { "evidence", new BsonDocument { { "id", source.Evidence.Id }, { "version", source.Evidence.Version }, { "sha256", source.Evidence.Sha256 } } },
            { "pinId", source.PinId }, { "submissionHash", source.SubmissionHash }, { "draftHash", source.DraftHash } };
        if (job.GetValue("kind", "") != Kind || job.GetValue("sourceKey", "") != $"supplierdocument.ocr:{source.SubmissionId}:1" || !payload.Equals(job.GetValue("payload", BsonNull.Value))) throw Broken();
        var result = await ResultAsync(tx, job, ct); string? failure = null;
        var state = result is not null ? "succeeded" : job["status"].AsString switch { "pending" => "queued", "leased" => "running", "retry" => "retry", "dead" => "dead", _ => throw Broken() };
        if (result is null && state is "retry" or "dead") {
            var failed = await Rows("supplierDocumentProcessingFailures").Find(tx, new BsonDocument("outboxId", job["_id"])).Sort(new BsonDocument("_id", -1)).FirstOrDefaultAsync(ct);
            if (failed is not null) failure = (await HistoricalFailureAsync(tx, failed["_id"].AsInt64, ct)).Failure["safeFailure"].AsString;
        }
        if (result is not null) {
            var original = result["binding"].DeepClone().AsBsonDocument; var current = currentBinding.DeepClone().AsBsonDocument;
            foreach (var field in new[] { "readPolicyId", "readPolicyRevision", "validationPolicyId", "validationPolicyRevision" }) { original.Remove(field); current.Remove(field); }
            if (!original.Equals(current) || result["binding"]["reservationId"] != source.ReservationId || result["binding"]["pinId"] != source.PinId) throw Broken();
        }
        await Rows("supplierDocumentExtractionReadAudits").InsertOneAsync(tx, new BsonDocument {
            { "_id", (await ids.NextAsync("supplierDocumentExtractionReadAudits", ct)).Value }, { "schemaVersion", 1 }, { "actorId", actorId }, { "actorGrantVersion", actorGrantVersion },
            { "outboxId", job["_id"] }, { "submissionId", source.SubmissionId }, { "draftId", source.DraftId }, { "reference", payload["evidence"] },
            { "resultId", result is null ? BsonNull.Value : result["_id"] }, { "resultHash", result is null ? BsonNull.Value : result["resultHash"] },
            { "state", state }, { "purpose", "supplier-document-ocr-view" }, { "correlationId", correlationId }, { "createdAt", clock.UtcNow.UtcDateTime }
        }, cancellationToken: ct);
        if (result is null) return new(source.DraftId, source.SubmissionId, source.Evidence, state, null, null, null, [], [], [], failure);
        var extraction = Extract(result); var suggestions = SupplierDocumentFieldExtractor.Extract(extraction.Lines);
        return new(source.DraftId, source.SubmissionId, source.Evidence, state, result["_id"].AsInt64, new(result["createdAt"].ToUniversalTime()),
            extraction.Provenance, extraction.Lines, suggestions.Fields, suggestions.QualityFlags, null);
    }
    internal static PersistenceConflictException Broken() => new("supplier_document_processing_history_invalid", "Supplier document processing history is incomplete or inconsistent");
}
