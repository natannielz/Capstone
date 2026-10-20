using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogPublicTests(MongoFixture fixture)
{
    private static async Task<(CatalogAuthoringTestData Data, CatalogPublicShowcaseService Service, string PublicId)> PublishedAsync(MongoFixture fixture)
    {
        var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        await data.AssortmentsAsync();
        var sku = await data.SkuAsync();
        var row = await data.RowAsync("skus", sku.Id.Value);
        await data.Service.SetPublicationsAsync(data.Maker, new(sku.Id, row["revision"].AsInt64, [new(501)], [new(new(501), 1)]),
            CatalogAuthoringTestData.Metadata("public-catalog-publication"));
        row = await data.RowAsync("skus", sku.Id.Value);
        return (data, new CatalogPublicShowcaseService(data.Database, new MongoUnitOfWork(data.Client), new(data.Database), data.Clock), row["publicId"].AsString);
    }

    [Fact]
    public async Task Nothing_is_public_until_an_independent_checker_approves_and_making_it_private_hides_it_again()
    {
        var (data, service, publicId) = await PublishedAsync(fixture);
        await using var _ = data;
        var maker = await data.ActorAsync(9801, CatalogAuthoringTestData.Shop, Permissions.CatalogPublicPropose);
        var checker = await data.ActorAsync(9802, CatalogAuthoringTestData.Shop, Permissions.CatalogPublicApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, checker.UserId);
        Assert.Equal(0, (await data.BuyerService.QueryPublicAsync(0, 24, null)).Total);
        Assert.Null(await data.BuyerService.ReadPublicItemAsync(publicId));

        Assert.Null(Assert.Single(await service.ListAsync(maker), item => item.AssortmentId == 501).OwnerName);
        var shop = await data.Rows("authorizationScopes").Find(new BsonDocument("_id", 101L)).SingleAsync();
        await data.Rows("organizationProfiles").InsertOneAsync(new BsonDocument {
            { "_id", 101L }, { "schemaVersion", 1 }, { "kind", shop["kind"] }, { "parentId", shop.GetValue("parentId", BsonNull.Value) },
            { "code", "TOKO-MELATI" }, { "normalizedCode", "TOKO-MELATI" }, { "name", "Toko Melati" }, { "version", 1L }, { "createdBy", 998L }, { "updatedBy", 999L },
            { "createdAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime }, { "approvedProposalId", 10101L } });
        var listed = await service.ListAsync(maker);
        Assert.Equal([501L, 502L], listed.Select(item => item.AssortmentId).Order());
        var first = Assert.Single(listed, item => item.AssortmentId == 501);
        Assert.Equal("Toko Melati", first.OwnerName);
        Assert.False(first.IsPublic); Assert.Equal(0, first.Revision); Assert.Equal(1, first.ListedProducts); Assert.Null(first.Pending);

        var proposal = await service.ProposeAsync(maker, 501, 0, true, CatalogAuthoringTestData.Metadata("public-501-open", "Tampilkan katalog pantry di halaman publik"));
        Assert.Equal(proposal, await service.ProposeAsync(maker, 501, 0, true, CatalogAuthoringTestData.Metadata("public-501-open", "Tampilkan katalog pantry di halaman publik")));
        Assert.Equal(0, (await data.BuyerService.QueryPublicAsync(0, 24, null)).Total);
        Assert.Equal(proposal["proposalId"].AsInt64, Assert.Single(await service.ListAsync(checker), item => item.AssortmentId == 501).Pending!.ProposalId);

        await service.DecideAsync(checker, proposal["proposalId"].AsInt64, 1, CatalogPublicShowcaseDecision.Approve,
            CatalogAuthoringTestData.Metadata("public-501-open-approve", "Diperiksa: tidak ada barang khusus cabang"));
        var page = await data.BuyerService.QueryPublicAsync(0, 24, null);
        var item = Assert.Single(page.Items);
        Assert.Equal(1, page.Total); Assert.Equal(publicId, item.PublicId); Assert.Equal("PENCIL-12", item.Code); Assert.Equal("Pensil", item.Name);
        Assert.Equal("Dus", item.PackageLabel); Assert.Equal(12, item.BaseUnitsPerPackage);
        Assert.Equal("ATK", Assert.Single(page.Categories).Code);
        Assert.Equal(item, await data.BuyerService.ReadPublicItemAsync(publicId));
        Assert.Equal(1, (await data.BuyerService.QueryPublicAsync(0, 24, "pensil")).Total);
        Assert.Equal(0, (await data.BuyerService.QueryPublicAsync(0, 24, "kopi")).Total);

        var close = await service.ProposeAsync(maker, 501, 1, false, CatalogAuthoringTestData.Metadata("public-501-close", "Katalog pantry ditarik dari halaman publik"));
        await service.DecideAsync(checker, close["proposalId"].AsInt64, 1, CatalogPublicShowcaseDecision.Approve,
            CatalogAuthoringTestData.Metadata("public-501-close-approve", "Disetujui ditarik"));
        Assert.Equal(0, (await data.BuyerService.QueryPublicAsync(0, 24, null)).Total);
        Assert.Null(await data.BuyerService.ReadPublicItemAsync(publicId));
        Assert.Equal(2, (await data.RowAsync("catalogPublicShowcaseHeads", 501))["revision"].AsInt64);
    }

    [Fact]
    public async Task The_proposer_cannot_approve_their_own_proposal_and_a_rejection_changes_nothing()
    {
        var (data, service, _) = await PublishedAsync(fixture);
        await using var __ = data;
        var both = await data.ActorAsync(9803, CatalogAuthoringTestData.Shop, Permissions.CatalogPublicPropose, Permissions.CatalogPublicApprove);
        var checker = await data.ActorAsync(9804, CatalogAuthoringTestData.Shop, Permissions.CatalogPublicApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, both.UserId, checker.UserId);
        var proposal = await service.ProposeAsync(both, 501, 0, true, CatalogAuthoringTestData.Metadata("public-self", "Tampilkan katalog"));
        await Assert.ThrowsAnyAsync<Exception>(() => service.DecideAsync(both, proposal["proposalId"].AsInt64, 1, CatalogPublicShowcaseDecision.Approve,
            CatalogAuthoringTestData.Metadata("public-self-approve", "Menyetujui sendiri")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ProposeAsync(both, 501, 0, true,
            CatalogAuthoringTestData.Metadata("public-second", "Usulan kedua")));
        await service.DecideAsync(checker, proposal["proposalId"].AsInt64, 1, CatalogPublicShowcaseDecision.Reject,
            CatalogAuthoringTestData.Metadata("public-self-reject", "Masih ada barang khusus cabang"));
        Assert.Equal(0, (await data.BuyerService.QueryPublicAsync(0, 24, null)).Total);
        Assert.Equal(0L, await data.CountAsync("catalogPublicShowcaseHeads"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListAsync(data.Buyer));
    }

    [Fact]
    public void The_public_item_carries_no_price_stock_or_internal_identity()
    {
        var names = typeof(PublicCatalogItem).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(names, name => name.Contains("Price", StringComparison.OrdinalIgnoreCase) || name.Contains("Stock", StringComparison.OrdinalIgnoreCase) ||
            name is "SkuId" or "Id" or "Owner" or "AssortmentId");
    }
}
