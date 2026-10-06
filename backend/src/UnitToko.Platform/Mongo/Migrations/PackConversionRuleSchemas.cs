using MongoDB.Bson;
using B = UnitToko.Platform.Inventory.PackConversionRuleBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PackConversionRuleSchemas
{
    internal static readonly string[] Collections = [B.Proposals, B.Decisions, B.Rules];
    private static BsonDocument Hash() => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Long() => S.Number(1, long.MaxValue - 2);
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    internal static BsonDocument Input() => S.Object(new() {
        { "location", StockCountSchemas.Validator("stockCountObservations")["$and"][0]["$jsonSchema"]["properties"]["location"].DeepClone() },
        { "sourceSkuId", Long() }, { "sourceSkuRevision", Long() }, { "sourceDefinitionId", Long() }, { "destinationSkuId", Long() },
        { "destinationSkuRevision", Long() }, { "destinationDefinitionId", Long() },
        { "sourcePackagesPerStep", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } } },
        { "destinationPackagesPerStep", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } } } });
    private static BsonDocument Catalogue() {
        BsonDocument Shape(string table) {
            var validator = CatalogSchemas.Validator(table);
            // The unchanged reviewed catalog templates own the actual UUID,
            // owner, definition and attribute shapes, not a generic freeform object.
            return validator["$and"][0]["$jsonSchema"].DeepClone().AsBsonDocument;
        }
        var package = S.Object(new() { { "sku", Shape("skus") }, { "family", Shape("productFamilies") },
            { "category", Shape("catalogCategories") }, { "definition", Shape("variantDefinitions") } });
        return S.Object(new() { { "source", package }, { "destination", package.DeepClone() } });
    }
    private static BsonDocument People(int length) => new() { { "bsonType", "array" }, { "minItems", length }, { "maxItems", length },
        { "items", S.Object(new() { { "userId", Long() }, { "personId", Long() }, { "verification", S.Choice("Verified") }, { "version", Long() } }) } };
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        if (name == B.Proposals) {
            fields.Add("kind", S.Choice("pack.conversion.rule.proposal")); fields.Add("revision", S.Number(1, 1)); fields.Add("input", Input());
            fields.Add("catalog", Catalogue()); fields.Add("binding", Hash()); fields.Add("people", People(1)); fields.Add("operation", S.Choice(B.Propose));
        } else if (name == B.Decisions) {
            fields.Add("kind", S.Choice("pack.conversion.rule.decision")); fields.Add("proposalId", Long()); fields.Add("proposalHash", Hash());
            fields.Add("decision", S.Choice("approve", "reject")); fields.Add("ruleId", Nullable(Long())); fields.Add("binding", Hash()); fields.Add("people", People(2));
            fields.Add("operation", S.Choice(B.Decide));
        } else if (name == B.Rules) {
            fields.Add("kind", S.Choice("pack.conversion.rule")); fields.Add("version", S.Number(1, 1)); fields.Add("proposalId", Long()); fields.Add("decisionId", Long());
            fields.Add("proposalHash", Hash()); fields.Add("decisionHash", Hash()); fields.Add("input", Input()); fields.Add("catalog", Catalogue());
            fields.Add("recordedAt", Date()); fields.Add("physicalConversionAvailable", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { false } } });
            fields.Add("snapshotHash", Hash()); return Guard(S.Object(fields));
        } else throw new ArgumentOutOfRangeException(nameof(name));
        fields.Add("actorId", Long()); fields.Add("actorGrantVersion", Long()); fields.Add("requestKey", StockCountSchemas.Validator("stockCountObservations")["$and"][0]["$jsonSchema"]["properties"]["requestKey"].DeepClone());
        fields.Add("bodyHash", Hash()); fields.Add("reason", S.Text(1000)); fields.Add("correlationId", S.Text(128)); fields.Add("auditId", Long());
        fields.Add("recordedAt", Date()); fields.Add("snapshotHash", Hash()); return Guard(S.Object(fields));
    }
    private static BsonDocument Guard(BsonDocument shape) => new("$and", new BsonArray {
        new BsonDocument("$jsonSchema", shape), new BsonDocument("$expr", Structure(shape, "$$ROOT", 0)) });
    private static BsonValue Structure(BsonDocument shape, string path, int depth) {
        if (shape.TryGetValue("anyOf", out var choices)) return new BsonDocument("$or", new BsonArray(
            choices.AsBsonArray.Select(choice => Structure(choice.AsBsonDocument, path, depth))));
        if (!shape.TryGetValue("bsonType", out var type)) return BsonBoolean.True;
        if (type.IsBsonArray) return new BsonDocument("$or", new BsonArray(type.AsBsonArray.Select(item => {
            var branch = shape.DeepClone().AsBsonDocument; branch["bsonType"] = item; return Structure(branch, path, depth); })));
        if (type == "date") return new BsonDocument("$and", new BsonArray {
            new BsonDocument("$gte", new BsonArray { path, new BsonDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)) }),
            new BsonDocument("$lte", new BsonArray { path, new BsonDateTime(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)) }) });
        if (type == "array" && shape.TryGetValue("items", out var itemShape) && itemShape.IsBsonDocument) {
            var variable = "item" + depth;
            return new BsonDocument("$allElementsTrue", new BsonArray { new BsonDocument("$map", new BsonDocument {
                { "input", new BsonDocument("$cond", new BsonArray { new BsonDocument("$isArray", path), path, new BsonArray() }) },
                { "as", variable }, { "in", Structure(itemShape.AsBsonDocument, "$$" + variable, depth + 1) } }) });
        }
        if (type != "object") return BsonBoolean.True;
        var pairs = new BsonDocument("$objectToArray", new BsonDocument("$cond", new BsonArray {
            new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", path), "object" }), path, new BsonDocument() }));
        var names = new BsonDocument("$map", new BsonDocument { { "input", pairs }, { "as", "field" }, { "in", "$$field.k" } });
        var rules = new BsonArray { new BsonDocument("$eq", new BsonArray {
            new BsonDocument("$size", names), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { names, new BsonArray() })) }) };
        if (shape.TryGetValue("properties", out var properties)) foreach (var field in properties.AsBsonDocument)
            rules.Add(Structure(field.Value.AsBsonDocument, path + "." + field.Name, depth + 1));
        return new BsonDocument("$and", rules);
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        B.Proposals => [("ux_pack_rule_proposal_key", new() { { "actorId", 1 }, { "requestKey", 1 } }, true), ("ix_pack_rule_proposal_location", new() { { "input.location.organizationId", 1 }, { "input.location.shopId", 1 }, { "_id", -1 } }, false)],
        B.Decisions => [("ux_pack_rule_proposal_decision", new("proposalId", 1), true), ("ux_pack_rule_decision_key", new() { { "actorId", 1 }, { "requestKey", 1 } }, true)],
        B.Rules => [("ux_pack_rule_proposal", new("proposalId", 1), true), ("ux_pack_rule_decision", new("decisionId", 1), true), ("ix_pack_rule_location", new() { { "input.location.organizationId", 1 }, { "input.location.shopId", 1 }, { "_id", -1 } }, false)],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
}
