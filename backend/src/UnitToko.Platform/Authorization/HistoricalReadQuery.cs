using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Organizations;
using DomainQuery = UnitToko.Modules.Organizations.HistoricalReadQuery;

namespace UnitToko.Platform.Authorization;

/// <summary>The same canonical, exact buying scope is applied before every count and metadata lookup.</summary>
public static class HistoricalReadQuery
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;

    public static FilterDefinition<BsonDocument> Create(DomainQuery query)
    {
        if (!query.IsAllowed) return new BsonDocument("$expr", false);
        var scope = query.Request.TargetScope;
        var properties = new BsonDocument { { "_id", Long() }, { "buyingOrganizationId", Long() }, { "branchId", Long() } };
        foreach (var field in new[] { "divisionId", "fulfillmentOrganizationId", "shopId", "warehouseId", "channelId", "createdBy" })
            properties.Add(field, new BsonDocument { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } });
        properties.Add("isAdministrative", new BsonDocument("bsonType", "bool"));
        properties.Add("assignedUserIds", new BsonDocument { { "bsonType", "array" }, { "items", Long() } });
        properties.Add("publicId", new BsonDocument { { "bsonType", "string" }, { "minLength", 36 }, { "maxLength", 36 },
            { "pattern", CatalogVisibilityQuery.PublicIdPattern } });
        properties.Add("displayName", new BsonDocument { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", 256 },
            { "pattern", "^[^\\x00-\\x1f\\x7f-\\x9f]+\\z" } });
        var canonical = new BsonDocument("$jsonSchema", new BsonDocument
        {
            { "bsonType", "object" }, { "required", new BsonArray { "_id", "buyingOrganizationId", "branchId" } },
            { "properties", properties }
        });
        var divisions = scope.Kind == GrantScopeKind.BuyingDivision ? Id("divisionId", scope.DivisionId) :
            F.Or(Id("divisionId", null), F.In("divisionId", query.Clauses.SelectMany(clause => clause.TargetNodes)
                .Where(node => node.Kind == ScopeNodeKind.Division).Select(node => node.Id).Distinct()));
        return F.And(canonical, F.Eq("buyingOrganizationId", scope.OrganizationId!.Value), F.Eq("branchId", scope.BranchId!.Value),
            divisions, F.Ne("isAdministrative", true),
            F.Or(Id("shopId", null), F.Ne("fulfillmentOrganizationId", BsonNull.Value)),
            F.Or(Id("warehouseId", null), F.Ne("shopId", BsonNull.Value)),
            F.Or(Id("channelId", null), F.Ne("fulfillmentOrganizationId", BsonNull.Value)));
    }

    private static BsonDocument Long() => new() { { "bsonType", "long" }, { "minimum", 1L } };
    private static FilterDefinition<BsonDocument> Id(string field, long? id) => F.Eq<BsonValue>(field,
        id.HasValue ? new BsonInt64(id.Value) : BsonNull.Value);
}
