using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

// Prospective M87 admission successor. Only the named exact reviewed predecessor
// becomes this union; surrounding operator conjuncts remain outside the union.
internal static class PaymentSupplementEvidenceSchemas
{
    internal static readonly string[] Collections = [.. PdfEvidenceAdmissionSchemas.Collections, "evidenceReadLeases", "evidencePins"];
    // M80 owns the full catalog/financial lease union. M85 extends only its four
    // admission owners; M86 retains that M80 lease and the M70 pin unchanged.
    internal static BsonDocument PreviousValidator(string name) => name == CatalogEvidenceReadSchemas.Leases
        ? CatalogEvidenceReadSchemas.Validator(name)
        : PdfEvidenceAdmissionSchemas.Collections.Contains(name)
            ? PdfEvidenceAdmissionSchemas.Validator(name) : SupplierDocumentEvidenceSchemas.Validator(name);
    internal static BsonDocument Validator(string name) => Collections.Contains(name)
        ? new("$or", new BsonArray { PreviousValidator(name), SupplementBranch(name) })
        : SupplierDocumentEvidenceSchemas.Validator(name);
    internal static BsonDocument SupplementBranch(string name)
    {
        var original = PdfEvidenceAdmissionSchemas.Collections.Contains(name)
            ? PdfEvidenceAdmissionSchemas.Validator(name)["$or"][2] : SupplierDocumentEvidenceSchemas.Validator(name)["$or"][1];
        var branch = Translate(original).AsBsonDocument;
        if (name == "evidenceUploadReservations") branch = new("$and", new BsonArray { branch,
            new BsonDocument("admittedTargetRevision", 1L) });
        if (name == "evidenceReadLeases") branch = new("$and", new BsonArray { branch,
            new BsonDocument("target.revision", new BsonDocument("$in", new BsonArray { 1L, 2L })) });
        if (name == "evidencePins") {
            var shape = branch["$and"][0]["$jsonSchema"].AsBsonDocument;
            var properties = shape["properties"].AsBsonDocument;
            properties["supplementRevision"] = PaymentSupplementSchemas.Long(2, 2);
            properties.Add("primary", PaymentSupplementSchemas.Primary());
            properties.Add("primarySubmissionHash", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } });
            shape["required"] = new BsonArray(properties.Names);
            branch["$and"].AsBsonArray.Add(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
                new BsonDocument("$eq", new BsonArray { "$associationId", "$supplementId" }),
                new BsonDocument("$ne", new BsonArray { "$reference", "$primary.primaryEvidence" }) })));
        }
        return branch;
    }
    private static BsonValue Translate(BsonValue value)
    {
        string Name(string text) => text switch {
            "SupplierDocument" => "PaymentSupplement", "SupplierDocumentPdf" => "PaymentSupplementPdf",
            "supplierDocumentDraftId" => "supplementId", "supplierDocumentDraftRevision" => "supplementRevision",
            "evidence.supplier-document-pdf.reserve" => "evidence.payment-supplement-pdf.reserve",
            "evidence.supplier-document-pdf.complete" => "evidence.payment-supplement-pdf.complete", _ => text };
        if (value.IsString) return new BsonString(Name(value.AsString));
        if (value.IsBsonArray) return new BsonArray(value.AsBsonArray.Select(Translate));
        if (value.IsBsonDocument) return new BsonDocument(value.AsBsonDocument.Elements.Select(element => new BsonElement(Name(element.Name), Translate(element.Value))));
        return value.DeepClone();
    }
    internal static BsonDocument Extend(string name, BsonDocument current)
    {
        var before = PreviousValidator(name); var after = Validator(name); var recognized = false;
        BsonDocument Replace(BsonDocument row) {
            if (CatalogSchemas.SemanticEquals(row, after)) { recognized = true; return row.DeepClone().AsBsonDocument; }
            if (CatalogSchemas.SemanticEquals(row, before)) { recognized = true; return after.DeepClone().AsBsonDocument; }
            var copy = row.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                copy["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Replace(term.AsBsonDocument) : term.DeepClone()));
            return copy;
        }
        var result = Replace(current);
        return recognized ? result : throw new InvalidOperationException("Payment supplement admission needs the exact retained predecessor: " + name);
    }
    internal static bool Supports(string name, BsonDocument current, BsonDocument required) =>
        Collections.Contains(name) && InventorySchemas.Contains(current, Validator(name)) &&
        (CatalogSchemas.SemanticEquals(required, Validator(name)) || CatalogSchemas.SemanticEquals(required, PreviousValidator(name)) ||
            SupplierDocumentEvidenceSchemas.Supports(name, PreviousValidator(name), required));
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var name in Collections) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (definition is null || !PdfEvidenceAdmissionSchemas.SafeDefinition(definition) ||
                !InventorySchemas.Contains(definition["options"]["validator"].AsBsonDocument, Validator(name)))
                throw new PersistenceConflictException("payment_supplement_evidence_unavailable", "Penyimpanan privat PDF lampiran belum siap.");
            if (name is "evidencePins" or "evidenceReadLeases")
                await InventoryRepository.RequireStorageAsync(database, name, definition["options"]["validator"].AsBsonDocument,
                    name == "evidencePins" ? PaymentSubmissionSchemas.Key(name) : EvidenceAccessSchemas.Key(name), ct);
            else PaymentEvidenceMigration.RequireIndexes(name, await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct));
        }
    }
}
