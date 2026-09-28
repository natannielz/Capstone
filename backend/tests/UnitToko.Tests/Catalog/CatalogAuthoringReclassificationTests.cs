using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

public sealed partial class CatalogAuthoringServiceTests
{
    [Fact]
    public async Task Same_owner_family_reclassification_preserves_existing_sku_and_original_category_history_and_replay()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); var familyId = sku.FamilyId!.Value;
        var originalFamily = await data.Service.FamilyAsync(data.Maker, familyId.Value, Audit);
        var originalReceipt = await data.Service.ReadOperationReceiptAsync(data.Maker, "catalog.family.create", "family-ATK", RecoveryAudit);
        var category = await data.CategoryAsync(code: "NEW");
        var result = await data.Service.EditFamilyAsync(data.Maker, new(familyId, 1, category.Id, "Reclassified", "Updated", []), Metadata("reclassify"));
        Assert.Equal(2, result.Version);
        var family = await data.Service.FamilyAsync(data.Maker, familyId.Value, Audit);
        Assert.Equal(category.Id, family.CategoryId); Assert.Equal("NEW", family.CategoryCode); Assert.Equal(Shop, family.AuthoringOwner);
        var currentSku = await data.Service.SkuAsync(data.Maker, sku.Id.Value, Audit);
        Assert.Equal(Shop, currentSku.AuthoringOwner); Assert.Equal(1, currentSku.Sku.Revision); Assert.Equal(sku.CurrentDefinitionId, currentSku.Definition.Id);
        var audit = await data.Rows("auditEvents").Find(new BsonDocument("operation", "catalog.family.edit")).SingleAsync();
        Assert.Equal(originalFamily.CategoryId.Value, audit["before"]["category"]["_id"].AsInt64);
        Assert.Equal(category.Id.Value, audit["after"]["category"]["_id"].AsInt64);
        Assert.Equal(originalReceipt, await data.Service.CreateFamilyAsync(data.Maker, new(Shop, originalFamily.CategoryId, "Pensil", "Deskripsi", []), Metadata("family-ATK")));
        Assert.Equal(result, await data.Service.ReadOperationReceiptAsync(data.Maker, "catalog.family.edit", "reclassify", RecoveryAudit));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Family_reclassification_cannot_transfer_ownership_even_with_both_owner_grants(bool bothGrants)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var family = await data.FamilyAsync(); var foreign = await data.CategoryAsync(data.Other, OtherOwner, "FOREIGN");
        if (bothGrants) await data.Base.AddGrantAsync(data.Maker, 3009, OtherOwner, [Permissions.CatalogManage]);
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.EditFamilyAsync(data.Maker,
            new(family.Id, 1, foreign.Id, "Transfer", "", []), Metadata("transfer")));
        Assert.Equal(before, await data.StateAsync());
    }
}
