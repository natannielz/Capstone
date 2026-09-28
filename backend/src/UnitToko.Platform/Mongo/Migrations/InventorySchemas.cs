using MongoDB.Bson;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Draft storage contract. Not registered as an applied migration; callers never install DDL.</summary>
internal static class InventorySchemas
{
    internal static BsonDocument BalanceKey() => new() { { "physicalPoolId", 1 }, { "skuId", 1 }, { "definitionId", 1 }, { "lotId", 1 } };
    internal static BsonDocument BalanceValidator()
    {
        BsonDocument Long(long minimum) => new() { { "bsonType", "long" }, { "minimum", minimum } };
        BsonDocument Object(BsonDocument properties) => new()
        {
            { "bsonType", "object" }, { "required", new BsonArray(properties.Names) }, { "additionalProperties", false }, { "properties", properties }
        };
        var fields = new BsonDocument
        {
            { "_id", Long(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", new BsonDocument { { "bsonType", "string" }, { "enum", new BsonArray { InventoryBson.BalanceKind } } } },
            { "physicalPoolId", Long(1) }, { "skuId", Long(1) }, { "definitionId", Long(1) }, { "lotId", Long(1) },
            { "location", Object(new() { { "organizationId", Long(1) }, { "shopId", Long(1) }, { "warehouseId", Long(1) }, { "channelId", Long(1) } }) },
            { "onHand", Long(0) }, { "reserved", Long(0) }, { "held", Long(0) }, { "expired", Long(0) }, { "version", Long(1) }
        };
        var unheld = new BsonDocument("$subtract", new BsonArray { "$onHand", "$held" });
        var eligible = new BsonDocument("$subtract", new BsonArray { unheld, "$expired" });
        return new("$and", new BsonArray
        {
            new BsonDocument("$jsonSchema", Object(fields)),
            new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$lte", new BsonArray { "$held", "$onHand" }),
                new BsonDocument("$lte", new BsonArray { "$expired", unheld }),
                new BsonDocument("$lte", new BsonArray { "$reserved", eligible })
            }))
        });
    }
    internal static bool Contains(BsonDocument candidate, BsonDocument required) =>
        CatalogSchemas.SemanticEquals(candidate, required) ||
        candidate.TryGetValue("$and", out var terms) && terms.IsBsonArray &&
        terms.AsBsonArray.Any(term => term.IsBsonDocument && Contains(term.AsBsonDocument, required));
}
