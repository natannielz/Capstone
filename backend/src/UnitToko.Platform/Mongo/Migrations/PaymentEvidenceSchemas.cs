using MongoDB.Bson;

namespace UnitToko.Platform.Mongo.Migrations;

// Forward extension. The applied v13 shape and catalog rules remain immutable.
internal static class PaymentEvidenceSchemas
{
    internal static readonly string[] Collections = ["evidenceLineages", "evidenceUploadReservations", "auditEvents", "idempotencyRecords"];
    internal static BsonDocument Validator(string name) {
        if (!Collections.Contains(name)) throw new ArgumentOutOfRangeException(nameof(name));
        var payment = Translate(EvidenceSchemas.Validator(name)).AsBsonDocument;
        if (name == "evidenceUploadReservations") payment = new("$and", new BsonArray { payment,
            new BsonDocument("admittedDefinitionId", new BsonDocument("$type", "null")),
            new BsonDocument("admittedDefinitionVersion", new BsonDocument("$type", "null")) });
        return new("$or", new BsonArray { EvidenceSchemas.Validator(name), payment });
    }
    private static BsonValue Translate(BsonValue value, bool enumeration = false) {
        if (value.IsString) return value.AsString switch {
            "CatalogFamily" or "CatalogSku" => "PaymentDeclaration", "CatalogImage" => "PaymentImage",
            EvidenceSchemas.ReserveOperation => "evidence.payment-image.reserve", EvidenceSchemas.CompleteOperation => "evidence.payment-image.complete", _ => value.DeepClone() };
        if (value.IsBsonArray) {
            var items = value.AsBsonArray.Select(item => Translate(item));
            return new BsonArray(enumeration ? items.Distinct() : items);
        }
        if (value.IsBsonDocument) return new BsonDocument(value.AsBsonDocument.Select(pair => new BsonElement(pair.Name, Translate(pair.Value, pair.Name == "enum"))));
        return value.DeepClone();
    }
    internal static BsonDocument Extend(string name, BsonDocument existing) {
        var previous = EvidenceSchemas.Validator(name); var next = Validator(name); var found = false;
        BsonDocument Replace(BsonDocument value) {
            if (CatalogSchemas.SemanticEquals(value, next)) { found = true; return value.DeepClone().AsBsonDocument; }
            if (CatalogSchemas.SemanticEquals(value, previous)) { found = true; return next.DeepClone().AsBsonDocument; }
            var copy = value.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                copy["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Replace(term.AsBsonDocument) : term.DeepClone()));
            return copy;
        }
        var result = Replace(existing);
        return found ? result : throw new InvalidOperationException("Payment evidence migration requires the accepted v13 validator: " + name);
    }
}
