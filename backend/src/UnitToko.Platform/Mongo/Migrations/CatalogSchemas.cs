using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace UnitToko.Platform.Mongo.Migrations;

// Frozen v11 counted catalog shapes. Runtime/preflight additionally establish UUID identity
// and ordinal Unicode order, which generic Mongo binData/string schemas cannot prove.
internal static class CatalogSchemas
{
    internal static readonly string[] Collections = ["catalogCategories", "productFamilies", "skus", "variantDefinitions", "catalogIdentifiers", "catalogIdentifierHistory"];
    private static readonly BsonDateTime Earliest = new(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));
    private static readonly BsonDateTime Latest = new(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));
    private const string Printable = "^(?![\\s\\p{Z}])[^\\x00-\\x1f\\x7f-\\x9f]*(?<![\\s\\p{Z}])\\z";
    private const string CodePattern = "^[A-Z0-9._-]{1,64}\\z";
    private const string UuidPattern = "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\z";

    private static BsonDocument Shape(string collection) => collection switch
    {
        "catalogCategories" => Object(new()
        {
            { "_id", Long() }, { "schemaVersion", Schema() }, { "organizationId", Long() }, { "authoringOwner", OwnerShape() },
            { "code", Code() }, { "name", Text(160) }, { "active", Bool() }, { "revision", Long() }, { "fence", Long(0) }
        }),
        "productFamilies" => Object(new()
        {
            { "_id", Long() }, { "schemaVersion", Schema() }, { "organizationId", Long() }, { "authoringOwner", OwnerShape() },
            { "publicId", Uuid() }, { "publicUuid", new BsonDocument("bsonType", "binData") }, { "categoryId", Long() }, { "categoryCode", Code() },
            { "name", Text(160) }, { "description", Description() }, { "imageAssetIds", Array(Long(), 0) }, { "active", Bool() }, { "revision", Long() }, { "fence", Long(0) }
        }),
        "skus" => Object(new()
        {
            { "_id", Long() }, { "schemaVersion", Schema() }, { "organizationId", Long() }, { "authoringOwner", OwnerShape() }, { "familyId", Long() },
            { "publicId", Uuid() }, { "publicUuid", new BsonDocument("bsonType", "binData") }, { "sku", Code() }, { "physicalItemKey", Text(128) },
            { "active", Bool() }, { "currentDefinitionId", Long() }, { "revision", Long() }, { "predecessorSkuId", NullableLong() },
            { "displayName", Text(227) }, { "catalogListings", Array(Object(new() { { "assortmentId", Long() }, { "ownerScope", OwnerShape(true) } }), 128) }, { "fence", Long(0) }
        }),
        "variantDefinitions" => Object(new()
        {
            { "_id", Long() }, { "schemaVersion", Schema() }, { "skuId", Long() }, { "version", Long() }, { "packageLabel", Text(160) },
            { "packageKind", Choice("loose", "sealed", "other") }, { "baseUnitCode", Text(64) }, { "baseUnitsPerPackage", Integer(1, int.MaxValue) },
            { "attributes", Array(Object(new() { { "key", Text(64) }, { "value", Text(256) } }), 32) }, { "imageAssetIds", Array(Long(), 0) },
            { "minimumOrderPackages", Integer(1, 1000000) }, { "orderIncrementPackages", Integer(1, 1000000) }, { "effectiveAt", Date() }
        }),
        "catalogIdentifiers" => IdentifierShape(),
        "catalogIdentifierHistory" => Object(new()
        {
            { "_id", Long() }, { "schemaVersion", Schema() }, { "identifierId", Long() }, { "organizationId", Long() }, { "skuId", Long() },
            { "assignmentVersion", ExactLong(1) }, { "event", Choice("assigned") }, { "actorId", Long() }, { "createdAt", Date() },
            { "auditId", Long() }, { "assignment", IdentifierShape() }
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(collection))
    };
    private static BsonDocument IdentifierShape() => Object(new()
    {
        { "_id", Long() }, { "schemaVersion", Schema() }, { "organizationId", Long() }, { "skuId", Long() }, { "kind", Choice("skuCode") },
        { "namespace", Choice("internal") }, { "rawValue", Code() }, { "canonicalValue", Code() }, { "isPrimary", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } },
        { "effectiveAt", Date() }, { "retiredAt", new BsonDocument("bsonType", "null") }, { "revision", ExactLong(1) },
        { "provenanceReference", Text(64) }, { "auditId", Long() }
    });
    private static BsonDocument OwnerShape(bool compatible = false)
    {
        var properties = new BsonDocument { { "kind", Choice("fulfillmentShop", "fulfillmentWarehouse") }, { "organizationId", Long() },
            { "shopId", Long() }, { "channelId", Long() }, { "warehouseId", NullableLong() } };
        if (compatible) { properties.Add("branchId", new BsonDocument("bsonType", "null")); properties.Add("divisionId", new BsonDocument("bsonType", "null")); }
        return Object(properties, compatible ? ["warehouseId", "branchId", "divisionId"] : []);
    }

    internal static BsonDocument Validator(string collection)
    {
        var shape = Shape(collection); var sequence = 0;
        var rules = new List<BsonValue> { Structural(shape, "$$ROOT", ref sequence) };
        if (collection is "catalogCategories" or "productFamilies" or "skus")
        {
            rules.Add(OwnerExpression("$$ROOT.authoringOwner", false));
            rules.Add(Equal("$$ROOT.organizationId", "$$ROOT.authoringOwner.organizationId"));
        }
        if (collection == "skus")
        {
            var listings = SafeArray("$$ROOT.catalogListings");
            var ids = Map(listings, "listing", "$$listing.assortmentId");
            rules.Add(And(Unique(ids), Equal(ids, Sort(ids)),
                Or("$$ROOT.active", Equal(Size(listings), 0)),
                Or(Equal("$$ROOT.predecessorSkuId", BsonNull.Value), Compare("$ne", "$$ROOT.predecessorSkuId", "$$ROOT._id")),
                All(Map(listings, "listing", And(OwnerExpression("$$listing.ownerScope", true), Equal("$$listing.ownerScope.organizationId", "$$ROOT.organizationId"))))));
        }
        if (collection == "variantDefinitions") rules.Add(And(Unique(Map(SafeArray("$$ROOT.attributes"), "attribute", "$$attribute.key")), Compare("$gt", "$$ROOT.effectiveAt", Earliest)));
        if (collection == "catalogIdentifiers") rules.Add(IdentifierExpression("$$ROOT"));
        if (collection == "catalogIdentifierHistory") rules.Add(And(IdentifierExpression("$$ROOT.assignment"),
            Equal("$$ROOT.identifierId", "$$ROOT.assignment._id"), Equal("$$ROOT.organizationId", "$$ROOT.assignment.organizationId"),
            Equal("$$ROOT.skuId", "$$ROOT.assignment.skuId"), Equal("$$ROOT.auditId", "$$ROOT.assignment.auditId"), Equal("$$ROOT.createdAt", "$$ROOT.assignment.effectiveAt")));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", shape), new BsonDocument("$expr", And(rules.ToArray())) });
    }

    internal static bool IsValid(string collection, BsonDocument row)
    {
        if (!Collections.Contains(collection, StringComparer.Ordinal) || !Conforms(row, Shape(collection))) return false;
        if (collection is "catalogCategories" or "productFamilies" or "skus")
            if (!ValidOwner(row["authoringOwner"].AsBsonDocument) || row["organizationId"] != row["authoringOwner"]["organizationId"]) return false;
        if (collection is "productFamilies" or "skus")
        {
            var binary = row["publicUuid"].AsBsonBinaryData;
            if (binary.SubType != BsonBinarySubType.UuidStandard || binary.Bytes.Length != 16 ||
                !Guid.TryParseExact(row["publicId"].AsString, "D", out var expected) || binary.ToGuid(GuidRepresentation.Standard) != expected) return false;
        }
        if (collection == "skus")
        {
            if (row["predecessorSkuId"] == row["_id"] || !row["active"].AsBoolean && row["catalogListings"].AsBsonArray.Count != 0) return false;
            long previous = 0;
            foreach (var listing in row["catalogListings"].AsBsonArray)
            {
                var id = listing["assortmentId"].AsInt64;
                if (id <= previous || !ValidOwner(listing["ownerScope"].AsBsonDocument) || listing["ownerScope"]["organizationId"] != row["organizationId"]) return false;
                previous = id;
            }
        }
        if (collection == "variantDefinitions")
        {
            if (row["effectiveAt"].AsBsonDateTime.MillisecondsSinceEpoch <= Earliest.MillisecondsSinceEpoch) return false;
            string? previous = null;
            foreach (var attribute in row["attributes"].AsBsonArray)
            {
                var key = attribute["key"].AsString;
                if (previous is not null && StringComparer.Ordinal.Compare(previous, key) >= 0) return false;
                previous = key;
            }
        }
        if (collection == "catalogIdentifiers") return ValidIdentifier(row);
        if (collection == "catalogIdentifierHistory")
        {
            var assignment = row["assignment"].AsBsonDocument;
            return ValidIdentifier(assignment) && row["identifierId"] == assignment["_id"] && row["organizationId"] == assignment["organizationId"] &&
                row["skuId"] == assignment["skuId"] && row["auditId"] == assignment["auditId"] && row["createdAt"] == assignment["effectiveAt"];
        }
        return true;
    }
    internal static bool SameOwner(BsonDocument left, BsonDocument right) => new[] { "kind", "organizationId", "shopId", "channelId", "warehouseId" }
        .All(field => left.GetValue(field, BsonNull.Value) == right.GetValue(field, BsonNull.Value));
    internal static bool ValidOwner(BsonDocument owner) => owner["kind"] == "fulfillmentShop"
        ? owner.GetValue("warehouseId", BsonNull.Value).IsBsonNull : owner.GetValue("warehouseId", BsonNull.Value).IsInt64 && owner["warehouseId"].AsInt64 > 0;
    internal static bool SemanticEquals(BsonValue left, BsonValue right)
    {
        if (left.BsonType != right.BsonType) return false;
        if (left.IsBsonDocument) return left.AsBsonDocument.ElementCount == right.AsBsonDocument.ElementCount &&
            left.AsBsonDocument.All(field => right.AsBsonDocument.TryGetValue(field.Name, out var other) && SemanticEquals(field.Value, other));
        if (left.IsBsonArray) return left.AsBsonArray.Count == right.AsBsonArray.Count && left.AsBsonArray.Zip(right.AsBsonArray).All(pair => SemanticEquals(pair.First, pair.Second));
        return left.Equals(right);
    }
    private static bool ValidIdentifier(BsonDocument row) => row["rawValue"] == row["canonicalValue"] &&
        row["provenanceReference"] == "catalog.command:" + row["auditId"].AsInt64.ToString(CultureInfo.InvariantCulture) && row["effectiveAt"].AsBsonDateTime.MillisecondsSinceEpoch > Earliest.MillisecondsSinceEpoch;
    internal static bool ValidDate(BsonValue value) => value.IsBsonDateTime && value.AsBsonDateTime.MillisecondsSinceEpoch >= Earliest.MillisecondsSinceEpoch && value.AsBsonDateTime.MillisecondsSinceEpoch <= Latest.MillisecondsSinceEpoch;
    internal static bool CanonicalText(string text, int maximum, bool description = false)
    {
        if (text.Length > maximum || text != text.Trim() || !description && text.Length == 0 ||
            text.Any(character => char.IsControl(character) && !(description && character is '\n' or '\t'))) return false;
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index])) continue;
            if (!char.IsHighSurrogate(text[index]) || index + 1 >= text.Length || !char.IsLowSurrogate(text[++index])) return false;
        }
        return true;
    }
    private static bool Conforms(BsonValue value, BsonDocument rule)
    {
        var type = rule["bsonType"];
        if (type.IsBsonArray)
        {
            if (value.IsBsonNull) return type.AsBsonArray.Contains("null");
            var required = rule.DeepClone().AsBsonDocument; required["bsonType"] = type.AsBsonArray.First(item => item != "null"); return Conforms(value, required);
        }
        if (rule.TryGetValue("enum", out var choices) && !choices.AsBsonArray.Contains(value)) return false;
        switch (type.AsString)
        {
            case "object":
                if (!value.IsBsonDocument) return false;
                var document = value.AsBsonDocument; var properties = rule["properties"].AsBsonDocument;
                return document.Names.Distinct(StringComparer.Ordinal).Count() == document.ElementCount && document.Names.All(properties.Contains) &&
                    rule["required"].AsBsonArray.All(field => document.Contains(field.AsString)) && document.All(field => Conforms(field.Value, properties[field.Name].AsBsonDocument));
            case "array": return value.IsBsonArray && value.AsBsonArray.Count <= rule["maxItems"].AsInt32 &&
                value.AsBsonArray.All(item => Conforms(item, rule["items"].AsBsonDocument));
            case "long": return value.IsInt64 && value.AsInt64 >= rule["minimum"].AsInt64;
            case "int": return value.IsInt32 && (!rule.Contains("minimum") || value.AsInt32 >= rule["minimum"].AsInt32 && value.AsInt32 <= rule["maximum"].AsInt32);
            case "bool": return value.IsBoolean;
            case "null": return value.IsBsonNull;
            case "date": return ValidDate(value);
            case "binData": return value.IsBsonBinaryData && value.AsBsonBinaryData.Bytes.Length == 16;
            case "string":
                if (!value.IsString) return false;
                if (rule.Contains("enum")) return true;
                return CanonicalText(value.AsString, rule["maxLength"].AsInt32, rule["minLength"] == 0) &&
                    Regex.IsMatch(value.AsString, rule["pattern"].AsString, RegexOptions.CultureInvariant);
            default: return false;
        }
    }
    private static BsonValue Structural(BsonDocument shape, string path, ref int sequence)
    {
        var type = shape["bsonType"];
        if (type.IsBsonArray)
        {
            var required = shape.DeepClone().AsBsonDocument; required["bsonType"] = type.AsBsonArray.First(item => item != "null");
            return Or(Equal(Type(path), "null"), Structural(required, path, ref sequence));
        }
        if (type == "date") return And(Compare("$gte", path, Earliest), Compare("$lte", path, Latest));
        if (type == "binData") return Equal(new BsonDocument("$binarySize", new BsonDocument("$cond", new BsonArray { Equal(Type(path), "binData"), path, new BsonBinaryData(new byte[0]) })), 16);
        if (type == "array")
        {
            var variable = "entry" + sequence++;
            return All(Map(SafeArray(path), variable, Structural(shape["items"].AsBsonDocument, "$$" + variable, ref sequence)));
        }
        if (type != "object") return BsonBoolean.True;
        var pairs = new BsonDocument("$objectToArray", new BsonDocument("$cond", new BsonArray { Equal(Type(path), "object"), path, new BsonDocument() }));
        var conditions = new List<BsonValue> { Unique(Map(pairs, "field", "$$field.k")) };
        foreach (var field in shape["properties"].AsBsonDocument)
        {
            var rule = Structural(field.Value.AsBsonDocument, path + "." + field.Name, ref sequence);
            conditions.Add(shape["required"].AsBsonArray.Contains(field.Name) ? rule : Or(Equal(Type(path + "." + field.Name), "missing"), rule));
        }
        return And(conditions.ToArray());
    }
    private static BsonValue OwnerExpression(string path, bool compatible) => new BsonDocument("$cond", new BsonArray
    {
        Equal(path + ".kind", "fulfillmentShop"), compatible ? Or(Equal(Type(path + ".warehouseId"), "null"), Equal(Type(path + ".warehouseId"), "missing")) : Equal(Type(path + ".warehouseId"), "null"),
        Equal(Type(path + ".warehouseId"), "long")
    });
    private static BsonValue IdentifierExpression(string path) => And(Compare("$gt", path + ".effectiveAt", Earliest), Equal(path + ".rawValue", path + ".canonicalValue"),
        Equal(path + ".provenanceReference", new BsonDocument("$concat", new BsonArray { "catalog.command:", new BsonDocument("$convert", new BsonDocument { { "input", path + ".auditId" }, { "to", "string" }, { "onError", "" }, { "onNull", "" } }) })));
    private static BsonDocument Object(BsonDocument properties, params string[] optional) => new() { { "bsonType", "object" }, { "required", new BsonArray(properties.Names.Where(name => !optional.Contains(name))) }, { "additionalProperties", false }, { "properties", properties } };
    private static BsonDocument Array(BsonDocument item, int maximum) => new() { { "bsonType", "array" }, { "maxItems", maximum }, { "items", item } };
    private static BsonDocument Long(long minimum = 1) => new() { { "bsonType", "long" }, { "minimum", minimum } };
    private static BsonDocument ExactLong(long value) { var shape = Long(); shape["enum"] = new BsonArray { value }; return shape; }
    private static BsonDocument NullableLong() { var shape = Long(); shape["bsonType"] = new BsonArray { "long", "null" }; return shape; }
    private static BsonDocument Integer(int min, int max) => new() { { "bsonType", "int" }, { "minimum", min }, { "maximum", max } };
    private static BsonDocument Schema() => new() { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } };
    private static BsonDocument Bool() => new("bsonType", "bool");
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Choice(params string[] values) => new() { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Text(int maximum, string pattern = Printable, int minimum = 1) => new() { { "bsonType", "string" }, { "minLength", minimum }, { "maxLength", maximum }, { "pattern", pattern } };
    private static BsonDocument Code() => Text(64, CodePattern);
    private static BsonDocument Uuid() => Text(36, UuidPattern, 36);
    private static BsonDocument Description() => Text(4000, "^(?:|(?=[^\\s\\p{Z}])[^\\x00-\\x08\\x0b-\\x1f\\x7f-\\x9f]*(?<![\\s\\p{Z}]))\\z", 0);
    private static BsonDocument Type(string path) => new("$type", path);
    private static BsonValue SafeArray(string path) => new BsonDocument("$cond", new BsonArray { new BsonDocument("$isArray", path), path, new BsonArray() });
    private static BsonDocument Map(BsonValue input, string variable, BsonValue value) => new("$map", new BsonDocument { { "input", input }, { "as", variable }, { "in", value } });
    private static BsonDocument All(BsonValue value) => new("$allElementsTrue", new BsonArray { value });
    private static BsonDocument Size(BsonValue value) => new("$size", value);
    private static BsonDocument Sort(BsonValue value) => new("$sortArray", new BsonDocument { { "input", value }, { "sortBy", 1 } });
    private static BsonDocument Unique(BsonValue values) => Equal(Size(values), Size(new BsonDocument("$setUnion", new BsonArray { values, new BsonArray() })));
    private static BsonDocument Equal(BsonValue left, BsonValue right) => Compare("$eq", left, right);
    private static BsonDocument Compare(string op, BsonValue left, BsonValue right) => new(op, new BsonArray { left, right });
    private static BsonDocument And(params BsonValue[] values) => new("$and", new BsonArray(values));
    private static BsonDocument Or(params BsonValue[] values) => new("$or", new BsonArray(values));
}
