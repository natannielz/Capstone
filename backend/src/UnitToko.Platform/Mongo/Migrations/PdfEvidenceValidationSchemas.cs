using MongoDB.Bson;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PdfEvidenceValidationSchemas
{
    internal static readonly string[] Collections = [PdfEvidenceValidationRecords.Policies, PdfEvidenceValidationRecords.Results, PdfEvidenceValidationRecords.Audits];
    internal static BsonDocument Key(string name) => name switch {
        PdfEvidenceValidationRecords.Policies => new("consumerId", 1),
        PdfEvidenceValidationRecords.Results => new("outboxId", 1), PdfEvidenceValidationRecords.Audits => new("versionRowId", 1),
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument { { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        if (name == PdfEvidenceValidationRecords.Policies) {
            fields.AddRange(new BsonDocument { { "consumerId", Text(128) }, { "storeId", Hash() }, { "pipelineId", Hash() },
                { "active", Type("bool") }, { "revision", Long() }, { "fence", Long(0) }, { "approvedAt", Type("date") },
                { "expiresAt", Type("date") }, { "hostAcceptanceRef", Text(256) }, { "clamdConfigurationAcceptanceRef", Text(256) } });
            return With(fields, Compare("$gt", "$expiresAt", "$approvedAt"));
        }
        var execution = new BsonDocument { { "outboxId", Type("objectId") }, { "pipelineId", Hash() }, { "consumerId", Text(128) },
            { "policyId", Long() }, { "policyRevision", Long() }, { "claimToken", Token() }, { "claimAttempt", Int(1, 5) },
            { "checkedAt", Type("date") }, { "claimUntil", Type("date") } };
        if (name == PdfEvidenceValidationRecords.Audits) {
            fields.AddRange(new BsonDocument { { "versionRowId", Long() }, { "resultHash", Hash() } }); fields.AddRange(execution);
            return With(fields, Compare("$gt", "$claimUntil", "$checkedAt"));
        }
        if (name != PdfEvidenceValidationRecords.Results) throw new ArgumentOutOfRangeException(nameof(name));
        fields.AddRange(execution);
        fields.AddRange(new BsonDocument { { "source", EvidenceValidationSchemas.Source() }, { "original", VideoValidationSchemas.Original() },
            { "storeId", Hash() }, { "generation", Hash() }, { "length", Long(1, PdfEvidenceValidationContract.MaximumOriginalBytes) },
            { "auditId", Long() }, { "resultHash", Hash() }, { "status", Choice("passed", "rejected") },
            { "scanStatus", Choice("clean", "infected") }, { "decoderStatus", Choice("decoded", "invalid", "unavailable") },
            { "safeFailure", Nullable(Choice("infected_media", "invalid_media", "integrity_mismatch")) },
            { "mime", Nullable(Choice(PdfEvidenceValidationContract.Mime)) }, { "pageCount", Nullable(Int(1, 10)) },
            { "rasterProvenance", Nullable(Provenance()) }, { "pages", Array(Page(), 0, 10) },
            { "scannerEngine", Text(40) }, { "scannerDefinitions", Long() }, { "scannerDefinitionsAt", Type("date") } });
        return With(fields, And(
            Eq("$_id", "$source.versionRowId"), Eq("$source.reservationId", "$original.reservationId"), Eq("$source.attemptId", "$original.attemptId"),
            Eq("$source.contentRevision", "$original.contentRevision"), Eq("$source.sha256", "$original.sha256"),
            Eq("$storeId", "$original.storeId"), Eq("$generation", "$original.generation"), Eq("$length", "$original.length"),
            Compare("$gt", "$claimUntil", "$checkedAt"), Compare("$lte", "$scannerDefinitionsAt", "$checkedAt"),
            Or(And(Eq("$status", "passed"), Eq("$scanStatus", "clean"), Eq("$decoderStatus", "decoded"), Eq("$safeFailure", BsonNull.Value),
                    Eq("$mime", PdfEvidenceValidationContract.Mime), Compare("$gt", "$pageCount", 0),
                    Eq("$pageCount", new BsonDocument("$size", "$pages")), Compare("$ne", "$rasterProvenance", BsonNull.Value)),
               And(Eq("$status", "rejected"), Eq("$mime", BsonNull.Value), Eq("$pageCount", BsonNull.Value), Eq("$rasterProvenance", BsonNull.Value),
                    Eq(new BsonDocument("$size", "$pages"), 0),
                    Or(And(Eq("$safeFailure", "infected_media"), Eq("$scanStatus", "infected"), Eq("$decoderStatus", "unavailable")),
                       And(Eq("$safeFailure", "invalid_media"), Eq("$scanStatus", "clean"), Eq("$decoderStatus", "invalid")),
                       And(Eq("$safeFailure", "integrity_mismatch"), Eq("$decoderStatus", "unavailable")))))));
    }
    private static BsonDocument Provenance() => Object(new() {
        { "pipeline", Choice(PdfRasterProtocol.Pipeline) }, { "wrapper", Choice(PdfRasterProtocol.Wrapper) },
        { "pdfium", Choice(PdfRasterProtocol.Pdfium) }, { "wheelSha256", Choice(PdfRasterProtocol.WheelSha256) },
        { "pillow", Text(32) }, { "runtimeSha256", Hash() } });
    private static BsonDocument Page() => Object(new() {
        { "pageIndex", Int(0, 9) }, { "width", Int(1, 4096) }, { "height", Int(1, 4096) },
        { "length", Long(57, 10 * 1024 * 1024) }, { "sha256", Hash() },
        { "rotationDegrees", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 0, 90, 180, 270 } } } },
        { "canvasSize", Array(Type("double"), 2, 2) }, { "visibleBox", Array(Type("double"), 4, 4) },
        { "pixelCornersInPdfCanvas", Array(Object(new() { { "x", Type("double") }, { "y", Type("double") } }), 4, 4) },
        { "reviewReasons", Array(Choice("human_review_required", "no_visible_marks"), 1, 2) } });
    internal static BsonDocument Receipt() => Object(new() {
        { "evidencePdfValidation", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } },
        { "versionRowId", Long() }, { "resultHash", Hash() }, { "auditId", Long() }, { "pipelineId", Hash() } });
    internal static BsonDocument OutboxValidator() => new("$or", new BsonArray {
        new BsonDocument("kind", new BsonDocument("$ne", PdfEvidenceValidationRecords.Kind)),
        new BsonDocument("$jsonSchema", new BsonDocument { { "bsonType", "object" }, { "properties", new BsonDocument {
            { "processingFence", Long(0) }, { "processingReceipt", Receipt() }, { "leaseOwner", Text(128) }, { "leaseToken", Token() },
            { "leaseUntil", Type("date") }, { "claimStartedAt", Type("date") }, { "claimDeadline", Type("date") },
            { "attemptLimit", Int(1, 5) }, { "deadAt", Type("date") }, { "completedAt", Type("date") }, { "lastFailureCode", Text(100) } } } }) });
    internal static BsonDocument PreviousOutboxValidator() => SupplierDocumentEvidenceSchemas.Validator("outboxEvents");
    internal static BsonDocument ExtendedOutboxValidator() => new("$and", new BsonArray {
        new BsonDocument("$or", new BsonArray {
            PreviousOutboxValidator(), new BsonDocument("$and", new BsonArray {
                new BsonDocument("kind", PdfEvidenceValidationRecords.Kind), Translate(EvidenceSchemas.Validator("outboxEvents")) }) }),
        OutboxValidator() });
    internal static bool SupportsOutbox(BsonDocument current, BsonDocument required) =>
        InventorySchemas.Contains(current, required) ||
        InventorySchemas.Contains(current, ExtendedOutboxValidator()) &&
        (CatalogSchemas.SemanticEquals(required, PreviousOutboxValidator()) ||
            SupplierDocumentEvidenceSchemas.Supports("outboxEvents", PreviousOutboxValidator(), required));
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
        var result = Replace(current); return found ? result : throw new InvalidOperationException("PDF outbox predecessor is not a reviewed definition");
    }
    private static BsonValue Translate(BsonValue value) => value.IsString
        ? value.AsString == "evidence.validation.requested" ? new BsonString(PdfEvidenceValidationRecords.Kind) : value.DeepClone()
        : value.IsBsonArray ? new BsonArray(value.AsBsonArray.Select(Translate))
        : value.IsBsonDocument ? new BsonDocument(value.AsBsonDocument.Elements.Select(e => new BsonElement(e.Name, Translate(e.Value)))) : value.DeepClone();
    private static BsonDocument With(BsonDocument fields, BsonDocument condition) => new("$and", new BsonArray { new BsonDocument("$jsonSchema", Object(fields)), new BsonDocument("$expr", condition) });
    private static BsonDocument Type(string kind) => new("bsonType", kind);
    private static BsonDocument Long(long minimum = 1, long maximum = long.MaxValue) => new() { { "bsonType", "long" }, { "minimum", minimum }, { "maximum", maximum } };
    private static BsonDocument Int(int minimum, int maximum) => new() { { "bsonType", "int" }, { "minimum", minimum }, { "maximum", maximum } };
    private static BsonDocument Text(int maximum) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum }, { "pattern", @"\A[^\x00-\x1f\x7f-\x9f]+\z" } };
    private static BsonDocument Hash() { var value = Text(64); value["pattern"] = @"\A[0-9a-f]{64}\z"; return value; }
    private static BsonDocument Token() { var value = Text(32); value["pattern"] = @"\A[0-9a-f]{32}\z"; return value; }
    private static BsonDocument Choice(params string[] values) => new() { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Nullable(BsonDocument rule) { rule["bsonType"] = new BsonArray { rule["bsonType"], "null" }; if (rule.TryGetValue("enum", out var values)) values.AsBsonArray.Add(BsonNull.Value); return rule; }
    private static BsonDocument Object(BsonDocument fields) => new() { { "bsonType", "object" }, { "required", new BsonArray(fields.Names) }, { "additionalProperties", false }, { "properties", fields } };
    private static BsonDocument Array(BsonDocument item, int minimum, int maximum) => new() { { "bsonType", "array" }, { "items", item }, { "minItems", minimum }, { "maxItems", maximum } };
    private static BsonDocument Eq(BsonValue a, BsonValue b) => Compare("$eq", a, b);
    private static BsonDocument Compare(string op, BsonValue a, BsonValue b) => new(op, new BsonArray { a, b });
    private static BsonDocument And(params BsonDocument[] values) => new("$and", new BsonArray(values));
    private static BsonDocument Or(params BsonDocument[] values) => new("$or", new BsonArray(values));
}
