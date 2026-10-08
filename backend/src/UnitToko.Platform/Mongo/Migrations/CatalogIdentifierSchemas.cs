using MongoDB.Bson;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Forward-only extension. Legacy identifier and history branches are the exact v11 validators.</summary>
internal static class CatalogIdentifierSchemas
{
    internal static BsonDocument Validator(string collection)
    {
        if (collection is not (CatalogIdentifierRecords.Collection or CatalogIdentifierRecords.History)) throw new ArgumentOutOfRangeException(nameof(collection));
        var legacy = CatalogSchemas.Validator(collection);
        var current = new BsonDocument("$and", new BsonArray
        {
            new BsonDocument("$jsonSchema", collection == CatalogIdentifierRecords.Collection ? IdentifierShape() : HistoryShape()),
            new BsonDocument("$expr", collection == CatalogIdentifierRecords.Collection ? IdentifierExpression("$$ROOT") : HistoryExpression())
        });
        return new("$or", new BsonArray { legacy, current });
    }
    private static BsonDocument IdentifierShape() => Object(new()
    {
        { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 2 } } } },
        { "organizationId", Long() }, { "skuId", Long() }, { "kind", Choice("skuCode", "gtin", "supplier", "omi", "smart") },
        { "namespace", Text(256) }, { "rawValue", Text(128) }, { "canonicalValue", Text(128) }, { "isPrimary", new BsonDocument("bsonType", "bool") },
        { "effectiveAt", Date() }, { "retiredAt", new BsonDocument("bsonType", new BsonArray { "date", "null" }) },
        { "revision", Long() }, { "provenanceReference", Text(500) }, { "auditId", Long() }, { "definitionId", Long() }
    });
    private static BsonDocument HistoryShape() => Object(new()
    {
        { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 2 } } } },
        { "identifierId", Long() }, { "organizationId", Long() }, { "skuId", Long() }, { "assignmentVersion", Long() },
        { "event", Choice("assigned", "retired", "aliased") }, { "actorId", Long() }, { "createdAt", Date() },
        { "auditId", Long() }, { "assignment", IdentifierShape() }
    });
    private static BsonValue IdentifierExpression(string path)
    {
        var raw = String(path + ".rawValue"); var canonical = String(path + ".canonicalValue");
        var length = new BsonDocument("$strLenBytes", raw);
        var gtinLengths = new BsonDocument("$in", new BsonArray { length, new BsonArray { 8, 12, 13, 14 } });
        // Safe padding even when a malformed value reaches $expr before $jsonSchema rejects it.
        var paddingLength = new BsonDocument("$max", new BsonArray { 0, new BsonDocument("$subtract", new BsonArray { 14, length }) });
        var padded = new BsonDocument("$concat", new BsonArray { new BsonDocument("$substrBytes", new BsonArray { "00000000000000", 0, paddingLength }), raw });
        return And(
            DateRange(path + ".effectiveAt"), Or(Equal(path + ".retiredAt", BsonNull.Value), And(DateRange(path + ".retiredAt"), Compare("$gte", path + ".retiredAt", path + ".effectiveAt"))),
            Or(Equal(path + ".isPrimary", false), And(Equal(path + ".kind", "skuCode"), Equal(path + ".retiredAt", BsonNull.Value))),
            new BsonDocument("$switch", new BsonDocument
            {
                { "branches", new BsonArray
                    {
                        new BsonDocument { { "case", Equal(path + ".kind", "skuCode") }, { "then", And(Equal(path + ".namespace", "internal"), Regex(raw, "^[A-Za-z0-9._-]{1,64}\\z"), Equal(canonical, new BsonDocument("$toUpper", raw))) } },
                        new BsonDocument { { "case", Equal(path + ".kind", "gtin") }, { "then", And(gtinLengths, Regex(raw, "^[0-9]+\\z"), Equal(canonical, padded), Regex(String(path + ".namespace"), "^catalog:[1-9][0-9]*:[1-9][0-9]*:(?:shop|[1-9][0-9]*)\\z")) } }
                    }
                },
                { "default", Equal(raw, canonical) }
            })
        );
    }
    private static BsonValue HistoryExpression() => And(IdentifierExpression("$$ROOT.assignment"), DateRange("$$ROOT.createdAt"),
        Equal("$$ROOT.identifierId", "$$ROOT.assignment._id"), Equal("$$ROOT.organizationId", "$$ROOT.assignment.organizationId"),
        Equal("$$ROOT.skuId", "$$ROOT.assignment.skuId"), Equal("$$ROOT.assignmentVersion", "$$ROOT.assignment.revision"),
        Equal("$$ROOT.auditId", "$$ROOT.assignment.auditId"), Compare("$gte", "$$ROOT.createdAt", "$$ROOT.assignment.effectiveAt"),
        new BsonDocument("$switch", new BsonDocument
        {
            { "branches", new BsonArray
                {
                    new BsonDocument { { "case", Equal("$$ROOT.event", "assigned") }, { "then", And(Equal("$$ROOT.assignmentVersion", 1L), Equal("$$ROOT.createdAt", "$$ROOT.assignment.effectiveAt"), Equal("$$ROOT.assignment.retiredAt", BsonNull.Value)) } },
                    new BsonDocument { { "case", Equal("$$ROOT.event", "retired") }, { "then", And(Equal("$$ROOT.assignment.retiredAt", "$$ROOT.createdAt"), Compare("$gt", "$$ROOT.assignmentVersion", 1L)) } }
                }
            },
            { "default", And(Equal("$$ROOT.assignment.kind", "skuCode"), Equal("$$ROOT.assignment.isPrimary", false), Equal("$$ROOT.assignment.retiredAt", BsonNull.Value), Compare("$gt", "$$ROOT.assignmentVersion", 1L)) }
        }));
    private static BsonValue DateRange(string path) => And(Compare("$gt", path, new BsonDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc))),
        Compare("$lte", path, new BsonDateTime(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc))));
    private static BsonDocument Object(BsonDocument fields) => new() { { "bsonType", "object" }, { "required", new BsonArray(fields.Names) }, { "additionalProperties", false }, { "properties", fields } };
    private static BsonDocument Long() => new() { { "bsonType", "long" }, { "minimum", 1L } };
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Choice(params string[] values) => new() { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Text(int maximum) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum }, { "pattern", "^(?![\\s\\p{Z}])[^\\x00-\\x1f\\x7f-\\x9f]*(?<![\\s\\p{Z}])\\z" } };
    private static BsonDocument String(string path) => new("$convert", new BsonDocument { { "input", path }, { "to", "string" }, { "onError", "" }, { "onNull", "" } });
    private static BsonDocument Regex(BsonValue value, string regex) => new("$regexMatch", new BsonDocument { { "input", value }, { "regex", regex } });
    private static BsonDocument Equal(BsonValue left, BsonValue right) => Compare("$eq", left, right);
    private static BsonDocument Compare(string operation, BsonValue left, BsonValue right) => new(operation, new BsonArray { left, right });
    private static BsonDocument And(params BsonValue[] values) => new("$and", new BsonArray(values));
    private static BsonDocument Or(params BsonValue[] values) => new("$or", new BsonArray(values));
}
