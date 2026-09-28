using MongoDB.Bson;
using MongoDB.Driver;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

public sealed partial class CatalogAuthoringServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Buyer_count_excludes_missing_or_wrong_current_definition_before_paging(bool wrongSku)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var first = await data.SkuAsync(); var second = await data.SkuAsync(24, "PENCIL-24"); await data.AssortmentsAsync();
        await data.Service.SetPublicationsAsync(data.Maker, new(first.Id, 1, [new(501)], [new(new(501), 1)]), Metadata("first-list"));
        await data.Service.SetPublicationsAsync(data.Maker, new(second.Id, 1, [new(501)], [new(new(501), 2)]), Metadata("second-list"));
        if (wrongSku) await data.Rows("variantDefinitions").UpdateOneAsync(new BsonDocument("_id", second.CurrentDefinitionId!.Value.Value), U.Set("skuId", 999999L));
        else await data.Rows("skus").UpdateOneAsync(new BsonDocument("_id", second.Id.Value), U.Set("currentDefinitionId", 999999L));
        var page = await data.BuyerService.QueryAsync(data.Buyer, 0, 1, null);
        Assert.Equal(1, page.Total); Assert.Equal(first.Id, Assert.Single(page.Items).Sku.Id);
        Assert.Empty((await data.BuyerService.QueryAsync(data.Buyer, 1, 1, null)).Items);
    }
    [Fact]
    public async Task Buyer_list_keeps_valid_owner_fields_when_stored_document_order_differs()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await data.AssortmentsAsync();
        await data.Service.SetPublicationsAsync(data.Maker, new(sku.Id, 1, [new(501)], [new(new(501), 1)]), Metadata("ordered-list"));
        var family = await data.RowAsync("productFamilies", sku.FamilyId!.Value.Value);
        var reordered = new BsonDocument(family["authoringOwner"].AsBsonDocument.Elements.Reverse());
        await data.Rows("productFamilies").UpdateOneAsync(new BsonDocument("_id", family["_id"]), U.Set("authoringOwner", reordered));
        var page = await data.BuyerService.QueryAsync(data.Buyer, 0, 25, null);
        Assert.Equal(1, page.Total); Assert.Equal(sku.Id, Assert.Single(page.Items).Sku.Id);
    }
    [Fact]
    public async Task Publication_option_count_excludes_wrongly_typed_status_before_paging()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); await data.AssortmentsAsync();
        await data.Rows("catalogAssortments").UpdateOneAsync(new BsonDocument("_id", 502L), U.Set("status", new BsonArray { "active", "revoked" }), new UpdateOptions { BypassDocumentValidation = true });
        var page = await data.Service.AssortmentsAsync(data.Maker, Shop, 0, 1, Audit);
        Assert.Equal(1, page.Total); Assert.Equal(501, Assert.Single(page.Items).Id.Value);
    }
    [Fact]
    public async Task Authoring_read_rejects_oversized_raw_correlation_without_persisting_audit_or_fences()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); var before = await data.StateAsync();
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => data.Service.OwnerOptionsAsync(data.Maker,
            new("catalog-authoring", new string(' ', 500) + "x")));
        Assert.Equal(before, await data.StateAsync());
    }
}
