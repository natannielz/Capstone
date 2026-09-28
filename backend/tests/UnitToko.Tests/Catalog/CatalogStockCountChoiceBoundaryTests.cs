using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

public sealed partial class CatalogStockCountChoiceTests
{
    [Fact]
    public async Task Warehouse_choice_expansion_rejects_more_than_256_exact_targets()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        await data.Rows("authorizationScopes").InsertManyAsync(Enumerable.Range(1000, 255).Select(id =>
            AuthorizationBson.Node(new ScopeNode(id, ScopeNodeKind.Warehouse, 101, AuthorizationStatus.Active, 1))));
        await NamesAsync(data); await PermissionAsync(data, data.Maker, Permissions.StockCount);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.StockWarehouseOptionsAsync(data.Maker, StockCountChoicePurpose.Count, CountAudit));
    }

    [Theory]
    [InlineData(Permissions.StockRead)]
    [InlineData(Permissions.CatalogManage)]
    [InlineData(Permissions.CatalogRead)]
    public async Task Other_permissions_never_authorize_countable_package_query(string permission)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); await data.SkuAsync();
        await PermissionAsync(data, data.WarehouseActor, permission);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.CountablePackagesAsync(data.WarehouseActor, Warehouse, 0, 25, null, CountAudit));
    }

    [Fact]
    public async Task Empty_authorized_package_result_remains_audited_and_fenced()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        await PermissionAsync(data, data.WarehouseActor, Permissions.StockCount);
        var before = (await data.RowAsync("authorizationScopes", 102))["fence"].AsInt64;
        var page = await data.Service.CountablePackagesAsync(data.WarehouseActor, Warehouse, 0, 25, null, CountAudit);
        Assert.Equal(0, page.Total); Assert.Empty(page.Items);
        Assert.True((await data.RowAsync("authorizationScopes", 102))["fence"].AsInt64 > before);
        var read = await data.Rows("auditEvents").Find(new BsonDocument("purpose", "inventory-stock-count")).SingleAsync();
        Assert.Equal(data.WarehouseActor.UserId, read["actorId"].AsInt64); Assert.Equal(0, read["resultCount"].AsInt32);
    }

    [Theory]
    [InlineData("uuid")]
    [InlineData("attributes")]
    public async Task Selected_package_runtime_validation_rejects_noncanonical_data_beyond_generic_schema(string change)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); var sku = await data.SkuAsync();
        if (change == "uuid") await data.Rows("skus").UpdateOneAsync(new BsonDocument("_id", sku.Id.Value), U.Set("publicUuid", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard)));
        else await data.Rows("variantDefinitions").UpdateOneAsync(new BsonDocument("_id", sku.CurrentDefinitionId!.Value.Value), U.Set("attributes", new BsonArray
        {
            new BsonDocument { { "key", "z" }, { "value", "last" } }, new BsonDocument { { "key", "a" }, { "value", "first" } }
        }));
        await PermissionAsync(data, data.WarehouseActor, Permissions.StockCount);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.CountablePackagesAsync(data.WarehouseActor, Warehouse, 0, 25, null, CountAudit));
    }
}
