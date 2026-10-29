using UnitToko.Modules.Contracts;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

/// <summary>I2: a signed-in member following a guest product link (/katalog/&lt;product&gt; becomes /shop/&lt;product&gt;) lands on that
/// product's first package they may see. A product they cannot see answers exactly like an unknown id.</summary>
[Collection("Mongo")]
public sealed class CatalogBuyerProductLinkTests(MongoFixture fixture)
{
    [Fact]
    public async Task A_product_link_opens_its_first_package_the_member_may_see_and_hides_the_rest_alike()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var catalog = data.Catalog;
        await catalog.AssortmentsAsync();
        async Task Publish(long skuId, long assortmentId) {
            var sku = await catalog.RowAsync("skus", skuId); var assortment = await catalog.RowAsync("catalogAssortments", assortmentId);
            await catalog.Service.SetPublicationsAsync(catalog.Maker, new(new(skuId), sku["revision"].AsInt64, [new(assortmentId)],
                [new(new(assortmentId), assortment["version"].AsInt64)]), Metadata("publish-" + skuId + "-" + assortmentId));
        }
        async Task<long> Package(long familyId, string code, int contents) {
            var family = await catalog.RowAsync("productFamilies", familyId);
            return (await catalog.Service.CreateSkuAsync(catalog.Maker, new(new(familyId), family["revision"].AsInt64, code, "base-" + code.ToLowerInvariant(),
                Definition(contents, "Pak " + contents)), Metadata("sku-" + code))).Id.Value;
        }
        async Task<string> PublicId(string collection, long id) => (await catalog.RowAsync(collection, id))["publicId"].AsString;

        // A product whose first package is published only to an assortment the buyer is not entitled to, and whose second is visible.
        var pencilFamily = (await catalog.RowAsync("skus", data.SkuId))["familyId"].AsInt64;
        await Publish(data.SkuId, 502);
        var visible = await Package(pencilFamily, "PENCIL-24", 24);
        await Publish(visible, 501);
        var link = await catalog.BuyerService.ReadBuyerPublicAsync(catalog.Buyer, await PublicId("productFamilies", pencilFamily));
        Assert.NotNull(link);
        Assert.Equal(await PublicId("skus", visible), link.Variant.Sku.PublicId.ToString("D"));
        // A package link keeps working exactly as before.
        Assert.Equal(link.Variant.Sku.Id, (await catalog.BuyerService.ReadBuyerPublicAsync(catalog.Buyer, await PublicId("skus", visible)))!.Variant.Sku.Id);

        // A product with nothing this buyer may see, and an unknown id, both answer null.
        var coffee = await catalog.FamilyAsync(code: "KOPI");
        var hidden = await Package(coffee.Id.Value, "KOPI-12", 12);
        await Publish(hidden, 502);
        Assert.Null(await catalog.BuyerService.ReadBuyerPublicAsync(catalog.Buyer, await PublicId("productFamilies", coffee.Id.Value)));
        Assert.Null(await catalog.BuyerService.ReadBuyerPublicAsync(catalog.Buyer, Guid.NewGuid().ToString("D")));
        Assert.Null(await catalog.BuyerService.ReadBuyerPublicAsync(catalog.Buyer, await PublicId("skus", hidden)));
        await Assert.ThrowsAsync<DomainValidationException>(() => catalog.BuyerService.ReadBuyerPublicAsync(catalog.Buyer, "not-a-uuid"));
    }
}
