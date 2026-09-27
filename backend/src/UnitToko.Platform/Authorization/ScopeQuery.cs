using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Authorization;

/// <summary>Complete grant alternatives and live hierarchy guards are combined before MongoDB paging/counts.</summary>
public static class ScopeQuery
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private static readonly FilterDefinition<BsonDocument> Deny = new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { 1, 0 }));

    public static FilterDefinition<BsonDocument> Create(AuthorizationQuery query)
    {
        if (!query.IsAllowed) return Deny;
        return F.And(CanonicalResource(), F.Or(query.Clauses.Select(Clause)), ActiveHierarchy(query.Directory));
    }

    private static FilterDefinition<BsonDocument> CanonicalResource()
    {
        var properties = new BsonDocument();
        foreach (var field in new[] { "buyingOrganizationId", "branchId", "divisionId", "fulfillmentOrganizationId", "shopId", "warehouseId", "channelId", "createdBy" })
            properties.Add(field, new BsonDocument { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } });
        properties.Add("isAdministrative", new BsonDocument("bsonType", "bool"));
        properties.Add("assignedUserIds", new BsonDocument { { "bsonType", "array" },
            { "items", new BsonDocument { { "bsonType", "long" }, { "minimum", 1L } } } });
        return new BsonDocument("$jsonSchema", new BsonDocument { { "bsonType", "object" }, { "properties", properties } });
    }

    private static FilterDefinition<BsonDocument> Clause(ScopeClause clause)
    {
        var scope = clause.Scope;
        if (scope.Kind == GrantScopeKind.System) return F.Eq("isAdministrative", true);
        var filters = new List<FilterDefinition<BsonDocument>> { F.Ne("isAdministrative", true) };
        if (scope.Kind is GrantScopeKind.BuyerOwned or GrantScopeKind.BuyingDivision or GrantScopeKind.BuyingBranch or GrantScopeKind.BuyingOrganization)
        {
            filters.Add(Id("buyingOrganizationId", scope.OrganizationId));
            if (scope.Kind is GrantScopeKind.BuyerOwned or GrantScopeKind.BuyingDivision or GrantScopeKind.BuyingBranch) filters.Add(Id("branchId", scope.BranchId));
            if (scope.Kind is GrantScopeKind.BuyerOwned or GrantScopeKind.BuyingDivision) filters.Add(Id("divisionId", scope.DivisionId));
            if (scope.Kind == GrantScopeKind.BuyerOwned) filters.Add(F.Or(F.Eq("createdBy", clause.ActorId), F.AnyEq("assignedUserIds", clause.ActorId)));
        }
        else
        {
            filters.Add(Id("fulfillmentOrganizationId", scope.OrganizationId)); filters.Add(Id("shopId", scope.ShopId)); filters.Add(Id("channelId", scope.ChannelId));
            if (scope.Kind == GrantScopeKind.FulfillmentWarehouse || scope.Kind == GrantScopeKind.CourierAssignment && scope.WarehouseId is not null)
                filters.Add(Id("warehouseId", scope.WarehouseId));
            if (scope.Kind == GrantScopeKind.CourierAssignment) filters.Add(F.AnyEq("assignedUserIds", clause.ActorId));
        }
        return F.And(filters);
    }

    private static FilterDefinition<BsonDocument> ActiveHierarchy(ScopeDirectory directory)
    {
        var fields = new[] { "buyingOrganizationId", "branchId", "divisionId", "fulfillmentOrganizationId", "shopId", "warehouseId", "channelId" };
        var administrative = F.And(F.Eq("isAdministrative", true), F.And(fields.Select(field => Id(field, null))));
        var business = new List<FilterDefinition<BsonDocument>>
        {
            F.Ne("isAdministrative", true), F.Or(F.Ne("buyingOrganizationId", BsonNull.Value), F.Ne("fulfillmentOrganizationId", BsonNull.Value))
        };
        void Dimension(string field, ScopeNodeKind kind, string? parentField = null)
        {
            var alternatives = new List<FilterDefinition<BsonDocument>> { Id(field, null) };
            foreach (var node in directory.Nodes.Where(node => node.Kind == kind && node.Status == AuthorizationStatus.Active && node.Version > 0))
            {
                if (parentField is null)
                {
                    if (node.ParentId is null) alternatives.Add(Id(field, node.Id));
                }
                else if (node.ParentId is > 0)
                    alternatives.Add(F.And(Id(field, node.Id), Id(parentField, node.ParentId)));
            }
            business.Add(F.Or(alternatives));
        }
        Dimension("buyingOrganizationId", ScopeNodeKind.BuyingOrganization);
        Dimension("branchId", ScopeNodeKind.Branch, "buyingOrganizationId");
        Dimension("divisionId", ScopeNodeKind.Division, "branchId");
        Dimension("fulfillmentOrganizationId", ScopeNodeKind.FulfillmentOrganization);
        Dimension("shopId", ScopeNodeKind.Shop, "fulfillmentOrganizationId");
        Dimension("warehouseId", ScopeNodeKind.Warehouse, "shopId");
        Dimension("channelId", ScopeNodeKind.Channel, "fulfillmentOrganizationId");
        return F.Or(administrative, F.And(business));
    }
    private static FilterDefinition<BsonDocument> Id(string field, long? value) => value.HasValue ? F.Eq(field, value.Value) : F.Eq(field, BsonNull.Value);
}
