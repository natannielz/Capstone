using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderQuoteServiceTests(MongoFixture fixture)
{
    private static readonly OrderBuyingContext Branch = new(new(10), new(11));
    private static OrderQuoteService Service(CatalogPriceTestData data, MongoClient? client = null) {
        var selected = client ?? data.Catalog.Client;
        return new(selected.GetDatabase(data.Catalog.Database.DatabaseNamespace.DatabaseName), new(selected), data.Catalog.Clock);
    }
    private static async Task<ImmutableArray<OrderPackageSelection>> Prepare(CatalogPriceTestData data) {
        await data.Catalog.Base.AddGrantAsync(data.Catalog.Buyer, 6200, CatalogAuthoringTestData.Audience, [Permissions.OrderSubmit]);
        await data.Catalog.AssortmentsAsync();
        await data.Catalog.Service.SetPublicationsAsync(data.Maker, new(new(data.SkuId), 1, [new(501)], [new(new(501), 1)]), CatalogPriceTestData.Meta("publish"));
        await data.ApproveAsync(await data.ProposeAsync());
        var item = Assert.Single((await data.Catalog.BuyerService.QueryAsync(data.Catalog.Buyer, 0, 25, null)).Items);
        return [new(item.Sku.Id, item.Definition.Id, item.Definition.Version, item.CurrentPrice!.Id, item.CurrentPrice.Version, 2)];
    }
    [Fact]
    public async Task Approved_branch_and_explicit_package_produce_exact_preview_without_business_effects() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var selected = await Prepare(data);
        var service = Service(data); var buyer = data.Catalog.Buyer;
        Assert.Equal(Branch, Assert.Single(await service.ContextsAsync(buyer)).Context);
        var result = await service.PreviewAsync(buyer, Branch, selected);
        Assert.Equal(Branch, result.Context); Assert.Equal("IDR", result.Catalog.Currency); Assert.Equal(24000.50m, result.Catalog.CatalogSubtotal.Value);
        var line = Assert.Single(result.Catalog.Lines); Assert.Equal(24L, line.BaseUnits); Assert.Equal(2, line.Packages);
        Assert.Equal(selected[0].DefinitionId, line.Definition.Id); Assert.Equal(selected[0].PriceId, line.Price.Id);
        foreach (var collection in new[] { "orders", "inventoryMovements", "journalEntries" }) Assert.Equal(0, await data.Catalog.CountAsync(collection));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", new BsonDocument("$regex", "^order"))));
    }
    [Fact]
    public async Task Reviewed_channel_groups_conserve_genuine_catalog_selections_and_refuse_foreign_buyer_scope_without_order_effects() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var selected = await Prepare(data);
        var catalog = data.Catalog; var owner = CatalogAuthoringTestData.OtherOwner;
        await catalog.Base.AddGrantAsync(data.Maker, 7300, owner, [Permissions.CatalogManage, Permissions.PricingManage]);
        await catalog.Base.AddGrantAsync(data.Checker, 7400, owner, [Permissions.PricingApprove]);
        var family = await catalog.FamilyAsync(data.Maker, owner, "GROUP-OTHER");
        var sku = await catalog.Service.CreateSkuAsync(data.Maker, new(family.Id, family.Version, "GROUP-OTHER-10", "group-other-base",
            CatalogAuthoringTestData.Definition(10)), CatalogPriceTestData.Meta("group-other-sku"));
        await catalog.Service.SetPublicationsAsync(data.Maker, new(sku.Id, 1, [new(503)], [new(new(503), 1)]), CatalogPriceTestData.Meta("group-other-publish"));
        // Existing fixture entitlement metadata scopes the synthetic buyer. Catalog and price
        // business rows above/below are produced by their real independent commands.
        await data.Rows("catalogEntitlements").InsertOneAsync(CatalogVisibilityBson.Entitlement(new CatalogEntitlement(10003,
            catalog.Buyer.UserId, catalog.Buyer.UserId, 503, CatalogAuthoringTestData.Audience, AuthorizationStatus.Active,
            catalog.Clock.UtcNow, null, null, 1, 998, 999, 100603, 1, catalog.Clock.UtcNow)));
        var pricePreview = await data.Service.PreviewAsync(data.Maker, sku.Id.Value, CatalogPriceAccess.Prepare);
        var price = await data.Service.ProposeAsync(data.Maker, sku.Id.Value, pricePreview.Expected, new("IDR", new(10000m)), CatalogPriceTestData.Meta("group-other-price"));
        await data.Service.DecideAsync(data.Checker, price["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve,
            "Independent synthetic group price review", CatalogPriceTestData.Meta("group-other-price-approve"));
        var other = (await catalog.BuyerService.QueryAsync(catalog.Buyer, 0, 25, null)).Items.Single(item => item.Sku.Id == sku.Id);
        var input = new OrderDraftInput(Branch, [new(other.Sku.Id, other.Definition.Id, other.Definition.Version,
            other.CurrentPrice!.Id, other.CurrentPrice.Version, 3), selected[0]],
            new("Penerima fixture", "081234567890", "Alamat cabang fixture", "Jakarta", "12810", null), "Separate channel fixture");
        var service = Service(data); var groups = await service.CheckoutGroupsAsync(catalog.Buyer, input);
        Assert.Equal(2, groups.Items.Length); Assert.Equal(Branch, groups.Context);
        Assert.Equal(new[] { "100:101:103:IDR", "100:201:203:IDR" }, groups.Items.Select(group => group.GroupKey));
        Assert.Equal(input.Selections.OrderBy(line => line.SkuId.Value), groups.Items.SelectMany(group => group.Input.Selections).OrderBy(line => line.SkuId.Value));
        Assert.Equal(54L, groups.Items.SelectMany(group => group.Catalog.Lines).Sum(line => line.BaseUnits));
        Assert.Equal(54000.50m, groups.Items.Sum(group => group.Catalog.CatalogSubtotal.Value));
        Assert.All(groups.Items, group => {
            Assert.Equal(input.Context, group.Input.Context); Assert.Equal(input.Delivery, group.Input.Delivery);
            Assert.Equal(input.PurchaseReference, group.Input.PurchaseReference);
            Assert.Equal(group.Input.Selections.Select(line => line.SkuId), group.Catalog.Lines.Select(line => line.Definition.SkuId));
            Assert.All(group.Catalog.Lines, line => {
                Assert.Equal(group.Seller.OrganizationId, line.FulfillmentOwner.OrganizationId);
                Assert.Equal(group.Seller.ShopId, line.FulfillmentOwner.ShopId); Assert.Equal(group.Seller.ChannelId, line.FulfillmentOwner.ChannelId);
            });
        });
        await catalog.Base.AddGrantAsync(catalog.Buyer, 6201, new(GrantScopeKind.BuyerOwned, 10, 12), [Permissions.OrderSubmit, Permissions.CatalogRead]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.CheckoutGroupsAsync(catalog.Buyer, input with { Context = new(new(10), new(12)) }));
        await data.Rows("catalogEntitlements").UpdateOneAsync(new BsonDocument("_id", 10003L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.CheckoutGroupsAsync(catalog.Buyer, input));
        foreach (var collection in new[] { "orders", "movements", "stockBalances", "journalEntries" }) Assert.Equal(0, await catalog.CountAsync(collection));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", new BsonDocument("$regex", "^ordering\\.order\\."))));
    }
    [Fact]
    public async Task Catalogue_membership_is_not_order_permission() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var selected = await Prepare(data);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 6200L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service(data).ContextsAsync(data.Catalog.Buyer));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service(data).PreviewAsync(data.Catalog.Buyer, Branch, selected));
    }
    [Fact]
    public async Task One_branch_cannot_borrow_another_branch_catalog_entitlement_even_with_both_order_permissions() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var selected = await Prepare(data);
        await data.Catalog.Base.AddGrantAsync(data.Catalog.Buyer, 6201, new(GrantScopeKind.BuyerOwned, 10, 12), [Permissions.OrderSubmit, Permissions.CatalogRead]);
        Assert.Equal(2, (await Service(data).ContextsAsync(data.Catalog.Buyer)).Length);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service(data).PreviewAsync(data.Catalog.Buyer, new(new(10), new(12)), selected));
        Assert.Equal(Branch, (await Service(data).PreviewAsync(data.Catalog.Buyer, Branch, selected)).Context);
    }
    [Theory]
    [InlineData("scope")] [InlineData("entitlement")] [InlineData("unpublished")]
    [InlineData("price")] [InlineData("definition")] [InlineData("quantity")] [InlineData("duplicate")]
    public async Task Unavailable_or_changed_selection_is_never_silently_repriced(string change) {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var selected = await Prepare(data); var scope = Branch;
        if (change == "scope") scope = new(new(10), new(12));
        if (change == "entitlement") await data.Rows("catalogEntitlements").UpdateOneAsync(new BsonDocument("_id", 10001L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        if (change == "unpublished") await data.Catalog.Service.SetPublicationsAsync(data.Maker, new(new(data.SkuId), 2, [], [new(new(501), 2)]), CatalogPriceTestData.Meta("unpublish"));
        if (change == "price") await data.ApproveAsync(await data.ProposeAsync("second", "13000.00"), "second-approval");
        if (change == "definition") selected = [selected[0] with { DefinitionVersion = selected[0].DefinitionVersion + 1 }];
        if (change == "quantity") selected = [selected[0] with { Packages = 0 }];
        if (change == "duplicate") selected = [selected[0], selected[0]];
        var error = await Record.ExceptionAsync(() => Service(data).PreviewAsync(data.Catalog.Buyer, scope, selected));
        Assert.True(error is AuthorizationDeniedException or DomainValidationException, error?.ToString());
    }
    [Theory]
    [InlineData("price")] [InlineData("entitlement")] [InlineData("order-grant")] [InlineData("order-member")] [InlineData("session")]
    public async Task Concurrent_catalog_or_order_authority_changes_cannot_escape_final_fences(string change) {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var selected = await Prepare(data);
        var proposal = await data.ProposeAsync("next", "13000.00");
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = 0; var observed = 0; var reads = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (command.Command.GetValue("find", BsonNull.Value) == "catalogPriceHeads" && Interlocked.Increment(ref reads) == 1) requestId = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (requestId != 0 && command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                    entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Quote race did not complete");
                }
            });
        };
        var pending = Task.Run(() => Service(data, new(settings)).PreviewAsync(data.Catalog.Buyer, Branch, selected));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "price") await data.ApproveAsync(proposal, "approve-next");
            if (change == "entitlement") await data.Rows("catalogEntitlements").UpdateOneAsync(new BsonDocument("_id", 10001L), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "order-grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 6200L), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "order-member") await data.Rows("memberships").UpdateOneAsync(new BsonDocument("_id", 6200L), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "session") await data.Catalog.Sessions.RevokeAsync(data.Catalog.Issued[data.Catalog.Buyer.UserId].OpaqueToken, "Synthetic quote session revocation");
        } finally { release.Set(); }
        var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, observed); Assert.True(error is DomainValidationException or AuthorizationDeniedException, error?.ToString());
    }
}
