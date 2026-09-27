using MongoDB.Bson;

namespace UnitToko.Platform.Mongo.Migrations;

// Frozen v6 persistence contract. Entitlements record an approved decision; migration never grants access.
internal static class CatalogVisibilitySchemas
{
    public static IReadOnlyDictionary<string, BsonDocument> Collections() => new Dictionary<string, BsonDocument>
    {
        ["catalogEntitlements"] = new("$jsonSchema", Entitlement()),
        ["catalogAssortments"] = new("$jsonSchema", Assortment())
    };

    private static BsonDocument Entitlement()
    {
        var schema = Document(new()
        {
            { "_id", Long() }, { "schemaVersion", SchemaVersion() }, { "userId", Long() },
            { "membershipId", Long() }, { "assortmentId", Long() }, { "audienceScope", AudienceScope() },
            { "status", Status() }, { "effectiveAt", Date() }, { "version", Long() }, { "fence", Long(0) },
            { "requestedBy", Long() }, { "approvedBy", Long() }, { "approvalId", Long() },
            { "approvalVersion", Long() }, { "approvedAt", Date() }
        });
        var properties = schema["properties"].AsBsonDocument;
        properties.Add("expiresAt", new BsonDocument("bsonType", new BsonArray { "date", "null" }));
        properties.Add("revokedAt", new BsonDocument("bsonType", new BsonArray { "date", "null" }));
        return schema;
    }

    private static BsonDocument Assortment() => Document(new()
    {
        { "_id", Long() }, { "schemaVersion", SchemaVersion() }, { "ownerScope", OwnerScope() },
        { "status", Status() }, { "version", Long() }, { "fence", Long(0) }
    });

    private static BsonDocument AudienceScope() => new("oneOf", new BsonArray { BuyingScope(), OwnerScope() });

    private static BsonDocument BuyingScope()
    {
        var schema = Scope();
        var properties = schema["properties"].AsBsonDocument;
        foreach (var field in new[] { "shopId", "warehouseId", "channelId" }) properties[field] = Null();
        schema.Add("oneOf", new BsonArray
        {
            new BsonDocument
            {
                { "properties", new BsonDocument("kind", Choice("buyerOwned")) },
                { "anyOf", new BsonArray
                    {
                        new BsonDocument("properties", new BsonDocument("divisionId", Null())),
                        new BsonDocument { { "required", new BsonArray { "branchId" } },
                            { "properties", new BsonDocument("branchId", Long()) } }
                    }
                }
            },
            Variant("buyingDivision", ["branchId", "divisionId"], []),
            Variant("buyingBranch", ["branchId"], ["divisionId"]),
            Variant("buyingOrganization", [], ["branchId", "divisionId"])
        });
        return schema;
    }

    private static BsonDocument OwnerScope()
    {
        var schema = Scope();
        schema["required"].AsBsonArray.AddRange(new BsonArray { "shopId", "channelId" });
        var properties = schema["properties"].AsBsonDocument;
        properties["shopId"] = Long(); properties["channelId"] = Long();
        properties["branchId"] = Null(); properties["divisionId"] = Null();
        schema.Add("oneOf", new BsonArray
        {
            Variant("fulfillmentShop", [], ["warehouseId"]),
            Variant("fulfillmentWarehouse", ["warehouseId"], [])
        });
        return schema;
    }

    private static BsonDocument Scope() => new()
    {
        { "bsonType", "object" }, { "required", new BsonArray { "kind", "organizationId" } },
        { "additionalProperties", false }, { "properties", new BsonDocument
            {
                { "kind", Choice("buyerOwned", "buyingDivision", "buyingBranch", "buyingOrganization", "fulfillmentShop", "fulfillmentWarehouse") },
                { "organizationId", Long() }, { "branchId", NullableLong() }, { "divisionId", NullableLong() },
                { "shopId", NullableLong() }, { "warehouseId", NullableLong() }, { "channelId", NullableLong() }
            }
        }
    };

    private static BsonDocument Variant(string kind, string[] required, string[] absent)
    {
        var properties = new BsonDocument("kind", Choice(kind));
        foreach (var field in required) properties.Add(field, Long());
        foreach (var field in absent) properties.Add(field, Null());
        var result = new BsonDocument("properties", properties);
        if (required.Length > 0) result.Add("required", new BsonArray(required));
        return result;
    }

    private static BsonDocument Document(BsonDocument properties) => new()
    {
        { "bsonType", "object" }, { "required", new BsonArray(properties.Names) },
        { "additionalProperties", false }, { "properties", properties }
    };
    private static BsonDocument SchemaVersion() => new() { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } };
    private static BsonDocument Long(long minimum = 1) => new() { { "bsonType", "long" }, { "minimum", minimum } };
    private static BsonDocument NullableLong() => new() { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } };
    private static BsonDocument Choice(params string[] values) => new() { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Status() => Choice("active", "suspended", "revoked");
    private static BsonDocument Null() => new("bsonType", "null");
    private static BsonDocument Date() => new("bsonType", "date");
}
