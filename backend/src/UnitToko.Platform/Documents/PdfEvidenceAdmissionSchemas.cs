using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

// Only the two explicit PDF admission branches are added. Applied image/video schemas are immutable.
internal static class PdfEvidenceAdmissionSchemas
{
    internal static readonly string[] Collections = ["evidenceLineages", "evidenceUploadReservations", "auditEvents", "idempotencyRecords"];
    internal static BsonDocument PreviousValidator(string name) => SupplierDocumentEvidenceSchemas.Validator(name);
    internal static BsonDocument Validator(string name)
    {
        if (!Collections.Contains(name)) throw new ArgumentOutOfRangeException(nameof(name));
        var payment = Translate(PaymentEvidenceSchemas.Validator(name)["$or"][1], true).AsBsonDocument;
        var supplier = Translate(SupplierDocumentEvidenceSchemas.Validator(name)["$or"][1], false).AsBsonDocument;
        return new("$or", new BsonArray { PreviousValidator(name), payment, supplier });
    }
    private static BsonValue Translate(BsonValue value, bool payment)
    {
        if (value.IsString) return value.AsString switch {
            "PaymentImage" when payment => "PaymentPdf",
            "SupplierDocumentImage" when !payment => "SupplierDocumentPdf",
            "evidence.payment-image.reserve" when payment => "evidence.payment-pdf.reserve",
            "evidence.payment-image.complete" when payment => "evidence.payment-pdf.complete",
            "evidence.supplier-document-image.reserve" when !payment => "evidence.supplier-document-pdf.reserve",
            "evidence.supplier-document-image.complete" when !payment => "evidence.supplier-document-pdf.complete",
            _ => value.DeepClone() };
        if (value.IsBsonArray) return new BsonArray(value.AsBsonArray.Select(item => Translate(item, payment)));
        if (value.IsBsonDocument) return new BsonDocument(value.AsBsonDocument.Elements.Select(e => new BsonElement(e.Name, Translate(e.Value, payment))));
        return value.DeepClone();
    }
    internal static BsonDocument Extend(string name, BsonDocument current)
    {
        var before = PreviousValidator(name); var after = Validator(name); var found = false;
        BsonDocument Replace(BsonDocument value) {
            if (CatalogSchemas.SemanticEquals(value, after)) { found = true; return value.DeepClone().AsBsonDocument; }
            if (CatalogSchemas.SemanticEquals(value, before)) { found = true; return after.DeepClone().AsBsonDocument; }
            var copy = value.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                copy["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Replace(term.AsBsonDocument) : term.DeepClone()));
            return copy;
        }
        var result = Replace(current); return found ? result : throw new InvalidOperationException("PDF admission requires the reviewed evidence predecessor: " + name);
    }
    internal static bool Supports(string name, BsonDocument current, BsonDocument required) =>
        Collections.Contains(name) && InventorySchemas.Contains(current, Validator(name)) &&
        (CatalogSchemas.SemanticEquals(required, PreviousValidator(name)) ||
            SupplierDocumentEvidenceSchemas.Supports(name, PreviousValidator(name), required));
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var name in Collections) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition is null || options is null || !SafeDefinition(definition) ||
                !InventorySchemas.Contains(options.GetValue("validator", new BsonDocument()).AsBsonDocument, Validator(name)))
                throw new PersistenceConflictException("evidence_pdf_admission_unavailable", "Explicit PDF evidence admission is not installed");
            PaymentEvidenceMigration.RequireIndexes(name, await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct));
        }
    }
    internal static bool SafeDefinition(BsonDocument definition)
    {
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        return definition.GetValue("type", "") == "collection" && !options.GetValue("capped", false).AsBoolean &&
            !options.Contains("timeseries") && !options.Contains("expireAfterSeconds") && !options.Contains("encryptedFields") &&
            options.GetValue("validationLevel", "") == "strict" && options.GetValue("validationAction", "") == "error" &&
            (!options.TryGetValue("collation", out var collation) || InvoiceStorage.Simple(collation)) &&
            options.TryGetValue("validator", out var validator) && validator.IsBsonDocument;
    }
}
