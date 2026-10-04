using MongoDB.Bson;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PdfDocumentExtractionSchemas
{
    internal static readonly string[] Collections = [PdfDocumentExtractionRecords.Policies, PdfDocumentExtractionRecords.ProcessingReads,
        PdfDocumentExtractionRecords.Results, PdfDocumentExtractionRecords.Audits, PdfDocumentExtractionRecords.Failures, PdfDocumentExtractionRecords.Reads];
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        PdfDocumentExtractionRecords.Policies => [("ux_" + name, new("consumerId", 1), true)],
        PdfDocumentExtractionRecords.ProcessingReads or PdfDocumentExtractionRecords.Failures => [("ux_" + name, new() { { "outboxId", 1 }, { "claimToken", 1 } }, true)],
        PdfDocumentExtractionRecords.Results => [("ux_" + name, new("outboxId", 1), true),
            ("ux_" + name + "_original", new() { { "binding.reference.id", 1 }, { "binding.reference.version", 1 }, { "sourceKind", 1 }, { "pipelineId", 1 } }, true)],
        PdfDocumentExtractionRecords.Audits => [("ux_" + name, new("resultId", 1), true), ("ux_" + name + "_outbox", new("outboxId", 1), true)],
        PdfDocumentExtractionRecords.Reads => [], _ => throw new ArgumentOutOfRangeException(nameof(name)) };

    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument { { "_id", Long() }, { "schemaVersion", Int(1, 1) } };
        var terms = new BsonArray();
        if (name == PdfDocumentExtractionRecords.Policies) {
            fields.AddRange(new BsonDocument { { "consumerId", Text(128) }, { "storeId", Hash() }, { "pipelineId", Hash() },
                { "sourceKind", SourceKind() }, { "purpose", SourceKind() }, { "inputMime", Choice(PdfDocumentExtractionContract.InputMime) },
                { "rasterProvenance", RasterProvenance() }, { "ocrProvenance", DocumentProcessingSchemas.Provenance() },
                { "active", Type("bool") }, { "revision", Long() }, { "fence", Long(0) }, { "approvedAt", Type("date") }, { "expiresAt", Type("date") },
                { "hostAcceptanceRef", Text(256) }, { "modelSelectionRef", Text(256) }, { "rasterAcceptanceRef", Text(256) } });
            terms.Add(new BsonDocument("$expr", And(Eq("$sourceKind", "$purpose"), Compare("$gt", "$expiresAt", "$approvedAt"),
                Compare("$lte", new BsonDocument("$subtract", new BsonArray { "$expiresAt", "$approvedAt" }), 86_400_000L * 90))));
        } else if (name == PdfDocumentExtractionRecords.Audits) {
            fields.AddRange(new BsonDocument { { "resultId", Long() }, { "readAuditId", Long() }, { "outboxId", Type("objectId") },
                { "sourceKind", SourceKind() }, { "resultHash", Hash() }, { "bindingHash", Hash() }, { "originalHash", Hash() },
                { "consumerId", Text(128) }, { "pipelineId", Hash() }, { "createdAt", Type("date") } });
        } else if (name == PdfDocumentExtractionRecords.Reads) {
            fields.AddRange(new BsonDocument { { "actorId", Long() }, { "actorGrantVersion", Long() }, { "outboxId", Type("objectId") },
                { "sourceKind", SourceKind() }, { "sourceId", Long() }, { "sourceRevision", Long() }, { "submissionId", Long() },
                { "reference", PaymentSubmissionSchemas.Reference() }, { "resultId", Nullable(Long()) }, { "resultHash", Nullable(Hash()) },
                { "state", Choice("queued", "running", "retry", "dead", "succeeded") },
                { "purpose", Choice("payment-pdf-ocr-view", "payment-review-pdf-ocr-view", "supplier-document-pdf-ocr-view") }, { "correlationId", Text(128) }, { "createdAt", Type("date") } });
            terms.Add(new BsonDocument("$expr", And(
                Or(And(Eq("$sourceKind", "Payment"), Or(Eq("$purpose", "payment-pdf-ocr-view"), Eq("$purpose", "payment-review-pdf-ocr-view"))),
                    And(Eq("$sourceKind", "SupplierInvoice"), Eq("$purpose", "supplier-document-pdf-ocr-view"))),
                Or(And(Eq("$state", "succeeded"), Compare("$ne", "$resultId", BsonNull.Value), Compare("$ne", "$resultHash", BsonNull.Value)),
                    And(Compare("$ne", "$state", "succeeded"), Eq("$resultId", BsonNull.Value), Eq("$resultHash", BsonNull.Value))))));
        } else {
            Common(fields); fields.AddRange(new BsonDocument { { "sourceKind", SourceKind() }, { "purpose", SourceKind() },
                { "inputMime", Choice(PdfDocumentExtractionContract.InputMime) }, { "binding", Binding() }, { "original", Original() },
                { "rasterProvenance", RasterProvenance() }, { "ocrProvenance", DocumentProcessingSchemas.Provenance() } });
            terms.Add(new BsonDocument("$expr", And(Eq("$sourceKind", "$purpose"), Compare("$gt", "$until", "$createdAt"),
                Compare("$lte", new BsonDocument("$subtract", new BsonArray { "$until", "$createdAt" }), 60_000L),
                Eq("$binding.reservationId", "$original.reservationId"), Eq("$binding.reference.sha256", "$original.sha256"),
                Eq("$binding.storeId", "$original.storeId"), Eq("$binding.generation", "$original.generation"), Eq("$binding.length", "$original.length"))));
            terms.Add(new BsonDocument("$or", new BsonArray {
                new BsonDocument { { "sourceKind", "Payment" }, { "binding.declarationId", new BsonDocument("$exists", true) } },
                new BsonDocument { { "sourceKind", "SupplierInvoice" }, { "binding.draftId", new BsonDocument("$exists", true) } } }));
            switch (name) {
                case PdfDocumentExtractionRecords.ProcessingReads: fields.Add("recordHash", Hash()); break;
                case PdfDocumentExtractionRecords.Results:
                    fields.AddRange(new BsonDocument { { "readAuditId", Long() }, { "auditId", Long() }, { "resultHash", Hash() },
                        { "pageCount", Int(1, 10) }, { "requiresHumanReview", True() }, { "output", Output() } });
                    terms.Add(new BsonDocument("$expr", Eq("$pageCount", new BsonDocument("$size", "$output.pages")))); break;
                case PdfDocumentExtractionRecords.Failures:
                    fields.AddRange(new BsonDocument { { "readAuditId", Long() }, { "bindingHash", Hash() }, { "originalHash", Hash() }, { "recordHash", Hash() },
                        { "safeFailure", SafeFailure() }, { "failurePageIndex", Nullable(Int(0, 9)) } }); break;
                default: throw new ArgumentOutOfRangeException(nameof(name));
            }
        }
        if (name != PdfDocumentExtractionRecords.Policies) terms.Add(StockSourceSetupSchemas.DateRange("createdAt"));
        terms.Insert(0, new BsonDocument("$jsonSchema", Object(fields))); return new("$and", terms);
    }

    private static void Common(BsonDocument fields) => fields.AddRange(new BsonDocument { { "outboxId", Type("objectId") }, { "consumerId", Text(128) }, { "pipelineId", Hash() },
        { "policyId", Long() }, { "policyRevision", Long() }, { "claimToken", Token() }, { "claimAttempt", Int(1, 5) }, { "createdAt", Type("date") }, { "until", Type("date") } });
    internal static BsonDocument Binding() => new("oneOf", new BsonArray { BoundBinding(DocumentProcessingSchemas.Binding()), BoundBinding(SupplierDocumentProcessingSchemas.Binding()) });
    private static BsonDocument BoundBinding(BsonDocument value) {
        foreach (var property in value["properties"].AsBsonDocument.Elements)
            if (property.Value.AsBsonDocument.GetValue("bsonType", "") == "long") property.Value.AsBsonDocument["maximum"] = long.MaxValue - 1;
        value["properties"]["length"] = Long(1, PdfDocumentExtractionContract.MaximumOriginalBytes);
        value["properties"].AsBsonDocument.Add("uploaderId", Long()); value["required"].AsBsonArray.Add("uploaderId"); return value;
    }
    internal static BsonDocument Original() {
        var value = VideoValidationSchemas.Original();
        foreach (var property in value["properties"].AsBsonDocument.Elements)
            if (property.Value.AsBsonDocument.GetValue("bsonType", "") == "long") property.Value.AsBsonDocument["maximum"] = long.MaxValue - 1;
        value["properties"]["length"] = Long(1, PdfDocumentExtractionContract.MaximumOriginalBytes); return value;
    }
    internal static BsonDocument RasterProvenance() => Object(new() { { "pipeline", Text(128) }, { "wrapper", Text(128) }, { "pdfium", Text(128) },
        { "wheelSha256", Hash() }, { "pillow", Text(32) }, { "runtimeSha256", Hash() } });
    private static BsonDocument PageRaster() => Object(new() { { "pageIndex", Int(0, 9) }, { "width", Int(1, 4096) }, { "height", Int(1, 4096) },
        { "length", Long(57, 10 * 1024 * 1024) }, { "sha256", Hash() }, { "rotationDegrees", IntChoice(0, 90, 180, 270) },
        { "canvasSize", Array(Coordinate(0.000001, 14400), 2, 2) }, { "visibleBox", Array(Coordinate(-14400, 14400), 4, 4) },
        { "pixelCornersInPdfCanvas", Corners() }, { "reviewReasons", Array(Choice("human_review_required", "no_visible_marks"), 1, 2) } });
    private static BsonDocument Preprocessing() => Object(new() { { "orientationDegrees", IntChoice(0, 90, 180, 270) },
        { "deskewDegrees", Decimal(-10, 10) }, { "contrastApplied", Type("bool") }, { "workingWidth", Int(0, 4800) }, { "workingHeight", Int(0, 4800) },
        { "orientationScore", Fraction() }, { "orientationMargin", Fraction() }, { "reviewReasons", Array(Choice("no_text", "orientation_unresolved", "skew_out_of_range", "low_text_confidence"), 0, 1) } });
    private static BsonDocument Field() => Object(new() { { "field", FieldName() }, { "value", Nullable(Text(256)) }, { "confidence", Nullable(Fraction()) },
        { "imageBox", Nullable(Box()) }, { "pdfCanvasCorners", Nullable(Corners()) }, { "flags", Flags() } });
    internal static BsonDocument Output() => Object(new() {
        { "pages", Array(Object(new() { { "raster", PageRaster() }, { "ocrProvenance", DocumentProcessingSchemas.Provenance() }, { "preprocessing", Preprocessing() },
            { "lines", Array(Object(new() { { "text", Text(512) }, { "confidence", Fraction() }, { "imageBox", Box() }, { "pdfCanvasCorners", Corners() } }), 0, 128) },
            { "fields", Array(Field(), 8, 8) }, { "reviewReasons", Flags() } }), 1, 10) },
        { "fields", Array(Object(new() { { "field", FieldName() }, { "observations", Array(Object(new() { { "pageIndex", Int(0, 9) }, { "candidate", Field() } }), 1, 10) },
            { "reviewReasons", Array(Choice("human_review_required", "unknown_on_page", "ambiguous_on_page", "contradictory_pages", "repeated_across_pages"), 1, 5) } }), 8, 8) } });
    internal static BsonDocument CandidateReference() => Object(new() { { "resultId", Long() }, { "resultHash", Hash() },
        { "evidence", PaymentSubmissionSchemas.Reference() }, { "pageIndex", Int(0, 9) }, { "field", FieldName() } });
    internal static BsonDocument Receipt() => Object(new() { { "pdfDocumentExtraction", True() }, { "sourceKind", SourceKind() }, { "resultId", Long() },
        { "resultHash", Hash() }, { "readAuditId", Long() }, { "auditId", Long() }, { "bindingHash", Hash() }, { "originalHash", Hash() }, { "pipelineId", Hash() } });
    internal static BsonDocument Payload(string kind)
    {
        var fields = new BsonDocument { { "sourceKind", Choice(kind == PdfDocumentExtractionContract.PaymentJobKind ? "Payment" : "SupplierInvoice") },
            { "submissionId", Long() }, { "evidence", PaymentSubmissionSchemas.Reference() }, { "pinId", Long() }, { "submissionHash", Hash() },
            { "uploaderId", Long() }, { "reservationId", Long() }, { "original", Original() } };
        if (kind == PdfDocumentExtractionContract.PaymentJobKind) fields.Add("declarationId", Long()).Add("declarationRevision", Long()).Add("reviewRevision", Long()).Add("declarationVersionId", Long());
        else if (kind == PdfDocumentExtractionContract.SupplierJobKind) fields.Add("draftId", Long()).Add("draftRevision", Long()).Add("draftHash", Hash());
        else throw new ArgumentOutOfRangeException(nameof(kind));
        return Object(fields);
    }
    internal static BsonDocument OutboxValidator() => new("$and", new BsonArray {
        // Older owners admit unrelated kinds. Excluding this unsupported primary-payment
        // kind explicitly prevents their fallback from admitting a PDF payment submission.
        new BsonDocument("kind", new BsonDocument("$ne", PdfDocumentExtractionContract.PaymentJobKind)),
        Job(PdfDocumentExtractionContract.SupplierJobKind) });
    private static BsonDocument Job(string kind) => new("$or", new BsonArray { new BsonDocument("kind", new BsonDocument("$ne", kind)), new BsonDocument("$and", new BsonArray {
        new BsonDocument("$jsonSchema", new BsonDocument { { "bsonType", "object" }, { "required", new BsonArray { "payload" } }, { "properties", new BsonDocument {
            { "payload", Payload(kind) }, { "processingFence", Long(0) }, { "processingReceipt", Receipt() }, { "attemptLimit", Int(1, 5) } } } }),
        new BsonDocument("$expr", Eq("$payload.evidence.sha256", "$payload.original.sha256")),
        new BsonDocument("$expr", Eq("$payload.reservationId", "$payload.original.reservationId")),
        new BsonDocument("$or", new BsonArray { new BsonDocument("status", new BsonDocument("$ne", "completed")), new BsonDocument("processingReceipt", new BsonDocument("$exists", true)) }),
        new BsonDocument("$or", new BsonArray { new BsonDocument("processingReceipt", new BsonDocument("$exists", false)),
            new BsonDocument("processingReceipt.sourceKind", kind == PdfDocumentExtractionContract.PaymentJobKind ? "Payment" : "SupplierInvoice") }) }) });
    internal static BsonDocument PreviousOutboxValidator() => PdfEvidenceValidationSchemas.ExtendedOutboxValidator();
    internal static BsonDocument ExtendedOutboxValidator() => new("$and", new BsonArray {
        new BsonDocument("$or", new BsonArray { PreviousOutboxValidator(),
            new BsonDocument("$and", new BsonArray { new BsonDocument("kind", PdfDocumentExtractionContract.SupplierJobKind), Translate(EvidenceSchemas.Validator("outboxEvents"), PdfDocumentExtractionContract.SupplierJobKind) }) }), OutboxValidator() });
    internal static BsonDocument ExtendOutbox(BsonDocument current)
    {
        var before = PreviousOutboxValidator(); var after = ExtendedOutboxValidator(); var found = false;
        BsonDocument Replace(BsonDocument value) {
            if (CatalogSchemas.SemanticEquals(value, after)) { found = true; return value.DeepClone().AsBsonDocument; }
            if (CatalogSchemas.SemanticEquals(value, before)) { found = true; return after.DeepClone().AsBsonDocument; }
            var copy = value.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                copy["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Replace(term.AsBsonDocument) : term.DeepClone()));
            return copy;
        }
        var result = Replace(current); return found ? result : throw new InvalidOperationException("PDF document OCR outbox predecessor is not a reviewed definition");
    }
    internal static bool SupportsOutbox(BsonDocument current, BsonDocument required) => InventorySchemas.Contains(current, required) ||
        InventorySchemas.Contains(current, ExtendedOutboxValidator()) &&
        (CatalogSchemas.SemanticEquals(required, PreviousOutboxValidator()) || PdfEvidenceValidationSchemas.SupportsOutbox(PreviousOutboxValidator(), required));
    private static BsonValue Translate(BsonValue value, string kind) => value.IsString
        ? value.AsString == "evidence.validation.requested" ? new BsonString(kind) : value.DeepClone()
        : value.IsBsonArray ? new BsonArray(value.AsBsonArray.Select(v => Translate(v, kind)))
        : value.IsBsonDocument ? new BsonDocument(value.AsBsonDocument.Elements.Select(e => new BsonElement(e.Name, Translate(e.Value, kind)))) : value.DeepClone();
    internal static BsonDocument SafeFailure() => Choice("ocr_unavailable", "ocr_timeout", "ocr_invalid_output", "ocr_integrity_mismatch", "ocr_invalid_media", "ocr_original_unavailable",
        "pdf_document_deadline", "pdf_document_deadline_invalid", "pdf_document_source_mismatch", "pdf_document_invalid_output", "pdf_document_page_limit", "pdf_document_unavailable",
        "pdf_raster_source_mismatch", "pdf_raster_source_limit", "pdf_raster_malformed_pdf", "pdf_raster_encrypted_pdf", "pdf_raster_unsupported_forms", "pdf_raster_unsupported_attachments", "pdf_raster_unsupported_javascript",
        "pdf_raster_page_limit", "pdf_raster_geometry_limit", "pdf_raster_total_pixel_limit", "pdf_raster_output_limit", "pdf_raster_deadline", "pdf_raster_render_failed", "pdf_raster_unavailable",
        "pdf_raster_configuration", "pdf_raster_runtime_pin", "pdf_raster_process_failed", "pdf_raster_invalid_output");
    private static BsonDocument FieldName() => Choice(PaymentFieldExtractor.Fields.Concat(SupplierDocumentFieldExtractor.Fields).Distinct(StringComparer.Ordinal).ToArray());
    private static BsonDocument SourceKind() => Choice("Payment", "SupplierInvoice");
    private static BsonDocument True() => new() { { "bsonType", "bool" }, { "enum", new BsonArray { true } } };
    private static BsonDocument Type(string type) => new("bsonType", type);
    private static BsonDocument Long(long minimum = 1, long maximum = long.MaxValue - 1) => new() { { "bsonType", "long" }, { "minimum", minimum }, { "maximum", maximum } };
    private static BsonDocument Int(int minimum, int maximum) => new() { { "bsonType", "int" }, { "minimum", minimum }, { "maximum", maximum } };
    private static BsonDocument IntChoice(params int[] values) => new() { { "bsonType", "int" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Text(int maximum) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum }, { "pattern", @"\A[^\x00-\x1f\x7f-\x9f]+\z" } };
    private static BsonDocument Hash() => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Token() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{32}\z" } };
    private static BsonDocument Choice(params string[] values) => new() { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Object(BsonDocument fields) => new() { { "bsonType", "object" }, { "required", new BsonArray(fields.Names) }, { "additionalProperties", false }, { "properties", fields } };
    private static BsonDocument Array(BsonDocument rule, int minimum, int maximum) => new() { { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", maximum }, { "items", rule } };
    private static BsonDocument Nullable(BsonDocument rule) { rule["bsonType"] = new BsonArray { rule["bsonType"], "null" }; if (rule.TryGetValue("enum", out var values)) values.AsBsonArray.Add(BsonNull.Value); return rule; }
    private static BsonDocument Decimal(decimal minimum, decimal maximum) => new() { { "bsonType", "decimal" }, { "minimum", new Decimal128(minimum) }, { "maximum", new Decimal128(maximum) } };
    private static BsonDocument Coordinate(double minimum, double maximum) => new() { { "bsonType", "double" }, { "minimum", minimum }, { "maximum", maximum } };
    private static BsonDocument Fraction() => Decimal(0, 1);
    private static BsonDocument Box() => Array(Fraction(), 4, 4);
    private static BsonDocument Corners() => Array(Object(new() { { "x", Coordinate(-14400, 14400) }, { "y", Coordinate(-14400, 14400) } }), 4, 4);
    private static BsonDocument Flags() => Array(new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[a-z_]{1,64}\z" } }, 0, 16);
    private static BsonDocument Eq(BsonValue a, BsonValue b) => Compare("$eq", a, b);
    private static BsonDocument Compare(string operation, BsonValue a, BsonValue b) => new(operation, new BsonArray { a, b });
    private static BsonDocument And(params BsonDocument[] values) => new("$and", new BsonArray(values));
    private static BsonDocument Or(params BsonDocument[] values) => new("$or", new BsonArray(values));
}
