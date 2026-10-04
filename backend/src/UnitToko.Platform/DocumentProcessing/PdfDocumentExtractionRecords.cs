using System.Globalization;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

/// <summary>Immutable multipage owner. Admission, authority and current worker claim remain independently fenced by the caller.</summary>
internal sealed partial class PdfDocumentExtractionRecords(IMongoDatabase database)
{
    internal const string Policies = "pdfDocumentProcessingPolicies", ProcessingReads = "pdfDocumentProcessingReadAudits",
        Results = "pdfDocumentExtractionResults", Audits = "pdfDocumentExtractionAudits", Failures = "pdfDocumentProcessingFailures", Reads = "pdfDocumentExtractionReadAudits";
    internal static string Hash(BsonDocument row) { var copy = row.DeepClone().AsBsonDocument; copy.Remove("resultHash"); copy.Remove("recordHash"); return PdfEvidenceValidationRecords.Hash(copy); }
    internal static BsonDocument Original(ExpectedPublication value) => PdfEvidenceValidationRecords.Original(value);
    internal static BsonDocument RasterProvenance(PdfRasterProvenance value) => PdfEvidenceValidationRecords.Provenance(value);
    internal static BsonDocument RasterProvenance(PdfDocumentRasterProvenance value) => new() { { "pipeline", value.Pipeline }, { "wrapper", value.Wrapper },
        { "pdfium", value.Pdfium }, { "wheelSha256", value.WheelSha256 }, { "pillow", value.Pillow }, { "runtimeSha256", value.RuntimeSha256 } };
    internal static BsonDocument OcrProvenance(ExtractionProvenance value) => ExtractionRecords.Provenance(value);
    internal static string PipelineId(string sourceKind, PdfRasterProvenance raster, ExtractionProvenance ocr) => Hash(new BsonDocument {
        { "version", "pdf-document-ocr-pipeline-v1" }, { "sourceKind", sourceKind }, { "inputMime", PdfDocumentExtractionContract.InputMime },
        { "rasterProvenance", RasterProvenance(raster) }, { "ocrProvenance", OcrProvenance(ocr) } });
    internal static string Kind(string sourceKind) => sourceKind switch { "Payment" => PdfDocumentExtractionContract.PaymentJobKind,
        "SupplierInvoice" => PdfDocumentExtractionContract.SupplierJobKind, _ => throw Broken() };
    internal static string SourceKey(string sourceKind, long submissionId) => Kind(sourceKind) + ":" + submissionId.ToString(CultureInfo.InvariantCulture);
    internal static BsonDocument Payload(string sourceKind, BsonDocument binding, ExpectedPublication original) => Payload(sourceKind, binding, Original(original));
    private static BsonDocument Payload(string sourceKind, BsonDocument binding, BsonDocument original)
    {
        var payload = new BsonDocument { { "sourceKind", sourceKind }, { "submissionId", binding["submissionId"] }, { "evidence", binding["reference"] },
            { "pinId", binding["pinId"] }, { "submissionHash", binding["submissionHash"] }, { "uploaderId", binding["uploaderId"] },
            { "reservationId", binding["reservationId"] }, { "original", original } };
        if (sourceKind == "Payment") payload.Add("declarationId", binding["declarationId"]).Add("declarationRevision", binding["declarationRevision"])
            .Add("reviewRevision", binding["reviewRevision"]).Add("declarationVersionId", binding["declarationVersionId"]);
        else if (sourceKind == "SupplierInvoice") payload.Add("draftId", binding["draftId"]).Add("draftRevision", binding["draftRevision"]).Add("draftHash", binding["draftHash"]);
        else throw Broken(); return payload;
    }
    internal static BsonDocument Receipt(BsonDocument row) => new() { { "pdfDocumentExtraction", true }, { "sourceKind", row["sourceKind"] },
        { "resultId", row["_id"] }, { "resultHash", row["resultHash"] }, { "readAuditId", row["readAuditId"] }, { "auditId", row["auditId"] },
        { "bindingHash", Hash(row["binding"].AsBsonDocument) }, { "originalHash", Hash(row["original"].AsBsonDocument) }, { "pipelineId", row["pipelineId"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "resultId", row["_id"] },
        { "readAuditId", row["readAuditId"] }, { "outboxId", row["outboxId"] }, { "sourceKind", row["sourceKind"] }, { "resultHash", row["resultHash"] },
        { "bindingHash", Hash(row["binding"].AsBsonDocument) }, { "originalHash", Hash(row["original"].AsBsonDocument) },
        { "consumerId", row["consumerId"] }, { "pipelineId", row["pipelineId"] }, { "createdAt", row["createdAt"] } };

    internal async Task<BsonDocument> ReadAuditAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        Transaction(tx); var row = await Rows(ProcessingReads).Find(tx, Shape(ProcessingReads, new("_id", id))).SingleOrDefaultAsync(ct) ?? throw Broken();
        if (row["recordHash"] != Hash(row)) throw Broken(); RequireOriginal(row); RequireProvenance(row); await RequirePinAsync(tx, row, ct); return row;
    }
    internal async Task<BsonDocument?> ResultAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct)
    {
        await CurrentJobAsync(tx, job, ct);
        var rows = await Rows(Results).Find(tx, new BsonDocument("outboxId", job["_id"])).Limit(2).ToListAsync(ct);
        var audits = await Rows(Audits).Find(tx, new BsonDocument("outboxId", job["_id"])).Limit(2).ToListAsync(ct);
        if (rows.Count == 0 && audits.Count == 0 && !job.Contains("processingReceipt")) return null;
        if (rows.Count != 1 || audits.Count != 1 || !job.Contains("processingReceipt")) throw Broken();
        var row = await Rows(Results).Find(tx, Shape(Results, new("_id", rows[0]["_id"]))).SingleOrDefaultAsync(ct) ?? throw Broken();
        if (row["resultHash"] != Hash(row) || !audits[0].Equals(Audit(row)) || !job["processingReceipt"].Equals(Receipt(row)) ||
            !job.TryGetValue("processingFence", out var fence) || !fence.IsInt64 || fence.AsInt64 <= 0) throw Broken();
        var read = await ReadAuditAsync(tx, row["readAuditId"].AsInt64, ct);
        SameAttempt(read, row);
        if (read["createdAt"].CompareTo(row["createdAt"]) > 0 || read["until"].CompareTo(row["until"]) < 0 ||
            job["createdAt"].CompareTo(read["createdAt"]) > 0 || row["claimAttempt"].AsInt32 > job["attempts"].AsInt32) throw Broken();
        RequireJob(row, job); _ = Extract(row); return row;
    }
    internal async Task<BsonDocument?> FailureAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct)
    {
        await CurrentJobAsync(tx, job, ct);
        if (!job.Contains("leaseToken")) throw Broken();
        var filter = new BsonDocument { { "outboxId", job["_id"] }, { "claimToken", job["leaseToken"] } };
        var row = await Rows(Failures).Find(tx, filter).SingleOrDefaultAsync(ct); if (row is null) return null;
        var found = await HistoricalFailureAsync(tx, row["_id"].AsInt64, ct); RequireJob(found.Failure, job); return found.Failure;
    }
    internal async Task<(BsonDocument Failure, BsonDocument Read)> HistoricalFailureAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        Transaction(tx); var row = await Rows(Failures).Find(tx, Shape(Failures, new("_id", id))).SingleOrDefaultAsync(ct) ?? throw Broken();
        var read = await ReadAuditAsync(tx, row["readAuditId"].AsInt64, ct); SameAttempt(read, row);
        if (row["recordHash"] != Hash(row) || row["bindingHash"] != Hash(read["binding"].AsBsonDocument) || row["originalHash"] != Hash(read["original"].AsBsonDocument) ||
            read["createdAt"].CompareTo(row["createdAt"]) > 0) throw Broken();
        RequireOriginal(row); RequireProvenance(row); return (row, read);
    }
    internal async Task<BsonDocument> RequireResultAsync(IClientSessionHandle tx, BsonDocument job, string sourceKind, BsonDocument currentBinding,
        ExpectedPublication original, long resultId, string resultHash, CancellationToken ct)
    {
        var row = await ResultAsync(tx, job, ct) ?? throw Broken();
        if (row["_id"] != resultId || row["resultHash"] != resultHash || row["sourceKind"] != sourceKind) throw Broken();
        SameOriginalBinding(row, currentBinding, original); return row;
    }
    internal async Task<PdfDocumentFieldCandidate> RequireCandidateAsync(IClientSessionHandle tx, BsonDocument job, string sourceKind, BsonDocument currentBinding,
        ExpectedPublication original, PdfDocumentCandidateReference reference, CancellationToken ct)
    {
        var row = await RequireResultAsync(tx, job, sourceKind, currentBinding, original, reference.ResultId, reference.ResultHash, ct);
        var result = Extract(row);
        if (result.Evidence != reference.Evidence || reference.PageIndex >= result.Pages.Count) throw Broken();
        return result.Pages[reference.PageIndex].Fields.SingleOrDefault(field => field.Field == reference.Field) ?? throw Broken();
    }
    internal async Task<PdfDocumentExtractionView> ReadViewAsync(IClientSessionHandle tx, BsonDocument job, string sourceKind, BsonDocument currentBinding,
        ExpectedPublication original, long actorId, long actorGrantVersion, IdAllocator ids, IClock clock, string purpose, string correlationId, CancellationToken ct)
    {
        Transaction(tx);
        if (actorId <= 0 || actorGrantVersion <= 0 || actorId == long.MaxValue || actorGrantVersion == long.MaxValue) throw Broken();
        if (sourceKind == "Payment" ? purpose is not ("payment-pdf-ocr-view" or "payment-review-pdf-ocr-view") :
            sourceKind != "SupplierInvoice" || purpose != "supplier-document-pdf-ocr-view") throw Broken();
        Text(correlationId, 128);
        if (job.GetValue("kind", "") != Kind(sourceKind) || job.GetValue("sourceKey", "") != SourceKey(sourceKind, currentBinding["submissionId"].AsInt64) ||
            !job.GetValue("payload", BsonNull.Value).Equals(Payload(sourceKind, currentBinding, original))) throw Broken();
        var row = await ResultAsync(tx, job, ct);
        var state = row is not null ? "succeeded" : job["status"].AsString switch { "pending" => "queued", "leased" => "running", "retry" => "retry", "dead" => "dead", _ => throw Broken() };
        string? failure = null;
        if (row is not null) SameOriginalBinding(row, currentBinding, original);
        else if (state is "retry" or "dead") {
            var failed = await Rows(Failures).Find(tx, new BsonDocument("outboxId", job["_id"])).Sort(new BsonDocument("_id", -1)).FirstOrDefaultAsync(ct);
            if (failed is not null) {
                var historical = await HistoricalFailureAsync(tx, failed["_id"].AsInt64, ct); RequireJob(historical.Failure, job);
                SameOriginalBinding(historical.Failure, currentBinding, original); failure = historical.Failure["safeFailure"].AsString;
            }
        }
        var sourceId = currentBinding[sourceKind == "Payment" ? "declarationId" : "draftId"].AsInt64;
        var sourceRevision = currentBinding[sourceKind == "Payment" ? "declarationRevision" : "draftRevision"].AsInt64;
        var reference = Reference(currentBinding["reference"].AsBsonDocument);
        var audit = new BsonDocument { { "_id", (await ids.NextAsync(Reads, ct)).Value }, { "schemaVersion", 1 }, { "actorId", actorId }, { "actorGrantVersion", actorGrantVersion },
            { "outboxId", job["_id"] }, { "sourceKind", sourceKind }, { "sourceId", sourceId }, { "sourceRevision", sourceRevision }, { "submissionId", currentBinding["submissionId"] },
            { "reference", currentBinding["reference"] }, { "resultId", row is null ? BsonNull.Value : row["_id"] }, { "resultHash", row is null ? BsonNull.Value : row["resultHash"] },
            { "state", state }, { "purpose", purpose }, { "correlationId", correlationId }, { "createdAt", clock.UtcNow.UtcDateTime } };
        await Rows(Reads).InsertOneAsync(tx, audit, cancellationToken: ct);
        return new(sourceId, sourceRevision, currentBinding["submissionId"].AsInt64, sourceKind, reference, state, row?["_id"].AsInt64, row?["resultHash"].AsString,
            row is null ? null : new DateTimeOffset(row["createdAt"].ToUniversalTime()), row is null ? null : Extract(row), failure);
    }
    internal static BsonDocument CandidateReference(PdfDocumentCandidateReference value) => new() { { "resultId", value.ResultId }, { "resultHash", value.ResultHash },
        { "evidence", Reference(value.Evidence) }, { "pageIndex", value.PageIndex }, { "field", value.Field } };
    private static void SameAttempt(BsonDocument read, BsonDocument row) {
        foreach (var name in new[] { "outboxId", "consumerId", "pipelineId", "policyId", "policyRevision", "claimToken", "claimAttempt", "sourceKind", "purpose", "inputMime", "binding", "original", "rasterProvenance", "ocrProvenance" })
            if (!read[name].Equals(row[name])) throw Broken();
    }
    private static void SameOriginalBinding(BsonDocument row, BsonDocument currentBinding, ExpectedPublication original) {
        var old = row["binding"].DeepClone().AsBsonDocument; var current = currentBinding.DeepClone().AsBsonDocument;
        foreach (var key in new[] { "readPolicyId", "readPolicyRevision", "validationPolicyId", "validationPolicyRevision" }) { old.Remove(key); current.Remove(key); }
        if (!old.Equals(current) || !row["original"].Equals(Original(original))) throw Broken();
    }
    private static void RequireJob(BsonDocument row, BsonDocument job) {
        var sourceKind = row["sourceKind"].AsString;
        if (row["outboxId"] != job["_id"] || job["kind"] != Kind(sourceKind) || job["sourceKey"] != SourceKey(sourceKind, row["binding"]["submissionId"].AsInt64) ||
            !job["payload"].Equals(Payload(sourceKind, row["binding"].AsBsonDocument, row["original"].AsBsonDocument))) throw Broken();
    }
    private static void RequireOriginal(BsonDocument row) {
        var binding = row["binding"].AsBsonDocument; var original = row["original"].AsBsonDocument;
        if (binding["reference"]["sha256"] != original["sha256"] || binding["reservationId"] != original["reservationId"] || binding["storeId"] != original["storeId"] ||
            binding["generation"] != original["generation"] || binding["length"] != original["length"] || original["length"].AsInt64 > PdfDocumentExtractionContract.MaximumOriginalBytes) throw Broken();
    }
    private static void RequireProvenance(BsonDocument row) {
        var raster = row["rasterProvenance"].AsBsonDocument;
        if (raster["pipeline"] != PdfRasterProtocol.Pipeline || raster["wrapper"] != PdfRasterProtocol.Wrapper || raster["pdfium"] != PdfRasterProtocol.Pdfium ||
            raster["wheelSha256"] != PdfRasterProtocol.WheelSha256 || !PdfRasterProtocol.Hash(raster["runtimeSha256"].AsString)) throw Broken();
        var ocr = ExtractionRecords.Provenance(row["ocrProvenance"].AsBsonDocument);
        if (ocr.EngineVersion != LocalOcrProtocol.Engine || ocr.PreprocessingVersion != (row["sourceKind"] == "Payment" ? LocalOcrProtocol.PaymentPreprocessing :
            LocalOcrProtocol.ImagePreprocessing + "/" + SupplierDocumentFieldExtractor.Version)) throw Broken();
        if (row["pipelineId"] != PipelineId(row["sourceKind"].AsString,
            new(raster["pipeline"].AsString, raster["wrapper"].AsString, raster["pdfium"].AsString, raster["wheelSha256"].AsString, raster["pillow"].AsString, raster["runtimeSha256"].AsString), ocr)) throw Broken();
    }
    private async Task RequirePinAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct)
    {
        var binding = row["binding"].AsBsonDocument; var sourceKind = row["sourceKind"].AsString;
        var pin = await EvidencePinRecords.ReadAsync(database, tx, binding["pinId"].AsInt64, ct);
        foreach (var name in new[] { "reference", "reservationId", "versionRowId", "pinHash", "storeId", "generation", "validationHash", "lifecycleRevision" })
            if (binding[name] != pin[name]) throw Broken();
        if (pin["associationId"] != binding["submissionId"] || pin["associationHash"] != binding["submissionHash"] || pin["actorId"] != binding["uploaderId"] ||
            pin["createdAt"].CompareTo(row["createdAt"]) > 0) throw Broken();
        if (sourceKind == "Payment") {
            if (pin["associationKind"] != "PaymentSubmission") throw Broken();
            foreach (var name in new[] { "declarationId", "declarationVersionId", "declarationRevision", "reviewRevision" }) if (pin[name] != binding[name]) throw Broken();
        } else if (sourceKind == "SupplierInvoice") {
            if (pin["associationKind"] != "SupplierDocument" || pin["supplierDocumentDraftId"] != binding["draftId"] || pin["supplierDocumentDraftRevision"] != binding["draftRevision"]) throw Broken();
        } else throw Broken();
        var reservation = await Rows("evidenceUploadReservations").Find(tx, new BsonDocument("_id", binding["reservationId"])).SingleOrDefaultAsync(ct) ?? throw Broken();
        var version = await Rows("documentVersions").Find(tx, new BsonDocument("_id", binding["versionRowId"])).SingleOrDefaultAsync(ct) ?? throw Broken();
        var attempt = await Rows("evidenceUploadAttempts").Find(tx, new BsonDocument("_id", version["attemptId"])).SingleOrDefaultAsync(ct) ?? throw Broken();
        var expected = new ExpectedPublication(new(attempt["storeId"].AsString, reservation["_id"].AsInt64, attempt["_id"].AsInt64,
            attempt["ticketRevision"].AsInt64, attempt["stageToken"].AsString, attempt["generation"].AsString), attempt["contentRevision"].AsInt64,
            new(attempt["length"].AsInt64, attempt["sha256"].AsString));
        if (!row["original"].Equals(Original(expected)) || reservation["purpose"] != (sourceKind == "Payment" ? "PaymentPdf" : "SupplierDocumentPdf") ||
            reservation["uploader"]["userId"] != binding["uploaderId"] || reservation["versionRowId"] != version["_id"] ||
            version["lineageId"] != binding["reference"]["id"] || version["contentVersion"] != binding["reference"]["version"] || version["sha256"] != binding["reference"]["sha256"] ||
            version["storeId"] != binding["storeId"] || version["generation"] != binding["generation"] || version["length"] != binding["length"]) throw Broken();
        var validation = await Rows(PdfEvidenceValidationRecords.Results).Find(tx, new BsonDocument("_id", binding["versionRowId"])).SingleOrDefaultAsync(ct) ?? throw Broken();
        var job = await Rows("outboxEvents").Find(tx, new BsonDocument("_id", validation["outboxId"])).SingleOrDefaultAsync(ct) ?? throw Broken();
        var proof = await PdfEvidenceValidationRecords.ReadAsync(database, tx, job, expected, validation["source"].AsBsonDocument, ct) ?? throw Broken();
        if (proof["status"] != "passed" || proof["resultHash"] != binding["validationHash"]) throw Broken();
    }
    private async Task CurrentJobAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct) {
        Transaction(tx);
        if (job.GetValue("kind", "") != PdfDocumentExtractionContract.PaymentJobKind && job.GetValue("kind", "") != PdfDocumentExtractionContract.SupplierJobKind) throw Broken();
        var current = await Rows("outboxEvents").Find(tx, new BsonDocument("_id", job["_id"])).SingleOrDefaultAsync(ct) ?? throw Broken();
        if (!current.Equals(job)) throw Broken();
    }
    private void Transaction(IClientSessionHandle tx) { if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw Broken(); }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static BsonDocument Shape(string name, BsonDocument identity) => new("$and", new BsonArray { identity, PdfDocumentExtractionSchemas.Validator(name) });
    internal static PersistenceConflictException Broken() => new("pdf_document_processing_history_invalid", "Riwayat pemrosesan seluruh PDF perlu diperiksa petugas; dokumen asli tetap dipertahankan.");

    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var name in PdfDocumentExtractionSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Broken();
            if (!AccountContactStorage.SafeDefinition(definition, PdfDocumentExtractionSchemas.Validator(name))) throw Broken();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var keys = PdfDocumentExtractionSchemas.Keys(name);
            if (!SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key)))) throw Broken();
        }
        var outbox = await AccountContactStorage.DefinitionAsync(database, "outboxEvents", ct) ?? throw Broken();
        if (!PdfEvidenceAdmissionSchemas.SafeDefinition(outbox) || !PdfDocumentExtractionSchemas.SupportsOutbox(outbox["options"]["validator"].AsBsonDocument, PdfDocumentExtractionSchemas.ExtendedOutboxValidator())) throw Broken();
        PaymentEvidenceMigration.RequireIndexes("outboxEvents", await (await database.GetCollection<BsonDocument>("outboxEvents").Indexes.ListAsync(ct)).ToListAsync(ct));
    }
    internal static bool SafeIndexes(IReadOnlyList<BsonDocument> rows, (string Name, BsonDocument Key, bool Unique)[] keys) =>
        rows.Any(row => row.GetValue("name", "") == "_id_" && row.GetValue("key", BsonNull.Value) == new BsonDocument("_id", 1)) &&
        rows.All(row => !InvoiceStorage.Unsafe(row) && !row.Contains("partialFilterExpression") && (row.GetValue("name", "") == "_id_" ||
            keys.Any(key => AccountContactStorage.Matches(row, key)) || !row.GetValue("unique", false).AsBoolean && keys.All(key => row.GetValue("name", "") != key.Name && row.GetValue("key", BsonNull.Value) != key.Key)));
    private static BsonDocument Reference(EvidenceRef value) => new() { { "id", value.Id }, { "version", value.Version }, { "sha256", value.Sha256 } };
    private static EvidenceRef Reference(BsonDocument value) => new(value["id"].AsInt64, value["version"].AsInt64, value["sha256"].AsString);
    private static void Text(string value, int maximum) {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != value.Trim() || !value.IsNormalized(System.Text.NormalizationForm.FormC) ||
            value.Any(v => char.IsControl(v) || char.GetUnicodeCategory(v) == UnicodeCategory.Format)) throw Broken();
    }
}
