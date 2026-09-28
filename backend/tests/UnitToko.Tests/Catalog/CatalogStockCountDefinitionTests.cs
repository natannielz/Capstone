using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed partial class CatalogStockCountDefinitionTests(MongoFixture fixture)
{
    private static readonly UpdateDefinitionBuilder<BsonDocument> U = Builders<BsonDocument>.Update;
    private static async Task StockOnlyAsync(CatalogAuthoringTestData data) =>
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.WarehouseActor.UserId),
            U.Set("permissions", new BsonArray { Permissions.StockCount }));

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(24)]
    public async Task Warehouse_counter_can_capture_unpublished_shop_owned_pack_without_catalog_manage(int contents)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(contents); await StockOnlyAsync(data);
        await new MongoUnitOfWork(data.Client).ExecuteAsync((tx, ct) =>
            data.Service.WithStockCountDefinitionAsync(data.Database, tx, data.WarehouseActor, Warehouse, sku.Id.Value, 1,
                sku.CurrentDefinitionId!.Value.Value, async (snapshot, token) =>
                {
                    Assert.Equal(Shop, snapshot.Owner); Assert.Equal(sku.Id.Value, snapshot.SkuId);
                    Assert.Equal(1, snapshot.SkuRevision); Assert.Equal(contents, snapshot.BaseUnitsPerPackage);
                    Assert.Equal("pencil-base", snapshot.PhysicalItemKey); Assert.Equal("piece", snapshot.BaseUnitCode);
                    Assert.Equal("Dus", snapshot.PackageLabel); Assert.Equal("sealed", snapshot.PackageKind);
                    Assert.Equal(sku.CurrentDefinitionId.Value.Value, snapshot.DefinitionId); Assert.Equal(1, snapshot.DefinitionVersion);
                    Assert.True(snapshot.CategoryId > 0); Assert.True(snapshot.FamilyId > 0);
                    await data.Rows("stockCountProbe").InsertOneAsync(tx, new BsonDocument("_id", 1L), cancellationToken: token);
                    Assert.Equal(0, await data.CountAsync("stockCountProbe")); return 1;
                }, ct));
        Assert.Equal(1, await data.CountAsync("stockCountProbe")); Assert.Equal(0, await data.CountAsync("catalogEntitlements"));
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("warehouse")]
    [InlineData("owner")]
    [InlineData("shop_scope")]
    [InlineData("channel")]
    public async Task Unauthorized_or_noncanonical_count_scope_cannot_reach_callback(string invalid)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await StockOnlyAsync(data); var called = false;
        var actor = invalid == "permission" ? data.Maker : data.WarehouseActor;
        var location = invalid switch
        {
            "warehouse" => SecondWarehouse,
            "owner" => new GrantScope(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 201, WarehouseId: 202, ChannelId: 203),
            "shop_scope" => Shop,
            "channel" => Warehouse with { ChannelId = 203 },
            _ => Warehouse
        };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new MongoUnitOfWork(data.Client).ExecuteAsync((tx, ct) =>
            data.Service.WithStockCountDefinitionAsync(data.Database, tx, actor, location, sku.Id.Value, 1, sku.CurrentDefinitionId!.Value.Value,
                (_, _) => { called = true; return Task.FromResult(1); }, ct)));
        Assert.False(called);
    }

    [Theory]
    [InlineData("catalogCategories")]
    [InlineData("productFamilies")]
    [InlineData("skus")]
    public async Task Inactive_catalog_hierarchy_denies_count_snapshot(string collection)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await StockOnlyAsync(data); var called = false;
        await data.Rows(collection).UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, U.Set("active", false));
        await Assert.ThrowsAsync<DomainValidationException>(() => new MongoUnitOfWork(data.Client).ExecuteAsync((tx, ct) =>
            data.Service.WithStockCountDefinitionAsync(data.Database, tx, data.WarehouseActor, Warehouse, sku.Id.Value, 1,
                sku.CurrentDefinitionId!.Value.Value, (_, _) => { called = true; return Task.FromResult(1); }, ct)));
        Assert.False(called);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changed_expected_sku_or_definition_conflicts_before_callback(bool head)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await StockOnlyAsync(data); var called = false;
        await Assert.ThrowsAsync<PersistenceConflictException>(() => new MongoUnitOfWork(data.Client).ExecuteAsync((tx, ct) =>
            data.Service.WithStockCountDefinitionAsync(data.Database, tx, data.WarehouseActor, Warehouse, sku.Id.Value,
                head ? 2 : 1, head ? sku.CurrentDefinitionId!.Value.Value : 900000,
                (_, _) => { called = true; return Task.FromResult(1); }, ct)));
        Assert.False(called);
    }

    [Theory]
    [InlineData("no_transaction")]
    [InlineData("other_client")]
    [InlineData("other_database")]
    public async Task Caller_transaction_must_belong_to_exact_service_database(string mismatch)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await StockOnlyAsync(data); var called = false;
        var client = mismatch == "other_client" ? new MongoClient(data.Client.Settings.Clone()) : data.Client;
        using var tx = await client.StartSessionAsync(); if (mismatch != "no_transaction") tx.StartTransaction();
        var db = mismatch == "other_database" ? client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName + "_other") : data.Database;
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Service.WithStockCountDefinitionAsync(db, tx,
            data.WarehouseActor, Warehouse, sku.Id.Value, 1, sku.CurrentDefinitionId!.Value.Value,
            (_, _) => { called = true; return Task.FromResult(1); }));
        Assert.False(called); Assert.False(tx.IsInTransaction);
    }

    [Fact]
    public async Task Caught_callback_failure_aborts_all_prior_caller_writes()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await StockOnlyAsync(data);
        await data.Rows("stockCountProbe").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "value", 0 } });
        using var tx = await data.Client.StartSessionAsync(); tx.StartTransaction();
        await data.Rows("stockCountProbe").UpdateOneAsync(tx, new BsonDocument("_id", 1L), U.Inc("value", 1));
        var failure = new InvalidDataException("required stock audit failed");
        var caught = await Assert.ThrowsAsync<InvalidDataException>(() => data.Service.WithStockCountDefinitionAsync<int>(data.Database, tx,
            data.WarehouseActor, Warehouse, sku.Id.Value, 1, sku.CurrentDefinitionId!.Value.Value, (_, _) => throw failure));
        Assert.Same(failure, caught); Assert.False(tx.IsInTransaction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CommitTransactionAsync());
        Assert.Equal(0, (await data.RowAsync("stockCountProbe", 1))["value"].AsInt32);
    }
}
