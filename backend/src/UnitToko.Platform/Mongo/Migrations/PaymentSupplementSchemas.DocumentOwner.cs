using MongoDB.Bson;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Platform.Mongo.Migrations;

internal static partial class PaymentSupplementSchemas
{
    // sourceId/submissionId are the supplement association, never the image
    // payment submission. The latter remains explicitly nested under primary.
    internal static BsonDocument DocumentBinding() => Object(new() {
        { "submissionId", Long() }, { "supplementId", Long() }, { "supplementRevision", Long(2, 2) }, { "primary", Primary() }, { "primarySubmissionHash", Hash() },
        { "reservationId", Long() }, { "pinId", Long() }, { "uploaderId", Long() }, { "versionRowId", Long() },
        { "length", Long(1, UnitToko.Modules.Documents.PdfDocumentExtractionContract.MaximumOriginalBytes) }, { "lifecycleRevision", Long() },
        { "readPolicyId", Long() }, { "readPolicyRevision", Long() }, { "validationPolicyId", Long() }, { "validationPolicyRevision", Long() },
        { "submissionHash", Hash() }, { "pinHash", Hash() }, { "storeId", Hash() }, { "generation", Hash() }, { "validationHash", Hash() }, { "reference", Reference() } });
    internal static BsonDocument DocumentOwnerValidator(string name) => new("$or", new BsonArray {
        new BsonDocument("$and", new BsonArray { PdfDocumentExtractionSchemas.Validator(name), new BsonDocument("sourceKind", new BsonDocument("$ne", PaymentSupplementBson.SourceKind)) }),
        SupplementDocumentBranch(name) });
    internal static BsonDocument SupplementDocumentBranch(string name)
    {
        if (!PdfDocumentExtractionSchemas.Collections.Contains(name)) throw new ArgumentException("Unknown shared PDF owner", nameof(name));
        var baseSchema = PdfDocumentExtractionSchemas.Validator(name)["$and"][0]["$jsonSchema"].DeepClone().AsBsonDocument;
        var properties = baseSchema["properties"].AsBsonDocument;
        properties["sourceKind"] = Choice(PaymentSupplementBson.SourceKind);
        if (properties.Contains("purpose")) properties["purpose"] = name == PdfDocumentExtractionRecords.Reads
            ? Choice("payment-supplement-pdf-ocr-view", "payment-review-supplement-pdf-ocr-view") : Choice("Payment");
        if (properties.Contains("binding")) properties["binding"] = DocumentBinding();
        var terms = new BsonArray { new BsonDocument("$jsonSchema", baseSchema) };
        if (name == PdfDocumentExtractionRecords.Policies) {
            terms.Add(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
                Expr("$lt", "$approvedAt", "$expiresAt"), Expr("$lte", new BsonDocument("$subtract", new BsonArray { "$expiresAt", "$approvedAt" }), 86_400_000L * 90) })));
        } else if (name == PdfDocumentExtractionRecords.Reads) {
            terms.Add(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
                Expr("$eq", "$sourceId", "$submissionId"), Expr("$eq", "$sourceRevision", 2L),
                new BsonDocument("$or", new BsonArray {
                    new BsonDocument("$and", new BsonArray { Expr("$eq", "$state", "succeeded"), Expr("$ne", "$resultId", BsonNull.Value), Expr("$ne", "$resultHash", BsonNull.Value) }),
                    new BsonDocument("$and", new BsonArray { Expr("$ne", "$state", "succeeded"), Expr("$eq", "$resultId", BsonNull.Value), Expr("$eq", "$resultHash", BsonNull.Value) }) }) })));
        } else if (name != PdfDocumentExtractionRecords.Audits) {
            var bindingRules = new BsonArray {
                Expr("$lt", "$createdAt", "$until"), Expr("$lte", new BsonDocument("$subtract", new BsonArray { "$until", "$createdAt" }), 60_000L),
                Expr("$eq", "$binding.submissionId", "$binding.supplementId"), Expr("$eq", "$binding.supplementRevision", 2L),
                Expr("$eq", "$binding.reservationId", "$original.reservationId"), Expr("$eq", "$binding.reference.sha256", "$original.sha256"),
                Expr("$eq", "$binding.storeId", "$original.storeId"), Expr("$eq", "$binding.generation", "$original.generation"),
                Expr("$eq", "$binding.length", "$original.length"), Expr("$ne", "$binding.reference", "$binding.primary.primaryEvidence") };
            if (name == PdfDocumentExtractionRecords.Results) bindingRules.Add(Expr("$eq", "$pageCount", new BsonDocument("$size", "$output.pages")));
            terms.Add(new BsonDocument("$expr", new BsonDocument("$and", bindingRules)));
        }
        if (name != PdfDocumentExtractionRecords.Policies) terms.Add(StockSourceSetupSchemas.DateRange("createdAt"));
        return new("$and", terms);
    }
    internal static BsonDocument ExtendDocumentOwner(string name, BsonDocument installed) =>
        ReplaceReviewed(installed, PdfDocumentExtractionSchemas.Validator(name), DocumentOwnerValidator(name));
    internal static bool SupportsDocumentOwner(string name, BsonDocument installed, BsonDocument required) => InventorySchemas.Contains(installed, required) ||
        InventorySchemas.Contains(installed, DocumentOwnerValidator(name)) && CatalogSchemas.SemanticEquals(required, PdfDocumentExtractionSchemas.Validator(name));
    internal static bool SupportsOutbox(BsonDocument installed, BsonDocument required) => InventorySchemas.Contains(installed, required) ||
        InventorySchemas.Contains(installed, ExtendedOutboxValidator()) && PdfDocumentExtractionSchemas.SupportsOutbox(PdfDocumentExtractionSchemas.ExtendedOutboxValidator(), required);
    private static BsonDocument Expr(string operation, BsonValue left, BsonValue right) => new(operation, new BsonArray { left, right });
    private static BsonDocument ReplaceReviewed(BsonDocument installed, BsonDocument before, BsonDocument after)
    {
        var found = false;
        BsonDocument Replace(BsonDocument value) {
            if (CatalogSchemas.SemanticEquals(value, after)) { found = true; return value.DeepClone().AsBsonDocument; }
            if (CatalogSchemas.SemanticEquals(value, before)) { found = true; return after.DeepClone().AsBsonDocument; }
            var result = value.DeepClone().AsBsonDocument;
            if (result.TryGetValue("$and", out var conjuncts) && conjuncts.IsBsonArray)
                result["$and"] = new BsonArray(conjuncts.AsBsonArray.Select(row => row.IsBsonDocument ? Replace(row.AsBsonDocument) : row.DeepClone()));
            return result;
        }
        var result = Replace(installed); return found ? result : throw new InvalidOperationException("Supplement requires the actual reviewed shared PDF owner");
    }
}
