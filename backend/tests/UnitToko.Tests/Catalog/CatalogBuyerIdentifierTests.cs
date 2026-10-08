using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogBuyerIdentifierTests(MongoFixture fixture)
{
    [Fact]
    public async Task Correction_changes_buyer_primary_but_keeps_stable_identity_and_active_alias_until_retirement()
    {
        await using var data = await Ready(); var catalog = data.Catalog;
        var initial = Assert.Single((await catalog.BuyerService.QueryBuyerAsync(catalog.Buyer, 0, 25, "PENCIL-12")).Items);
        var stable = initial.Variant.Sku; var oldPrimary = initial.BuyerIdentity.CurrentPrimary;
        var corrected = await catalog.Service.CorrectSkuCodeAsync(data.Maker,
            new(stable.Id, initial.BuyerIdentity.SkuRevision, oldPrimary.IdentifierId, oldPrimary.IdentifierRevision,
                "CURRENT-PENCIL", "Explicit corrected catalog spelling; original package identity preserved"), Metadata("buyer-correct-primary"));
        foreach (var code in new[] { "PENCIL-12", "CURRENT-PENCIL" }) {
            var page = await catalog.BuyerService.QueryBuyerAsync(catalog.Buyer, 0, 25, code);
            var visible = Assert.Single(page.Items); Assert.Equal(1L, page.Total);
            Assert.Equal("PENCIL-12", visible.Variant.Sku.Code); Assert.Equal(stable.Id, visible.Variant.Sku.Id);
            Assert.Equal(stable.PublicId, visible.Variant.Sku.PublicId); Assert.Equal(stable.CurrentDefinitionId, visible.Variant.Definition.Id);
            Assert.Equal("CURRENT-PENCIL", visible.BuyerIdentity.CurrentPrimary.Code);
            Assert.Equal(corrected.IdentifierId, visible.BuyerIdentity.CurrentPrimary.IdentifierId);
            var exact = Assert.Single(await catalog.BuyerService.ReadExactBuyerIdentifiersAsync(catalog.Buyer, [code]));
            Assert.Equal(code, exact.RequestedCode); Assert.Equal(code, exact.MatchedIdentifier.Code);
            Assert.Equal(stable.Id, exact.MatchedIdentifier.SkuId); Assert.Equal(visible.BuyerIdentity.SkuRevision, exact.MatchedIdentifier.SkuRevision);
            Assert.Equal(stable.CurrentDefinitionId, exact.MatchedIdentifier.DefinitionId);
            Assert.Equal(code == "PENCIL-12" ? oldPrimary.IdentifierId : corrected.IdentifierId, exact.MatchedIdentifier.IdentifierId);
            Assert.Equal("PENCIL-12", exact.Variant.Variant.Sku.Code);
        }
        var detail = await catalog.BuyerService.ReadBuyerPublicAsync(catalog.Buyer, stable.PublicId.ToString("D"));
        Assert.NotNull(detail); Assert.Equal("CURRENT-PENCIL", detail.BuyerIdentity.CurrentPrimary.Code);
        var legacyOwner = await catalog.BuyerService.ReadPublicAsync(catalog.Buyer, stable.PublicId.ToString("D"));
        Assert.NotNull(legacyOwner); Assert.Equal("PENCIL-12", legacyOwner.Sku.Code);
        await catalog.Service.RetireIdentifierAsync(data.Maker, new(stable.Id, corrected.SkuRevision,
            oldPrimary.IdentifierId, oldPrimary.IdentifierRevision + 1, "Old alias no longer accepted in new buyer searches"), Metadata("buyer-retire-alias"));
        Assert.Empty((await catalog.BuyerService.QueryBuyerAsync(catalog.Buyer, 0, 25, "PENCIL-12")).Items);
        await Denied(catalog, ["PENCIL-12"]);
        Assert.Equal(stable.Id, Assert.Single(await catalog.BuyerService.ReadExactBuyerIdentifiersAsync(catalog.Buyer, ["CURRENT-PENCIL"])).Variant.Variant.Sku.Id);
        Assert.Equal("PENCIL-12", (await catalog.RowAsync("skus", stable.Id.Value))["sku"].AsString);
    }

    [Fact]
    public async Task Two_aliases_for_one_sku_and_any_unentitled_member_refuse_the_whole_batch_without_business_changes()
    {
        await using var data = await Ready(); var catalog = data.Catalog; var row = await catalog.RowAsync("skus", data.SkuId);
        var alias = await catalog.Service.AssignIdentifierAsync(data.Maker, new(new(data.SkuId), row["revision"].AsInt64,
            new(row["currentDefinitionId"].AsInt64), "skuCode", null, "ACTIVE-ALIAS", "Explicit supplier-independent internal alias"), Metadata("buyer-add-alias"));
        var hidden = await catalog.SkuAsync(code: "PRIVATE-PACKAGE", categoryCode: "PRIVATE");
        await Publish(data, hidden.Id.Value, 502, "buyer-hidden-publication");
        var before = await Business(data);
        foreach (var codes in new[] { ImmutableArray.Create("PENCIL-12", "ACTIVE-ALIAS"), ImmutableArray.Create("ACTIVE-ALIAS", "PENCIL-12"),
            ImmutableArray.Create("PENCIL-12", "PRIVATE-PACKAGE"), ImmutableArray.Create("PRIVATE-PACKAGE", "ACTIVE-ALIAS") }) {
            await Denied(catalog, codes); Assert.Equal(before, await Business(data));
        }
        var match = Assert.Single(await catalog.BuyerService.ReadExactBuyerIdentifiersAsync(catalog.Buyer, [" active-alias "]));
        Assert.Equal("ACTIVE-ALIAS", match.RequestedCode); Assert.Equal(alias.IdentifierId, match.MatchedIdentifier.IdentifierId);
        Assert.Equal("PENCIL-12", match.Variant.BuyerIdentity.CurrentPrimary.Code);
        Assert.Empty((await catalog.BuyerService.QueryBuyerAsync(catalog.Buyer, 0, 25, "PRIVATE-PACKAGE")).Items);
    }

    [Fact]
    public async Task Label_only_definition_revision_retains_exact_identifier_binding_but_material_change_cannot_reinterpret_it()
    {
        await using var data = await Ready(); var catalog = data.Catalog; var row = await catalog.RowAsync("skus", data.SkuId);
        var assigned = await catalog.Service.AssignIdentifierAsync(data.Maker, new(new(data.SkuId), row["revision"].AsInt64,
            new(row["currentDefinitionId"].AsInt64), "skuCode", null, "EXPLICIT-PACKAGE", "Reviewed exact twelve-piece package label"), Metadata("buyer-package-alias"));
        var revised = await catalog.Service.ReviseDefinitionAsync(data.Maker, new(new(data.SkuId), assigned.SkuRevision,
            Definition(label: "Dus dengan label terbaru")), Metadata("buyer-package-label"));
        var match = Assert.Single(await catalog.BuyerService.ReadExactBuyerIdentifiersAsync(catalog.Buyer, ["EXPLICIT-PACKAGE"]));
        Assert.Equal(assigned.IdentifierId, match.MatchedIdentifier.IdentifierId);
        Assert.Equal(revised.CurrentDefinitionId, match.MatchedIdentifier.DefinitionId); Assert.Equal(2, match.MatchedIdentifier.DefinitionVersion);
        Assert.Equal(12, match.Variant.Variant.Definition.BaseUnitsPerPackage); Assert.Equal("PENCIL-12", match.Variant.Variant.Sku.Code);
        await Assert.ThrowsAsync<DomainValidationException>(() => catalog.Service.ReviseDefinitionAsync(data.Maker,
            new(new(data.SkuId), revised.Version, Definition(contents: 24)), Metadata("cannot-reinterpret-physical-package")));
        // Corruption cannot bypass the owner rule merely by making the current definition syntactically valid.
        await catalog.Rows("variantDefinitions").UpdateOneAsync(new BsonDocument("_id", revised.CurrentDefinitionId!.Value.Value),
            Builders<BsonDocument>.Update.Set("baseUnitsPerPackage", 24));
        var before = await Business(data);
        await Assert.ThrowsAsync<DomainValidationException>(() => catalog.BuyerService.ReadExactBuyerIdentifiersAsync(catalog.Buyer, ["EXPLICIT-PACKAGE"]));
        Assert.Equal(before, await Business(data));
    }

    [Fact]
    public async Task Gtin_is_not_an_internal_buyer_alias_and_missing_identifier_provenance_never_becomes_a_buyer_projection()
    {
        await using var data = await Ready(); var catalog = data.Catalog; var row = await catalog.RowAsync("skus", data.SkuId);
        await catalog.Service.AssignIdentifierAsync(data.Maker, new(new(data.SkuId), row["revision"].AsInt64,
            new(row["currentDefinitionId"].AsInt64), "gtin", null, "4006381333931", "Manufacturer label used only by explicit operational GTIN resolver"), Metadata("buyer-private-gtin"));
        await Denied(catalog, ["4006381333931"]);
        Assert.Empty((await catalog.BuyerService.QueryBuyerAsync(catalog.Buyer, 0, 25, "4006381333931")).Items);
        var original = Assert.Single(await catalog.BuyerService.ReadExactBuyerIdentifiersAsync(catalog.Buyer, ["PENCIL-12"]));
        await catalog.Rows("catalogIdentifierHistory").DeleteOneAsync(new BsonDocument("identifierId", original.MatchedIdentifier.IdentifierId.Value));
        var before = await Business(data);
        await Assert.ThrowsAsync<DomainValidationException>(() => catalog.BuyerService.ReadExactBuyerIdentifiersAsync(catalog.Buyer, ["PENCIL-12"]));
        await Assert.ThrowsAsync<DomainValidationException>(() => catalog.BuyerService.ReadBuyerPublicAsync(catalog.Buyer, original.Variant.Variant.Sku.PublicId.ToString("D")));
        Assert.Equal(before, await Business(data));
    }

    private async Task<CatalogPriceTestData> Ready()
    {
        var data = await CatalogPriceTestData.CreateAsync(fixture);
        try {
            await data.Catalog.AssortmentsAsync(); await Publish(data, data.SkuId, 501, "buyer-visible-publication");
            await data.ApproveAsync(await data.ProposeAsync()); return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    private static async Task Publish(CatalogPriceTestData data, long skuId, long assortmentId, string key)
    {
        var sku = await data.Catalog.RowAsync("skus", skuId); var assortment = await data.Catalog.RowAsync("catalogAssortments", assortmentId);
        await data.Catalog.Service.SetPublicationsAsync(data.Maker, new(new(skuId), sku["revision"].AsInt64,
            [new(assortmentId)], [new(new(assortmentId), assortment["version"].AsInt64)]), Metadata(key));
    }
    private static async Task Denied(CatalogAuthoringTestData data, ImmutableArray<string> codes)
    {
        var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.BuyerService.ReadExactBuyerIdentifiersAsync(data.Buyer, codes));
        Assert.Equal("order_package_unavailable", error.Code);
    }
    private static async Task<BsonDocument> Business(CatalogPriceTestData data)
    {
        var state = await data.StateAsync();
        foreach (var name in new[] { "catalogIdentifiers", "catalogIdentifierHistory" })
            state[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        foreach (var collection in state) foreach (var value in collection.Value.AsBsonArray) value.AsBsonDocument.Remove("fence");
        return state;
    }
}
