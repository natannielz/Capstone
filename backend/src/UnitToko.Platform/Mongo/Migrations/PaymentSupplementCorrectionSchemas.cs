using MongoDB.Bson;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PaymentSupplementCorrectionSchemas
{
    internal static BsonDocument Observation() => S.Object(new() {
        { "field", S.Choice(PaymentFieldExtractor.Fields.ToArray()) }, { "supplementId", S.Number(1, long.MaxValue - 2) },
        { "supplementRevision", S.Number(2, 2) }, { "candidate", S.Object(new() {
            { "resultId", S.Number(1, long.MaxValue - 2) }, { "resultHash", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } } },
            { "evidence", PaymentSupplementSchemas.Reference() }, { "pageIndex", new BsonDocument {
                { "bsonType", "int" }, { "minimum", 0 }, { "maximum", PdfDocumentExtractionContract.MaximumPages - 1 } } },
            { "field", S.Choice(PaymentFieldExtractor.Fields.ToArray()) } }) },
        { "originalReviewed", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } } });
    internal static BsonDocument AnnotatedValidator()
    {
        var validator = PaymentCorrectionSchemas.Validator(PaymentCorrectionBson.Versions).DeepClone().AsBsonDocument;
        var schema = validator["$and"][0]["$jsonSchema"];
        schema["properties"]["schemaVersion"]["enum"] = new BsonArray { 2 };
        schema["required"].AsBsonArray.Add("supplementaryObservations");
        schema["properties"].AsBsonDocument.Add("supplementaryObservations", new BsonDocument {
            { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", PaymentFieldExtractor.Fields.Count }, { "items", Observation() } });
        var fieldNames = new BsonDocument("$map", new BsonDocument { { "input", "$$observations" }, { "as", "item" }, { "in", "$$item.field" } });
        validator["$and"].AsBsonArray.Add(new BsonDocument("$expr", new BsonDocument("$let", new BsonDocument {
            { "vars", new BsonDocument("observations", new BsonDocument("$cond", new BsonArray {
                new BsonDocument("$isArray", "$supplementaryObservations"), "$supplementaryObservations", new BsonArray() })) },
            { "in", new BsonDocument("$and", new BsonArray {
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$size", "$$observations"),
                    new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { fieldNames, new BsonArray() })) }),
                new BsonDocument("$allElementsTrue", new BsonArray { new BsonDocument("$map", new BsonDocument {
                    { "input", "$$observations" }, { "as", "observation" },
                    { "in", new BsonDocument("$eq", new BsonArray { "$$observation.field", "$$observation.candidate.field" }) } }) })
            }) }
        })));
        return validator;
    }
    internal static BsonDocument Validator(string name) => name == PaymentCorrectionBson.Versions
        ? new("$or", new BsonArray { PaymentCorrectionSchemas.Validator(name), AnnotatedValidator() })
        : name == PaymentCorrectionBson.Heads ? PaymentCorrectionSchemas.Validator(name) : throw new ArgumentOutOfRangeException(nameof(name));
    // Only replace the exact reviewed predecessor, traversing conjuncts so every
    // operator constraint remains in force. Arbitrary disjunctions are never adopted.
    internal static BsonDocument Extend(BsonDocument current)
    {
        var successor = Validator(PaymentCorrectionBson.Versions);
        if (InventorySchemas.Contains(current, successor)) return current.DeepClone().AsBsonDocument;
        var previous = PaymentCorrectionSchemas.Validator(PaymentCorrectionBson.Versions); var replaced = false;
        BsonDocument Visit(BsonDocument value) {
            if (CatalogSchemas.SemanticEquals(value, previous)) { replaced = true; return successor.DeepClone().AsBsonDocument; }
            var copy = value.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                for (var index = 0; index < terms.AsBsonArray.Count; index++)
                    if (terms[index].IsBsonDocument) terms.AsBsonArray[index] = Visit(terms[index].AsBsonDocument);
            return copy;
        }
        var result = Visit(current);
        if (!replaced) throw new InvalidOperationException("Payment supplementary correction needs the exact retained predecessor");
        return result;
    }
}
