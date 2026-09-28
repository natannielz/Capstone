using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed partial class CatalogStockCountChoiceTests(MongoFixture fixture)
{
    private static readonly UpdateDefinitionBuilder<BsonDocument> U = Builders<BsonDocument>.Update;
    private static readonly AuthorizationReadMetadata CountAudit = new("inventory-stock-count", "synthetic-stock-choice");
    private static readonly AuthorizationReadMetadata HistoryAudit = new("inventory-stock-history", "synthetic-stock-history-choice");

    private static Task PermissionAsync(CatalogAuthoringTestData data, ActorContext actor, params string[] permissions) =>
        data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", actor.UserId), U.Set("permissions", new BsonArray(permissions)));

    private static async Task NamesAsync(CatalogAuthoringTestData data)
    {
        var nodes = await data.Rows("authorizationScopes").Find(new BsonDocument()).ToListAsync();
        foreach (var node in nodes.Where(node => node["_id"].AsInt64 >= 100))
        {
            var id = node["_id"].AsInt64;
            var name = id switch { 100 => "Unit Selatan", 101 => "Toko Melati", 102 => "Gudang Utama", 103 => "Kanal Sekolah", 104 => "Gudang Cadangan", _ => "Lokasi " + id };
            await data.Rows("organizationProfiles").InsertOneAsync(new BsonDocument
            {
                { "_id", id }, { "schemaVersion", 1 }, { "kind", node["kind"] }, { "parentId", node.GetValue("parentId", BsonNull.Value) },
                { "code", "NODE-" + id }, { "normalizedCode", "NODE-" + id }, { "name", name }, { "version", 1L },
                { "createdBy", 998L }, { "updatedBy", 999L }, { "createdAt", data.Clock.UtcNow.UtcDateTime },
                { "updatedAt", data.Clock.UtcNow.UtcDateTime }, { "approvedProposalId", id + 10000 }
            });
        }
    }

    [Theory]
    [InlineData(StockCountChoicePurpose.Count, Permissions.StockCount)]
    [InlineData(StockCountChoicePurpose.History, Permissions.StockRead)]
    public async Task Warehouse_only_permission_returns_actual_names_and_canonical_location(StockCountChoicePurpose purpose, string permission)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        await NamesAsync(data); await PermissionAsync(data, data.WarehouseActor, permission);
        var result = await data.Service.StockWarehouseOptionsAsync(data.WarehouseActor, purpose, purpose == StockCountChoicePurpose.Count ? CountAudit : HistoryAudit);
        var option = Assert.Single(result);
        Assert.Equal(Warehouse, option.Location); Assert.Equal("Unit Selatan", option.OrganizationName);
        Assert.Equal("Toko Melati", option.ShopName); Assert.Equal("Gudang Utama", option.WarehouseName); Assert.Equal("Kanal Sekolah", option.ChannelName);
    }

    [Theory]
    [InlineData(StockCountChoicePurpose.Count, Permissions.StockRead)]
    [InlineData(StockCountChoicePurpose.History, Permissions.StockCount)]
    [InlineData(StockCountChoicePurpose.Count, Permissions.CatalogManage)]
    [InlineData(StockCountChoicePurpose.History, Permissions.CatalogRead)]
    public async Task Choice_purposes_do_not_borrow_another_permission(StockCountChoicePurpose purpose, string permission)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        await NamesAsync(data); await PermissionAsync(data, data.WarehouseActor, permission);
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.StockWarehouseOptionsAsync(data.WarehouseActor, purpose,
            purpose == StockCountChoicePurpose.Count ? CountAudit : HistoryAudit));
        Assert.Equal(before, await data.StateAsync());
    }

    [Fact]
    public async Task Shop_grant_expands_actual_warehouses_and_deduplicates_overlapping_grants()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); await NamesAsync(data);
        await PermissionAsync(data, data.Maker, Permissions.StockCount);
        await data.Base.AddGrantAsync(data.Maker, 7000, Warehouse, [Permissions.StockCount]);
        var result = await data.Service.StockWarehouseOptionsAsync(data.Maker, StockCountChoicePurpose.Count, CountAudit);
        Assert.Equal(new[] { Warehouse, SecondWarehouse }, result.Select(option => option.Location));
        Assert.Equal(new[] { "Gudang Utama", "Gudang Cadangan" }, result.Select(option => option.WarehouseName));
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("wrong_parent")]
    [InlineData("revoked_grant")]
    [InlineData("expired_membership")]
    public async Task Expansion_uses_current_hierarchy_and_effective_grants(string change)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); await NamesAsync(data);
        await PermissionAsync(data, data.Maker, Permissions.StockCount);
        if (change == "suspended") await data.Rows("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 104L), U.Set("status", "suspended").Inc("version", 1L));
        if (change == "wrong_parent") await data.Rows("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 104L), U.Set("parentId", 201L).Inc("version", 1L));
        if (change is "revoked_grant" or "expired_membership")
        {
            await data.Base.AddGrantAsync(data.Maker, 7000, OtherOwner, [Permissions.StockCount]);
            if (change == "revoked_grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 7000L), U.Set("status", "revoked"));
            else await data.Rows("memberships").UpdateOneAsync(new BsonDocument("_id", 7000L), U.Set("expiresAt", data.Clock.UtcNow.UtcDateTime));
        }
        var result = await data.Service.StockWarehouseOptionsAsync(data.Maker, StockCountChoicePurpose.Count, CountAudit);
        Assert.Equal(change is "suspended" or "wrong_parent" ? new[] { 102L } : new[] { 102L, 104L }, result.Select(option => option.Location.WarehouseId!.Value));
        Assert.All(result, option => Assert.Equal(103L, option.Location.ChannelId));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong_parent")]
    [InlineData("wrong_kind")]
    [InlineData("wrong_id_type")]
    [InlineData("empty_name")]
    [InlineData("hidden_name")]
    [InlineData("wrong_version_type")]
    public async Task Named_options_fail_closed_for_missing_or_malformed_real_profiles(string change)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); await NamesAsync(data);
        await PermissionAsync(data, data.WarehouseActor, Permissions.StockCount);
        var row = await data.RowAsync("organizationProfiles", 102);
        if (change == "missing") await data.Rows("organizationProfiles").DeleteOneAsync(new BsonDocument("_id", 102L));
        else
        {
            if (change == "wrong_parent") row["parentId"] = 201L;
            if (change == "wrong_kind") row["kind"] = "shop";
            if (change == "wrong_id_type") { await data.Rows("organizationProfiles").DeleteOneAsync(new BsonDocument("_id", 102L)); row["_id"] = 102; }
            if (change == "empty_name") row["name"] = " ";
            if (change == "hidden_name") row["name"] = "\u202eMisleading";
            if (change == "wrong_version_type") row["version"] = 1;
            await data.Rows("organizationProfiles").ReplaceOneAsync(new BsonDocument("_id", 102L), row, new ReplaceOptions { IsUpsert = true, BypassDocumentValidation = true });
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.StockWarehouseOptionsAsync(data.WarehouseActor, StockCountChoicePurpose.Count, CountAudit));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public async Task Unknown_choice_purpose_is_rejected(int purpose)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.StockWarehouseOptionsAsync(data.WarehouseActor, (StockCountChoicePurpose)purpose, CountAudit));
    }

    [Fact]
    public async Task Stock_counter_can_choose_unpublished_shop_owned_package_with_expected_versions()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); var sku = await data.SkuAsync(24);
        await PermissionAsync(data, data.WarehouseActor, Permissions.StockCount);
        var page = await data.Service.CountablePackagesAsync(data.WarehouseActor, Warehouse, 0, 25, null, CountAudit);
        Assert.Equal(1, page.Total); var option = Assert.Single(page.Items);
        Assert.Equal(sku.Id, option.SkuId); Assert.Equal(1, option.SkuRevision); Assert.Equal(sku.CurrentDefinitionId!.Value, option.DefinitionId);
        Assert.Equal(1, option.DefinitionVersion); Assert.Equal("PENCIL-12", option.SkuCode); Assert.Equal("Pensil", option.FamilyName);
        Assert.Equal("Dus", option.PackageLabel); Assert.Equal("sealed", option.PackageKind); Assert.Equal("piece", option.BaseUnitCode); Assert.Equal(24, option.BaseUnitsPerPackage);
    }

    [Fact]
    public async Task Package_choices_include_exact_warehouse_and_shop_but_exclude_siblings_and_other_channels()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); var shared = await data.SkuAsync();
        var localFamily = await data.FamilyAsync(data.WarehouseActor, Warehouse, "LOCAL");
        var local = await data.Service.CreateSkuAsync(data.WarehouseActor, new(localFamily.Id, localFamily.Version, "LOCAL-1", "local-base", Definition()), Metadata("local-pack"));
        var siblingFamily = await data.FamilyAsync(data.Maker, SecondWarehouse, "SIBLING");
        await data.Service.CreateSkuAsync(data.Maker, new(siblingFamily.Id, siblingFamily.Version, "SIBLING-1", "sibling-base", Definition()), Metadata("sibling-pack"));
        var otherFamily = await data.FamilyAsync(data.Other, OtherOwner, "OTHER");
        await data.Service.CreateSkuAsync(data.Other, new(otherFamily.Id, otherFamily.Version, "OTHER-1", "other-base", Definition()), Metadata("other-pack"));
        await PermissionAsync(data, data.WarehouseActor, Permissions.StockCount);
        var page = await data.Service.CountablePackagesAsync(data.WarehouseActor, Warehouse, 0, 25, null, CountAudit);
        Assert.Equal(2, page.Total); Assert.Equal(new[] { shared.Id, local.Id }, page.Items.Select(item => item.SkuId));
    }

    [Theory]
    [InlineData("missing_definition")]
    [InlineData("wrong_definition_sku")]
    [InlineData("inactive_sku")]
    [InlineData("inactive_family")]
    [InlineData("inactive_category")]
    [InlineData("wrong_family_owner")]
    [InlineData("wrong_category_owner")]
    [InlineData("wrong_category_code")]
    [InlineData("array_active")]
    [InlineData("int_revision")]
    [InlineData("wrong_display")]
    public async Task Invalid_current_graph_is_excluded_before_count_and_page(string change)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var firstFamily = await data.FamilyAsync(code: "FIRST");
        var first = await data.Service.CreateSkuAsync(data.Maker, new(firstFamily.Id, firstFamily.Version, "FIRST-12", "first-base", Definition()), Metadata("first-choice"));
        var badFamily = await data.FamilyAsync(code: "BAD");
        var bad = await data.Service.CreateSkuAsync(data.Maker, new(badFamily.Id, badFamily.Version, "BAD-12", "bad-base", Definition()), Metadata("bad-choice"));
        var family = await data.RowAsync("productFamilies", badFamily.Id.Value);
        var collection = "skus"; var id = bad.Id.Value; UpdateDefinition<BsonDocument> changeSet = U.Set("currentDefinitionId", 999999L);
        switch (change)
        {
            case "wrong_definition_sku": collection = "variantDefinitions"; id = bad.CurrentDefinitionId!.Value.Value; changeSet = U.Set("skuId", 999999L); break;
            case "inactive_sku": changeSet = U.Set("active", false); break;
            case "inactive_family": collection = "productFamilies"; id = badFamily.Id.Value; changeSet = U.Set("active", false); break;
            case "inactive_category": collection = "catalogCategories"; id = family["categoryId"].AsInt64; changeSet = U.Set("active", false); break;
            case "wrong_family_owner": collection = "productFamilies"; id = badFamily.Id.Value; changeSet = U.Set("authoringOwner.warehouseId", 104L).Set("authoringOwner.kind", "fulfillmentWarehouse"); break;
            case "wrong_category_owner": collection = "catalogCategories"; id = family["categoryId"].AsInt64; changeSet = U.Set("authoringOwner.warehouseId", 104L).Set("authoringOwner.kind", "fulfillmentWarehouse"); break;
            case "wrong_category_code": collection = "productFamilies"; id = badFamily.Id.Value; changeSet = U.Set("categoryCode", "WRONG"); break;
            case "array_active": changeSet = U.Set("active", new BsonArray { true }); break;
            case "int_revision": changeSet = U.Set("revision", 1); break;
            case "wrong_display": changeSet = U.Set("displayName", "Wrong display"); break;
        }
        await data.Rows(collection).UpdateOneAsync(new BsonDocument("_id", id), changeSet, new UpdateOptions { BypassDocumentValidation = true });
        await PermissionAsync(data, data.WarehouseActor, Permissions.StockCount);
        var page = await data.Service.CountablePackagesAsync(data.WarehouseActor, Warehouse, 0, 1, null, CountAudit);
        Assert.Equal(1, page.Total); Assert.Equal(first.Id, Assert.Single(page.Items).SkuId);
        var end = await data.Service.CountablePackagesAsync(data.WarehouseActor, Warehouse, 1, 1, null, CountAudit);
        Assert.Equal(1, end.Total); Assert.Empty(end.Items);
    }

    [Theory]
    [InlineData("shop")]
    [InlineData("sibling")]
    [InlineData("channel")]
    [InlineData("branch")]
    [InlineData("warehouse_zero")]
    public async Task Package_choice_requires_exact_canonical_authorized_warehouse(string change)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); await data.SkuAsync();
        await PermissionAsync(data, data.WarehouseActor, Permissions.StockCount);
        var target = change switch { "shop" => Shop, "sibling" => SecondWarehouse, "channel" => Warehouse with { ChannelId = 203 }, "branch" => Warehouse with { BranchId = 11 }, _ => Warehouse with { WarehouseId = 0 } };
        var error = await Record.ExceptionAsync(() => data.Service.CountablePackagesAsync(data.WarehouseActor, target, 0, 25, null, CountAudit));
        Assert.True(error is AuthorizationDeniedException or DomainValidationException, error?.ToString() ?? "Expected denial");
    }

    [Fact]
    public async Task Search_and_offset_are_applied_to_admissible_package_graph()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); var family = await data.FamilyAsync();
        var first = await data.Service.CreateSkuAsync(data.Maker, new(family.Id, 1, "BOX.12", "first", Definition()), Metadata("search-first"));
        var second = await data.Service.CreateSkuAsync(data.Maker, new(family.Id, 1, "BOXA12", "second", Definition()), Metadata("search-second"));
        await PermissionAsync(data, data.WarehouseActor, Permissions.StockCount);
        var exact = await data.Service.CountablePackagesAsync(data.WarehouseActor, Warehouse, 0, 1, "BOX.12", CountAudit);
        Assert.Equal(1, exact.Total); Assert.Equal(first.Id, Assert.Single(exact.Items).SkuId);
        var page = await data.Service.CountablePackagesAsync(data.WarehouseActor, Warehouse, 1, 1, "BOX", CountAudit);
        Assert.Equal(2, page.Total); Assert.Equal(second.Id, Assert.Single(page.Items).SkuId);
    }

    [Theory]
    [InlineData(-1, 25, null)]
    [InlineData(10001, 25, null)]
    [InlineData(0, 0, null)]
    [InlineData(0, 101, null)]
    [InlineData(0, 25, "\n")]
    public async Task Invalid_page_or_search_does_not_mutate_audit_or_fences(int offset, int limit, string? search)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); await PermissionAsync(data, data.WarehouseActor, Permissions.StockCount);
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.CountablePackagesAsync(data.WarehouseActor, Warehouse, offset, limit, search, CountAudit));
        Assert.Equal(before, await data.StateAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalid_audit_purpose_or_correlation_is_rejected_before_mutations(bool purpose)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); await PermissionAsync(data, data.WarehouseActor, Permissions.StockCount);
        var audit = purpose ? HistoryAudit : new AuthorizationReadMetadata("inventory-stock-count", new string('x', 129));
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.CountablePackagesAsync(data.WarehouseActor, Warehouse, 0, 25, null, audit));
        Assert.Equal(before, await data.StateAsync());
    }
}
