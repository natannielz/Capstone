using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderDraftServiceTests(MongoFixture fixture)
{
    private static OrderDraftService Service(CatalogPriceTestData data) => new(data.Catalog.Database, new(data.Catalog.Client), new(data.Catalog.Database), data.Catalog.Clock);
    private static async Task<OrderDraftInput> Prepare(CatalogPriceTestData data) {
        var lease = await new MigrationGate(data.Catalog.Database, data.Catalog.Clock, 21).AcquireAsync(21, default);
        if (lease is not null) { await new OrderDraftMigration(data.Catalog.Database).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        await data.Catalog.Base.AddGrantAsync(data.Catalog.Buyer, 6200, CatalogAuthoringTestData.Audience, [Permissions.OrderSubmit]);
        await data.Catalog.AssortmentsAsync();
        await data.Catalog.Service.SetPublicationsAsync(data.Maker, new(new(data.SkuId), 1, [new(501)], [new(new(501), 1)]), CatalogPriceTestData.Meta("draft-publish"));
        await data.ApproveAsync(await data.ProposeAsync());
        var item = Assert.Single((await data.Catalog.BuyerService.QueryAsync(data.Catalog.Buyer, 0, 25, null)).Items);
        return new(new(new(10), new(11)), [new(item.Sku.Id, item.Definition.Id, item.Definition.Version, item.CurrentPrice!.Id, item.CurrentPrice.Version, 2)],
            new("Penerima fixture", "+62 812-3456-7890", "Jalan Contoh 1\nLantai 2", "Jakarta", "12810", null), "PO fixture");
    }
    [Fact]
    public async Task Draft_save_edit_and_original_receipt_recovery_preserve_versions_without_orders_or_stock() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var input = await Prepare(data); var service = Service(data); var actor = data.Catalog.Buyer;
        var first = await service.SaveAsync(actor, null, 0, input, CatalogPriceTestData.Meta("draft-first")); var id = first["draftId"].AsInt64;
        var view = await service.ReadAsync(actor, id); Assert.Equal(input.Delivery, view.Input.Delivery); Assert.Equal(1L, view.Revision);
        Assert.Equal(id, Assert.Single((await service.ListAsync(actor, input.Context)).Items).Id.Value);
        Assert.Empty((await service.ListAsync(actor, input.Context, id)).Items);
        var changed = input with { Selections = [input.Selections[0] with { Packages = 3 }] };
        var second = await service.SaveAsync(actor, id, 1, changed, CatalogPriceTestData.Meta("draft-second")); Assert.Equal(2L, second["revision"].AsInt64);
        Assert.Equal(3, Assert.Single((await service.ReadAsync(actor, id)).Input.Selections).Packages);
        await data.ApproveAsync(await data.ProposeAsync("changed-price", "14000.00"), "approve-changed-price");
        Assert.Equal(first, await service.RecoverAsync(actor, "draft-first"));
        Assert.Equal(first, await service.SaveAsync(actor, null, 0, input, CatalogPriceTestData.Meta("draft-first")));
        Assert.Equal(2, await data.Catalog.CountAsync("orderDraftVersions")); Assert.Equal(1, await data.Catalog.CountAsync("orderDrafts"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.Maker, id));
        foreach (var name in new[] { "orders", "reservations", "movements", "journalEntries" }) Assert.Equal(0, await data.Catalog.CountAsync(name));
        Assert.Null(await service.RecoverAsync(actor, "never-submitted"));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 6200L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(actor, id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListAsync(actor, input.Context));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(actor, "draft-first"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(actor, "never-submitted"));
    }
    [Fact]
    public async Task Draft_races_replay_one_create_and_reject_stale_edits_without_losing_the_winner() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var input = await Prepare(data); var service = Service(data); var actor = data.Catalog.Buyer;
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => service.SaveAsync(actor, null, 0, input, CatalogPriceTestData.Meta("same-key"))));
        Assert.Equal(results[0], results[1]); var id = results[0]["draftId"].AsInt64;
        var attempts = await Task.WhenAll(new[] { 3, 4 }.Select(quantity => Record.ExceptionAsync(() => service.SaveAsync(actor, id, 1,
            input with { Selections = [input.Selections[0] with { Packages = quantity }] }, CatalogPriceTestData.Meta("edit-" + quantity)))));
        Assert.Single(attempts, error => error is null); Assert.IsType<PersistenceConflictException>(Assert.Single(attempts, error => error is not null));
        Assert.Equal(2L, (await service.ReadAsync(actor, id)).Revision); Assert.Equal(2, await data.Catalog.CountAsync("orderDraftVersions"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.SaveAsync(actor, null, 0, input with { PurchaseReference = "changed" }, CatalogPriceTestData.Meta("same-key")));
    }
    [Theory]
    [InlineData("price")] [InlineData("entitlement")] [InlineData("grant")]
    public async Task Invalid_current_selection_or_authority_rolls_back_every_draft_effect(string change) {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var input = await Prepare(data); var service = Service(data); var actor = data.Catalog.Buyer;
        if (change == "price") await data.ApproveAsync(await data.ProposeAsync("new-price", "13000.00"), "approve-new");
        if (change == "entitlement") await data.Rows("catalogEntitlements").UpdateOneAsync(new BsonDocument("_id", 10001L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        if (change == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 6200L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        var denied = await Record.ExceptionAsync(() => service.SaveAsync(actor, null, 0, input, CatalogPriceTestData.Meta("denied-save")));
        if (change == "price") Assert.Equal("order_selection_changed", Assert.IsType<UnitToko.Modules.Contracts.DomainValidationException>(denied).Code);
        else Assert.IsType<AuthorizationDeniedException>(denied);
        Assert.Equal(0, await data.Catalog.CountAsync("orderDrafts")); Assert.Equal(0, await data.Catalog.CountAsync("orderDraftVersions"));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "ordering.draft.save")));
        Assert.Equal(0, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "ordering.draft.saved")));
        Assert.Equal(0, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "ordering.draft.saved")));
    }
    [Theory]
    [InlineData("audit")] [InlineData("receipt")] [InlineData("input")]
    public async Task Draft_history_requires_its_original_receipt_and_untampered_content(string fault) {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var input = await Prepare(data); var service = Service(data); var actor = data.Catalog.Buyer;
        var result = await service.SaveAsync(actor, null, 0, input, CatalogPriceTestData.Meta("history"));
        if (fault == "audit") await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("kind", "ordering.draft.saved"));
        if (fault == "receipt") await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "ordering.draft.save"));
        if (fault == "input") await data.Rows("orderDraftVersions").UpdateOneAsync(new BsonDocument("_id", result["versionId"]), Builders<BsonDocument>.Update.Set("input.purchaseReference", "tampered"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(actor, result["draftId"].AsInt64));
        if (fault != "receipt") await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(actor, "history"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.SaveAsync(actor, null, 0, input, CatalogPriceTestData.Meta("history")));
        Assert.Equal(1, await data.Catalog.CountAsync("orderDrafts")); Assert.Equal(1, await data.Catalog.CountAsync("orderDraftVersions"));
    }
}
