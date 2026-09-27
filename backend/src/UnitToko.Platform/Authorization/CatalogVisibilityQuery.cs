using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Organizations;
using DomainQuery = UnitToko.Modules.Catalog.CatalogVisibilityQuery;

namespace UnitToko.Platform.Authorization;

/// <summary>Catalog publication tuples are matched in the same array element before any result is counted.</summary>
public static class CatalogVisibilityQuery
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    internal const string PublicIdPattern = "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\z";

    public static FilterDefinition<BsonDocument> Create(DomainQuery query)
    {
        if (!query.IsAllowed) return new BsonDocument("$expr", false);
        return F.And(CanonicalResource(), F.Or(query.Clauses.Select(clause => F.And(
            F.Eq("organizationId", clause.Assortment.OwnerScope.OrganizationId!.Value),
            F.ElemMatch<BsonDocument>("catalogListings", F.And(
                F.Eq("assortmentId", clause.Assortment.Id), Owner("ownerScope", clause.Assortment.OwnerScope)))))));
    }

    internal static FilterDefinition<BsonDocument> Owner(string prefix, GrantScope scope) => F.And(
        F.Eq(prefix + ".kind", AuthorizationBson.Name(scope.Kind)),
        Id(prefix + ".organizationId", scope.OrganizationId), Id(prefix + ".branchId", scope.BranchId),
        Id(prefix + ".divisionId", scope.DivisionId), Id(prefix + ".shopId", scope.ShopId),
        Id(prefix + ".warehouseId", scope.WarehouseId), Id(prefix + ".channelId", scope.ChannelId));

    private static FilterDefinition<BsonDocument> CanonicalResource()
    {
        var ownerProperties = new BsonDocument("kind", new BsonDocument("enum", new BsonArray { "fulfillmentShop", "fulfillmentWarehouse" }));
        foreach (var field in new[] { "organizationId", "shopId", "channelId" }) ownerProperties.Add(field, PositiveLong());
        foreach (var field in new[] { "branchId", "divisionId", "warehouseId" })
            ownerProperties.Add(field, new BsonDocument { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } });
        var ownerSchema = new BsonDocument
        {
            { "bsonType", "object" }, { "required", new BsonArray { "kind", "organizationId", "shopId", "channelId" } },
            { "additionalProperties", false }, { "properties", ownerProperties }
        };
        var schema = new BsonDocument("$jsonSchema", new BsonDocument
        {
            { "bsonType", "object" }, { "required", new BsonArray { "_id", "organizationId", "catalogListings" } },
            { "properties", new BsonDocument
                {
                    { "_id", PositiveLong() }, { "organizationId", PositiveLong() },
                    { "publicId", new BsonDocument { { "bsonType", "string" }, { "minLength", 36 }, { "maxLength", 36 }, { "pattern", PublicIdPattern } } },
                    { "displayName", new BsonDocument { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", 256 },
                        { "pattern", "^[^\\x00-\\x1f\\x7f]+\\z" } } },
                    { "catalogListings", new BsonDocument
                        {
                            { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 128 }, { "uniqueItems", true },
                            { "items", new BsonDocument
                                {
                                    { "bsonType", "object" }, { "required", new BsonArray { "assortmentId", "ownerScope" } },
                                    { "additionalProperties", false },
                                    { "properties", new BsonDocument { { "assortmentId", PositiveLong() }, { "ownerScope", ownerSchema } } }
                                }
                            }
                        }
                    }
                }
            }
        });
        // Expressions guard their own array inputs: MongoDB need not evaluate $jsonSchema first.
        var listings = new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$isArray", "$catalogListings"), "$catalogListings", new BsonArray()
        });
        var ids = new BsonDocument("$map", new BsonDocument { { "input", "$$listings" }, { "as", "listing" }, { "in", "$$listing.assortmentId" } });
        var expression = new BsonDocument("$expr", new BsonDocument("$let", new BsonDocument
        {
            { "vars", new BsonDocument("listings", listings) },
            { "in", new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$size", "$$listings"),
                        new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { ids, new BsonArray() })) }),
                    new BsonDocument("$allElementsTrue", new BsonArray { new BsonDocument("$map", new BsonDocument
                        {
                            { "input", "$$listings" }, { "as", "listing" },
                            { "in", new BsonDocument("$and", new BsonArray
                                {
                                    new BsonDocument("$eq", new BsonArray { "$$listing.ownerScope.organizationId", "$organizationId" }),
                                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$$listing.ownerScope.branchId", BsonNull.Value }), BsonNull.Value }),
                                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$$listing.ownerScope.divisionId", BsonNull.Value }), BsonNull.Value }),
                                    new BsonDocument("$cond", new BsonArray
                                    {
                                        new BsonDocument("$eq", new BsonArray { "$$listing.ownerScope.kind", "fulfillmentShop" }),
                                        new BsonDocument("$eq", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$$listing.ownerScope.warehouseId", BsonNull.Value }), BsonNull.Value }),
                                        new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$$listing.ownerScope.warehouseId"), "long" })
                                    })
                                })
                            }
                        }) })
                })
            }
        }));
        return F.And(schema, expression);
    }

    private static BsonDocument PositiveLong() => new() { { "bsonType", "long" }, { "minimum", 1L } };
    private static FilterDefinition<BsonDocument> Id(string field, long? id) => F.Eq<BsonValue>(field, id.HasValue ? new BsonInt64(id.Value) : BsonNull.Value);
}
