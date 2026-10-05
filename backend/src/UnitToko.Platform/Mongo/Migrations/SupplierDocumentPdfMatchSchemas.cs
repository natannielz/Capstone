using MongoDB.Bson;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Procurement;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class SupplierDocumentPdfMatchSchemas
{
    internal static BsonDocument Observation() => S.Object(new() {
        { "field", S.Choice(SupplierDocumentFieldExtractor.Fields.ToArray()) },
        { "candidate", S.Object(new() {
            { "resultId", S.Number(1, long.MaxValue - 2) },
            { "resultHash", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } } },
            { "evidence", UnitToko.Platform.Mongo.Migrations.PaymentSupplementSchemas.Reference() },
            { "pageIndex", new BsonDocument { { "bsonType", "int" }, { "minimum", 0 }, { "maximum", PdfDocumentExtractionContract.MaximumPages - 1 } } },
            { "field", S.Choice(SupplierDocumentFieldExtractor.Fields.ToArray()) } }) },
        { "originalReviewed", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } }
    });
    internal static BsonDocument AnnotatedValidator(bool quality) {
        var previous = ProcurementQualityValuationSchemas.PrePdfDocumentValidator(B.Matches);
        var validator = previous["$or"][quality ? 1 : 0].DeepClone().AsBsonDocument;
        var schema = validator["$and"][0]["$jsonSchema"];
        schema["properties"]["schemaVersion"]["enum"] = new BsonArray { quality ? 4 : 3 };
        var input = schema["properties"]["input"];
        input["required"].AsBsonArray.Add("pdfObservations");
        input["properties"]["ocrResultId"] = new BsonDocument("bsonType", "null");
        input["properties"].AsBsonDocument.Add("pdfObservations", new BsonDocument {
            { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", SupplierDocumentFieldExtractor.Fields.Count }, { "items", Observation() } });
        var fields = new BsonDocument("$map", new BsonDocument { { "input", "$$observations" }, { "as", "value" }, { "in", "$$value.field" } });
        // The reviewed old branches omit this field. Guard expression evaluation
        // so Mongo accepts retained v1/v2 rows under the actual union.
        validator["$and"].AsBsonArray.Add(new BsonDocument("$expr", new BsonDocument("$let", new BsonDocument {
            { "vars", new BsonDocument("observations", new BsonDocument("$cond", new BsonArray {
                new BsonDocument("$isArray", "$input.pdfObservations"), "$input.pdfObservations", new BsonArray() })) },
            { "in", new BsonDocument("$and", new BsonArray {
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$size", "$$observations"),
                    new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { fields, new BsonArray() })) }),
                new BsonDocument("$allElementsTrue", new BsonArray { new BsonDocument("$map", new BsonDocument {
                    { "input", "$$observations" }, { "as", "value" },
                    { "in", new BsonDocument("$eq", new BsonArray { "$$value.field", "$$value.candidate.field" }) } }) })
            }) }
        })));
        return validator;
    }
    internal static BsonDocument Validator(string name) {
        var previous = ProcurementQualityValuationSchemas.PrePdfDocumentValidator(name);
        if (name != B.Matches) return previous;
        return new("$or", new BsonArray { previous["$or"][0].DeepClone(), previous["$or"][1].DeepClone(),
            AnnotatedValidator(false), AnnotatedValidator(true) });
    }
    // Only exact known M79->M89 replacement through conjuncts. Never infer or
    // expand an arbitrary operator OR; every extra operator guard is retained.
    internal static BsonDocument Extend(BsonDocument current) {
        var successor = Validator(B.Matches);
        if (InventorySchemas.Contains(current, successor)) return current.DeepClone().AsBsonDocument;
        var previous = ProcurementQualityValuationSchemas.PrePdfDocumentValidator(B.Matches); var replaced = false;
        BsonDocument Visit(BsonDocument value) {
            if (CatalogSchemas.SemanticEquals(value, previous)) { replaced = true; return successor.DeepClone().AsBsonDocument; }
            var copy = value.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                for (var index = 0; index < terms.AsBsonArray.Count; index++)
                    if (terms[index].IsBsonDocument) terms.AsBsonArray[index] = Visit(terms[index].AsBsonDocument);
            return copy;
        }
        var result = Visit(current);
        if (!replaced) throw new InvalidOperationException("Supplier PDF matching needs the exact retained M79 match predecessor");
        return result;
    }
}
