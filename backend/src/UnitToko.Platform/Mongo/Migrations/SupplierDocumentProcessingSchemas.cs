using MongoDB.Bson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class SupplierDocumentProcessingSchemas
{
    internal static readonly string[] Collections = ["supplierDocumentProcessingPolicies", "supplierDocumentProcessingReadAudits", "supplierDocumentExtractionResults", "supplierDocumentExtractionAudits", "supplierDocumentProcessingFailures", "supplierDocumentExtractionReadAudits"];
    internal static BsonDocument Key(string name) => name switch {
        "supplierDocumentProcessingPolicies" => new("consumerId", 1),
        "supplierDocumentExtractionResults" => new() { { "binding.reference.id", 1 }, { "binding.reference.version", 1 }, { "purpose", 1 }, { "provenance.modelSha256", 1 }, { "provenance.preprocessingVersion", 1 } },
        "supplierDocumentExtractionAudits" => new("resultId", 1),
        "supplierDocumentExtractionReadAudits" => new("_id", 1),
        "supplierDocumentProcessingReadAudits" or "supplierDocumentProcessingFailures" => new() { { "outboxId", 1 }, { "claimToken", 1 } },
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        switch (name) {
            case "supplierDocumentProcessingPolicies":
                fields.AddRange(new BsonDocument { { "consumerId", S.Text(128) }, { "storeId", Hash() }, { "pipelineId", Hash() }, { "active", Type("bool") },
                    { "revision", S.Number(1) }, { "fence", S.Number(0) }, { "approvedAt", Type("date") }, { "expiresAt", Type("date") },
                    { "hostAcceptanceRef", S.Text(256) }, { "modelSelectionRef", S.Text(256) } }); break;
            case "supplierDocumentProcessingReadAudits":
                Common(fields); fields.Add("binding", Binding()).Add("recordHash", Hash()); break;
            case "supplierDocumentExtractionResults":
                Common(fields); fields.AddRange(new BsonDocument { { "binding", Binding() }, { "readAuditId", S.Number(1) }, { "auditId", S.Number(1) },
                    { "purpose", S.Choice("SupplierInvoice") }, { "provenance", Provenance() }, { "output", Output() }, { "resultHash", Hash() } }); break;
            case "supplierDocumentExtractionAudits":
                fields.AddRange(new BsonDocument { { "resultId", S.Number(1) }, { "readAuditId", S.Number(1) }, { "outboxId", Type("objectId") }, { "resultHash", Hash() },
                    { "bindingHash", Hash() }, { "consumerId", S.Text(128) }, { "pipelineId", Hash() }, { "createdAt", Type("date") } }); break;
            case "supplierDocumentProcessingFailures":
                Common(fields); fields.AddRange(new BsonDocument { { "readAuditId", S.Number(1) }, { "bindingHash", Hash() }, { "recordHash", Hash() },
                    { "safeFailure", S.Choice("ocr_unavailable", "ocr_timeout", "ocr_invalid_output", "ocr_integrity_mismatch", "ocr_invalid_media", "ocr_original_unavailable") } }); break;
            case "supplierDocumentExtractionReadAudits":
                fields.AddRange(new BsonDocument { { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "outboxId", Type("objectId") },
                    { "submissionId", S.Number(1) }, { "draftId", S.Number(1) }, { "reference", PaymentSubmissionSchemas.Reference() }, { "resultId", Nullable(S.Number(1)) },
                    { "resultHash", Nullable(Hash()) }, { "state", S.Choice("queued", "running", "retry", "dead", "succeeded") },
                    { "purpose", S.Choice("supplier-document-ocr-view") }, { "correlationId", S.Text(128) }, { "createdAt", Type("date") } }); break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) };
        if (name == "supplierDocumentProcessingPolicies") terms.Add(new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$expiresAt", "$approvedAt" })));
        else { terms.Add(S.DateRange("createdAt")); if (name is not ("supplierDocumentExtractionAudits" or "supplierDocumentExtractionReadAudits")) terms.Add(new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$until", "$createdAt" }))); }
        return new("$and", terms);
    }
    private static void Common(BsonDocument fields) => fields.AddRange(new BsonDocument { { "outboxId", Type("objectId") }, { "consumerId", S.Text(128) }, { "pipelineId", Hash() },
        { "policyId", S.Number(1) }, { "policyRevision", S.Number(1) }, { "claimToken", Token() }, { "claimAttempt", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000 } } },
        { "createdAt", Type("date") }, { "until", Type("date") } });
    internal static BsonDocument Binding() {
        var fields = new BsonDocument();
        foreach (var key in new[] { "submissionId", "draftId", "draftRevision", "reservationId", "pinId", "versionRowId", "length", "lifecycleRevision", "readPolicyId", "readPolicyRevision", "validationPolicyId", "validationPolicyRevision" }) fields.Add(key, S.Number(1));
        foreach (var key in new[] { "submissionHash", "draftHash", "pinHash", "storeId", "generation", "validationHash" }) fields.Add(key, Hash());
        fields.Add("reference", PaymentSubmissionSchemas.Reference()); return S.Object(fields);
    }
    internal static BsonDocument Provenance() => S.Object(new() { { "engineVersion", S.Text(128) }, { "modelVersion", S.Text(128) }, { "modelSha256", Hash() }, { "runtimeSha256", Hash() }, { "preprocessingVersion", S.Text(128) } });
    internal static BsonDocument Output() => S.Object(new() {
        { "lines", Array(S.Object(new() { { "text", S.Text(512) }, { "confidence", Fraction() }, { "box", Box() } }), 0, 128) },
        { "fields", Array(S.Object(new() { { "field", S.Choice("document_reference", "document_date", "supplier_name", "total_amount", "currency", "purchase_order_reference", "delivery_reference", "source_sku") },
            { "value", Nullable(S.Text(256)) }, { "confidence", Nullable(Fraction()) }, { "box", Nullable(Box()) }, { "flags", Flags() } }), 8, 8) }, { "qualityFlags", Flags() } });
    private static BsonDocument Box() => Array(Fraction(), 4, 4);
    private static BsonDocument Fraction() => new() { { "bsonType", "decimal" }, { "minimum", Decimal128.Zero }, { "maximum", new Decimal128(1) } };
    private static BsonDocument Flags() => Array(new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[a-z_]{1,64}\z" } }, 0, 16);
    private static BsonDocument Array(BsonDocument rule, int min, int max) => new() { { "bsonType", "array" }, { "minItems", min }, { "maxItems", max }, { "items", rule } };
    private static BsonDocument Nullable(BsonDocument rule) { rule["bsonType"] = new BsonArray { rule["bsonType"], "null" }; return rule; }
    private static BsonDocument Hash() => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Type(string type) => new("bsonType", type);
    private static BsonDocument Token() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{32}\z" } };
    internal static BsonDocument Receipt() => S.Object(new() { { "supplierDocumentExtraction", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } }, { "resultId", S.Number(1) }, { "resultHash", Hash() }, { "readAuditId", S.Number(1) }, { "auditId", S.Number(1) }, { "bindingHash", Hash() }, { "pipelineId", Hash() } });
    internal static BsonDocument OutboxValidator() => new("$or", new BsonArray { new BsonDocument("kind", new BsonDocument("$ne", "supplierdocument.ocr.requested")),
        new BsonDocument("$and", new BsonArray {
            new BsonDocument("$jsonSchema", new BsonDocument { { "bsonType", "object" }, { "required", new BsonArray { "payload" } }, { "properties", new BsonDocument {
                { "payload", S.Object(new() { { "submissionId", S.Number(1) }, { "draftId", S.Number(1) }, { "draftRevision", S.Number(1) }, { "evidence", PaymentSubmissionSchemas.Reference() }, { "pinId", S.Number(1) }, { "submissionHash", Hash() }, { "draftHash", Hash() } }) },
                { "processingFence", S.Number(0) }, { "processingReceipt", Receipt() } } } }),
            new BsonDocument("$or", new BsonArray { new BsonDocument("status", new BsonDocument("$ne", "completed")), new BsonDocument("processingReceipt", new BsonDocument("$exists", true)) }) }) });
}
