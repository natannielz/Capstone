using MongoDB.Bson;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class HistoricalReadSchemas
{
    internal static BsonDocument Validator() => new("$and", new BsonArray
    {
        new BsonDocument("$jsonSchema", Document(new BsonDocument
        {
            { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "actorId", Long() }, { "actorGrantVersion", Long(0) }, { "source", Choice("orders", "invoices", "shipments") },
            { "permission", Choice("history.read", "report.read") }, { "purpose", Choice("historical-review", "reporting") },
            { "operation", Choice("query", "count", "detail", "public-detail", "reference") }, { "targetScope", Scope() },
            { "grantIds", Ids() }, { "membershipIds", Ids() }, { "selection", Selection() }, { "resultCount", Long(0) },
            { "correlationId", Text(128) }, { "createdAt", new BsonDocument("bsonType", "date") }
        })),
        new BsonDocument("$or", new BsonArray
        {
            new BsonDocument { { "permission", "history.read" }, { "purpose", "historical-review" } },
            new BsonDocument { { "permission", "report.read" }, { "purpose", "reporting" } }
        }),
        new BsonDocument("$or", new BsonArray
        {
            new BsonDocument { { "operation", "query" }, { "selection.offset", new BsonDocument("$exists", true) }, { "selection.limit", new BsonDocument("$exists", true) },
                { "selection.resourceId", new BsonDocument("$exists", false) }, { "selection.publicId", new BsonDocument("$exists", false) }, { "selection.referencePurpose", new BsonDocument("$exists", false) },
                { "resultCount", new BsonDocument("$lte", 100L) } },
            new BsonDocument { { "operation", "count" }, { "selection", new BsonDocument() } },
            new BsonDocument { { "operation", "detail" }, { "selection.resourceId", new BsonDocument("$exists", true) },
                { "selection.offset", new BsonDocument("$exists", false) }, { "selection.limit", new BsonDocument("$exists", false) }, { "selection.publicId", new BsonDocument("$exists", false) },
                { "selection.referencePurpose", new BsonDocument("$exists", false) }, { "resultCount", new BsonDocument("$lte", 1L) } },
            new BsonDocument { { "operation", "public-detail" }, { "selection.publicId", new BsonDocument("$exists", true) },
                { "selection.offset", new BsonDocument("$exists", false) }, { "selection.limit", new BsonDocument("$exists", false) }, { "selection.resourceId", new BsonDocument("$exists", false) },
                { "selection.referencePurpose", new BsonDocument("$exists", false) }, { "resultCount", new BsonDocument("$lte", 1L) } },
            new BsonDocument { { "operation", "reference" }, { "selection.resourceId", new BsonDocument("$exists", true) }, { "selection.referencePurpose", new BsonDocument("$exists", true) },
                { "selection.offset", new BsonDocument("$exists", false) }, { "selection.limit", new BsonDocument("$exists", false) }, { "selection.publicId", new BsonDocument("$exists", false) },
                { "resultCount", new BsonDocument("$lte", 1L) } }
        })
    });

    private static BsonDocument Scope() => new("oneOf", new BsonArray
    {
        Document(new BsonDocument { { "kind", Choice("buyingBranch") }, { "organizationId", Long() }, { "branchId", Long() } }),
        Document(new BsonDocument { { "kind", Choice("buyingDivision") }, { "organizationId", Long() }, { "branchId", Long() }, { "divisionId", Long() } })
    });
    private static BsonDocument Selection() => new()
    {
        { "bsonType", "object" }, { "additionalProperties", false }, { "properties", new BsonDocument
            {
                { "offset", new BsonDocument { { "bsonType", "int" }, { "minimum", 0 }, { "maximum", 10000 } } },
                { "limit", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 100 } } },
                { "resourceId", Long() }, { "publicId", new BsonDocument { { "bsonType", "string" }, { "minLength", 36 }, { "maxLength", 36 },
                    { "pattern", Authorization.CatalogVisibilityQuery.PublicIdPattern } } },
                { "referencePurpose", Choice("image", "evidence", "export", "event") }
            }
        }
    };
    private static BsonDocument Document(BsonDocument properties) => new()
        { { "bsonType", "object" }, { "required", new BsonArray(properties.Names) }, { "additionalProperties", false }, { "properties", properties } };
    private static BsonDocument Ids() => new() { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 256 }, { "uniqueItems", true }, { "items", Long() } };
    private static BsonDocument Long(long minimum = 1) => new() { { "bsonType", "long" }, { "minimum", minimum } };
    private static BsonDocument Choice(params string[] values) => new() { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Text(int maximum) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum },
        { "pattern", "^[^\\x00-\\x1f\\x7f-\\x9f]+\\z" } };
}
