using UnitToko.Platform.Authorization;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Identity;

internal static class PersonAuthorityBson
{
    internal static BsonDocument Require(string collection, BsonDocument row)
    {
        if (!PersonAuthoritySchemas.IsValid(collection, row)) throw Denied();
        return row;
    }
    internal static AuthorizationDeniedException Denied() => new("person_authority_unavailable");
    internal static long Id(BsonDocument row, string field = "_id") => row[field].AsInt64;
    internal static long[] Ids(BsonDocument row, string field) => row[field].AsBsonArray.Select(value => value.AsInt64).ToArray();
    internal static DateTimeOffset At(BsonDocument row, string field) => new(row[field].ToUniversalTime(), TimeSpan.Zero);

    // Equality predicates alone allow Mongo scalar/array and numeric coercions. A complete exact
    // type/field-presence schema is also required, including explicit null versus absent fields.
    internal static FilterDefinition<BsonDocument> ExactFilter(BsonDocument expected, bool mutable)
    {
        var properties = new BsonDocument();
        var filters = new List<FilterDefinition<BsonDocument>>();
        foreach (var field in expected)
        {
            if (field.Name == "fence" && mutable)
            {
                properties.Add(field.Name, new BsonDocument { { "bsonType", "long" }, { "minimum", 0L }, { "maximum", long.MaxValue - 1 } });
                continue;
            }
            properties.Add(field.Name, ExactType(field.Value));
            filters.Add(Builders<BsonDocument>.Filter.Eq(field.Name, field.Value));
        }
        filters.Add(new BsonDocument("$jsonSchema", new BsonDocument
        {
            { "bsonType", "object" }, { "required", new BsonArray(expected.Names) },
            { "additionalProperties", false }, { "properties", properties }
        }));
        return Builders<BsonDocument>.Filter.And(filters);
    }

    private static BsonDocument ExactType(BsonValue value) => value.BsonType switch
    {
        BsonType.Int64 => new("bsonType", "long"),
        BsonType.Int32 => new("bsonType", "int"),
        BsonType.String => new("bsonType", "string"),
        BsonType.DateTime => new("bsonType", "date"),
        BsonType.Null => new("bsonType", "null"),
        BsonType.Array => new() { { "bsonType", "array" }, { "items", new BsonDocument("bsonType", "long") }, { "minItems", value.AsBsonArray.Count }, { "maxItems", value.AsBsonArray.Count } },
        _ => throw Denied()
    };
}
