using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Authorization;

public sealed partial class AccessAdministrationService
{
    private static FilterDefinition<BsonDocument> CanonicalHistoryScope(string path, ScopeDirectory directory)
    {
        var fields = new[] { "organizationId", "branchId", "divisionId", "shopId", "warehouseId", "channelId" };
        var properties = new BsonDocument("kind", new BsonDocument("enum", new BsonArray
            { "buyerOwned", "buyingDivision", "buyingBranch", "buyingOrganization", "fulfillmentShop", "fulfillmentWarehouse", "courierAssignment", "system" }));
        foreach (var field in fields) properties.Add(field, new BsonDocument { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } });
        BsonDocument schema = new() { { "bsonType", "object" }, { "required", new BsonArray { "kind" } }, { "additionalProperties", false }, { "properties", properties } };
        foreach (var part in path.Split('.').Reverse()) schema = new BsonDocument
            { { "bsonType", "object" }, { "required", new BsonArray { part } }, { "properties", new BsonDocument(part, schema) } };
        FilterDefinition<BsonDocument> shape = new BsonDocument("$jsonSchema", schema);
        FilterDefinition<BsonDocument> Null(string field) => F.Eq(path + "." + field, BsonNull.Value);
        FilterDefinition<BsonDocument> Present(string field) => F.Ne(path + "." + field, BsonNull.Value);
        FilterDefinition<BsonDocument> Root(ScopeNodeKind kind) => F.In(path + ".organizationId",
            directory.Nodes.Where(node => node.Kind == kind && node.ParentId is null && node.Version > 0).Select(node => node.Id));
        FilterDefinition<BsonDocument> Pair(string field, ScopeNodeKind kind, string parentField, bool optional)
        {
            var alternatives = directory.Nodes.Where(node => node.Kind == kind && node.ParentId is > 0 && node.Version > 0)
                .Select(node => F.Eq(path + "." + field, node.Id) & F.Eq(path + "." + parentField, node.ParentId!.Value)).ToList();
            if (optional) alternatives.Add(Null(field));
            return alternatives.Count == 0 ? new BsonDocument("$expr", false) : F.Or(alternatives);
        }
        var kindField = path + ".kind";
        var buying = F.In(kindField, new[] { "buyerOwned", "buyingDivision", "buyingBranch", "buyingOrganization" }) & Root(ScopeNodeKind.BuyingOrganization) &
            Pair("branchId", ScopeNodeKind.Branch, "organizationId", true) & Pair("divisionId", ScopeNodeKind.Division, "branchId", true) &
            Null("shopId") & Null("warehouseId") & Null("channelId") &
            (F.Eq(kindField, "buyerOwned") | (F.Eq(kindField, "buyingDivision") & Present("branchId") & Present("divisionId")) |
             (F.Eq(kindField, "buyingBranch") & Present("branchId") & Null("divisionId")) |
             (F.Eq(kindField, "buyingOrganization") & Null("branchId") & Null("divisionId")));
        var fulfillment = F.In(kindField, new[] { "fulfillmentShop", "fulfillmentWarehouse", "courierAssignment" }) & Root(ScopeNodeKind.FulfillmentOrganization) &
            Pair("shopId", ScopeNodeKind.Shop, "organizationId", false) & Pair("channelId", ScopeNodeKind.Channel, "organizationId", false) &
            Pair("warehouseId", ScopeNodeKind.Warehouse, "shopId", true) & Null("branchId") & Null("divisionId") &
            ((F.Eq(kindField, "fulfillmentShop") & Null("warehouseId")) | (F.Eq(kindField, "fulfillmentWarehouse") & Present("warehouseId")) | F.Eq(kindField, "courierAssignment"));
        var system = F.Eq(kindField, "system") & F.And(fields.Select(Null));
        return shape & (buying | fulfillment | system);
    }
}
