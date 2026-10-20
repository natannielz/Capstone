using UnitToko.Platform.Catalog;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

// Browsing a larger catalog: category chips with counts, a category filter that pages correctly on the server, and an
// alphabetical order. Counts come only from packages the buyer can already see.
[Collection("Mongo")]
public sealed class CatalogBuyerBrowseTests(MongoFixture fixture)
{
    [Fact]
    public async Task Categories_count_visible_packages_for_the_search_and_the_filter_pages_within_one_category()
    {
        await using var data = await Ready(); var catalog = data.Catalog;
        var pantry = await catalog.Service.SaveCategoryAsync(data.Maker, new(Shop, null, 0, "PANTRY", "Pantry kopi dan teh"), Metadata("category-pantry"));
        await Sku(data, pantry.Id, "Teh celup melati", "TEA-25", 501);
        await Sku(data, pantry.Id, "air mineral 600 ml", "AIR-600", 501);
        var hidden = await catalog.Service.SaveCategoryAsync(data.Maker, new(Shop, null, 0, "PRIVATE", "Khusus"), Metadata("category-private"));
        await Sku(data, hidden.Id, "Barang khusus", "PRIVATE-1", 502);

        var all = await catalog.BuyerService.QueryBuyerAsync(catalog.Buyer, 0, 25, null);
        Assert.Equal(3L, all.Total);
        Assert.Equal([("ATK", "Alat tulis", 1L), ("PANTRY", "Pantry kopi dan teh", 2L)], all.Categories.Select(item => (item.Code, item.Name, item.Count)));

        var filtered = await catalog.BuyerService.QueryBuyerAsync(catalog.Buyer, 0, 1, null, category: "pantry");
        Assert.Equal(2L, filtered.Total); Assert.Equal("PANTRY", Assert.Single(filtered.Items).Variant.Family.CategoryCode);
        var second = await catalog.BuyerService.QueryBuyerAsync(catalog.Buyer, 1, 1, null, category: "PANTRY");
        Assert.NotEqual(filtered.Items[0].Variant.Sku.Id, Assert.Single(second.Items).Variant.Sku.Id);
        Assert.Equal(all.Categories.AsEnumerable(), filtered.Categories.AsEnumerable());

        var searched = await catalog.BuyerService.QueryBuyerAsync(catalog.Buyer, 0, 25, "teh");
        Assert.Equal([("PANTRY", 1L)], searched.Categories.Select(item => (item.Code, item.Count)));
        Assert.Empty((await catalog.BuyerService.QueryBuyerAsync(catalog.Buyer, 0, 25, null, category: "PRIVATE")).Items);
    }

    [Fact]
    public async Task Name_order_is_alphabetical_ignoring_case_and_added_order_stays_the_default()
    {
        await using var data = await Ready(); var catalog = data.Catalog;
        var pantry = await catalog.Service.SaveCategoryAsync(data.Maker, new(Shop, null, 0, "PANTRY", "Pantry"), Metadata("category-pantry"));
        await Sku(data, pantry.Id, "Teh celup melati", "TEA-25", 501);
        await Sku(data, pantry.Id, "air mineral 600 ml", "AIR-600", 501);

        var byName = await catalog.BuyerService.QueryBuyerAsync(catalog.Buyer, 0, 25, null, sort: CatalogBuyerSort.Name);
        Assert.Equal(["air mineral 600 ml", "Pensil", "Teh celup melati"], byName.Items.Select(item => item.Variant.Family.Name));
        var added = await catalog.BuyerService.QueryBuyerAsync(catalog.Buyer, 0, 25, null);
        Assert.Equal(["PENCIL-12", "TEA-25", "AIR-600"], added.Items.Select(item => item.Variant.Sku.Code));
        var page = await catalog.BuyerService.QueryBuyerAsync(catalog.Buyer, 1, 1, null, sort: CatalogBuyerSort.Name);
        Assert.Equal("Pensil", Assert.Single(page.Items).Variant.Family.Name);
    }

    [Fact]
    public async Task A_cart_reads_its_own_packages_and_a_package_the_buyer_cannot_see_is_simply_absent()
    {
        await using var data = await Ready(); var catalog = data.Catalog;
        var pantry = await catalog.Service.SaveCategoryAsync(data.Maker, new(Shop, null, 0, "PANTRY", "Pantry"), Metadata("category-pantry"));
        var tea = await Sku(data, pantry.Id, "Teh celup melati", "TEA-25", 501);
        var hidden = await Sku(data, pantry.Id, "Barang khusus", "PRIVATE-1", 502);

        var items = await catalog.BuyerService.ReadBuyerSkusAsync(catalog.Buyer, [hidden, tea, data.SkuId, 987654321]);
        Assert.Equal(new[] { data.SkuId, tea }.Order(), items.Select(item => item.Variant.Sku.Id.Value));
        Assert.Equal("TEA-25", Assert.Single(items, item => item.Variant.Sku.Id.Value == tea).Variant.Sku.Code);
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => catalog.BuyerService.ReadBuyerSkusAsync(catalog.Buyer, [tea, tea]));
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => catalog.BuyerService.ReadBuyerSkusAsync(catalog.Buyer, [.. Enumerable.Range(1, 101).Select(id => (long)id)]));
    }

    [Fact]
    public async Task An_invalid_category_filter_is_refused()
    {
        await using var data = await Ready();
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() =>
            data.Catalog.BuyerService.QueryBuyerAsync(data.Catalog.Buyer, 0, 25, null, category: "not a code!"));
    }

    private async Task<CatalogPriceTestData> Ready()
    {
        var data = await CatalogPriceTestData.CreateAsync(fixture);
        try {
            await data.Catalog.AssortmentsAsync(); await Publish(data, data.SkuId, 501, "browse-visible-publication");
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    private static async Task<long> Sku(CatalogPriceTestData data, UnitToko.Modules.Contracts.LongId categoryId, string family, string code, long assortmentId)
    {
        var created = await data.Catalog.Service.CreateFamilyAsync(data.Maker, new(Shop, categoryId, family, "Deskripsi", []), Metadata("family-" + code));
        var sku = await data.Catalog.Service.CreateSkuAsync(data.Maker, new(created.Id, created.Version, code, code.ToLowerInvariant(), Definition()), Metadata("sku-" + code));
        await Publish(data, sku.Id.Value, assortmentId, "publish-" + code);
        return sku.Id.Value;
    }
    private static async Task Publish(CatalogPriceTestData data, long skuId, long assortmentId, string key)
    {
        var sku = await data.Catalog.RowAsync("skus", skuId); var assortment = await data.Catalog.RowAsync("catalogAssortments", assortmentId);
        await data.Catalog.Service.SetPublicationsAsync(data.Maker, new(new(skuId), sku["revision"].AsInt64,
            [new(assortmentId)], [new(new(assortmentId), assortment["version"].AsInt64)]), Metadata(key));
    }
}
