using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderSubmissionServiceTests(MongoFixture fixture)
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Source_separated_suborders_and_two_physical_pools_commit_or_roll_back_together(bool secondSourceShort) {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); await data.AddSecondFulfillmentAsync(secondSourceShort ? 2 : 10);
        var review = await data.Service.PreviewAsync(data.Buyer, data.Input);
        Assert.Equal("55500.50", review.Total.ToString());
        if (secondSourceShort) {
            Assert.Equal("insufficient_available_stock", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.SubmitAsync(data.Buyer, data.Input, review.Review, CatalogPriceTestData.Meta("two-sources")))).Code);
            foreach (var name in new[] { "orders", "orderSuborders", "reservations", "movements" }) Assert.Equal(0, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
            Assert.All(await data.Rows("stockBalances").Find(new BsonDocument()).ToListAsync(), row => Assert.Equal(0L, row["reserved"].AsInt64));
        } else {
            var result = await data.Service.SubmitAsync(data.Buyer, data.Input, review.Review, CatalogPriceTestData.Meta("two-sources"));
            Assert.Equal(2, result["suborderIds"].AsBsonArray.Count);
            foreach (var name in new[] { "orderSuborders", "reservations", "movements" }) Assert.Equal(2, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
            var view = await data.Service.ReadOwnAsync(data.Buyer, result["orderId"].AsInt64);
            Assert.Equal(new long[] { 103, 203 }, view.Catalog.Lines.Select(line => line.FulfillmentOwner.ChannelId!.Value));
            Assert.Equal(new long[] { 24, 30 }, view.Catalog.Lines.Select(line => line.BaseUnits));
            Assert.Equal(new long[] { 2, 3 }, (await data.Rows("stockBalances").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Select(row => row["reserved"].AsInt64));
        }
    }
    [Fact]
    public async Task Submit_commits_reviewed_order_suborder_reservation_movement_audit_receipt_and_outbox_once() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        var review = await data.Service.PreviewAsync(data.Buyer, data.Input);
        Assert.Equal("25500.50", review.Total.ToString());
        var result = await data.Service.SubmitAsync(data.Buyer, data.Input, review.Review, CatalogPriceTestData.Meta("submit"));
        Assert.Equal("Pending", result["spendingState"].AsString);
        Assert.Equal(result, await data.Service.RecoverAsync(data.Buyer, "submit"));
        Assert.Equal(result, await data.Service.SubmitAsync(data.Buyer, data.Input, review.Review, CatalogPriceTestData.Meta("submit")));
        var order = await data.Service.ReadOwnAsync(data.Buyer, result["orderId"].AsInt64);
        Assert.Equal(data.Input.Delivery!.Address, order.Input.Delivery!.Address);
        Assert.Equal(order.Id, Assert.Single((await data.Service.ListOwnAsync(data.Buyer, data.Input.Context)).Items).Id);
        Assert.Empty((await data.Service.ListOwnAsync(data.Buyer, data.Input.Context, order.Id.Value)).Items);
        foreach (var name in new[] { "orders", "orderSuborders", "reservations", "movements" }) Assert.Equal(1, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal("awaitingSpending", (await data.Rows("orderSuborders").Find(new BsonDocument()).SingleAsync())["status"]);
        var balance = await data.Rows("stockBalances").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(10L, balance["onHand"].AsInt64); Assert.Equal(2L, balance["reserved"].AsInt64);
        Assert.Equal(1, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "ordering.order.submitted")));
        Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "ordering.order.submitted")));
        Assert.Equal(0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        await data.Prices.ApproveAsync(await data.Prices.ProposeAsync("new-price", "13000.00"), "new-price-approve");
        Assert.Equal(result, await data.Service.SubmitAsync(data.Buyer, data.Input, review.Review, CatalogPriceTestData.Meta("submit")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadOwnAsync(data.Prices.Maker, result["orderId"].AsInt64));
    }
    [Fact]
    public async Task Concurrent_last_stock_commands_have_one_winner_and_same_key_retries_have_one_effect() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture, 2);
        var other = await data.Prices.Catalog.ActorAsync(6001, CatalogAuthoringTestData.Audience, Permissions.CatalogRead, Permissions.OrderSubmit);
        await data.Rows("catalogEntitlements").InsertOneAsync(CatalogVisibilityBson.Entitlement(new CatalogEntitlement(10002, other.UserId, other.UserId, 501,
            CatalogAuthoringTestData.Audience, AuthorizationStatus.Active, data.Prices.Catalog.Clock.UtcNow, null, null, 1, 998, 999, 100602, 1, data.Prices.Catalog.Clock.UtcNow)));
        var review = await data.Service.PreviewAsync(data.Buyer, data.Input);
        var errors = await Task.WhenAll(new[] { (data.Buyer, "race-one"), (other, "race-two") }.Select(attempt => Record.ExceptionAsync(() =>
            data.Service.SubmitAsync(attempt.Item1, data.Input, review.Review, CatalogPriceTestData.Meta(attempt.Item2)))));
        Assert.Single(errors, error => error is null);
        var losingError = Assert.Single(errors, error => error is not null);
        Assert.True(losingError is DomainValidationException { Code: "insufficient_available_stock" } or PersistenceConflictException { Code: "version_conflict" or "stock_revision_conflict" }, losingError?.ToString());
        var row = await data.Rows("orders").Find(new BsonDocument()).SingleAsync(); var winner = row["requestKey"].AsString;
        var winnerActor = row["createdBy"].AsInt64 == other.UserId ? other : data.Buyer;
        var loserActor = winnerActor == other ? data.Buyer : other;
        Assert.Equal("insufficient_available_stock", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.SubmitAsync(loserActor, data.Input,
            review.Review, CatalogPriceTestData.Meta(loserActor == other ? "race-two" : "race-one")))).Code);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadOwnAsync(loserActor, row["_id"].AsInt64));
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => data.Service.SubmitAsync(winnerActor, data.Input, review.Review, CatalogPriceTestData.Meta(winner))));
        Assert.Equal(results[0], results[1]); Assert.Equal(1, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, (await data.Rows("stockBalances").Find(new BsonDocument()).SingleAsync())["reserved"].AsInt64);
    }
    [Theory]
    [InlineData("review")] [InlineData("price")] [InlineData("credit")] [InlineData("missing-policy")] [InlineData("revoked-policy")]
    [InlineData("grant")] [InlineData("outbox")] [InlineData("expired-final")]
    public async Task Failed_submission_rolls_back_all_order_and_inventory_effects(string fault) {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        var review = await data.Service.PreviewAsync(data.Buyer, data.Input); var expected = review.Review;
        if (fault == "review") expected = new('a', 64);
        if (fault == "price") await data.Prices.ApproveAsync(await data.Prices.ProposeAsync("changed", "14000.00"), "approve-changed");
        if (fault == "credit") await data.Rows("fixtureOrderApprovals").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("creditLimit", new BsonDecimal128(1m)));
        if (fault == "missing-policy") await data.Rows("fixtureOrderApprovals").DeleteManyAsync(new BsonDocument());
        if (fault == "revoked-policy") await data.Rows("fixtureOrderApprovals").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("approved", false));
        if (fault == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 6200L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        if (fault == "outbox") await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" }, { "validator", new BsonDocument("kind", new BsonDocument("$ne", "ordering.order.submitted")) } });
        if (fault == "expired-final") { var calls = 0; data.Admission.OnComplete = () => { if (++calls == 2) data.Prices.Catalog.Clock.UtcNow = data.Prices.Catalog.Clock.UtcNow.AddHours(2); }; }
        Assert.NotNull(await Record.ExceptionAsync(() => data.Service.SubmitAsync(data.Buyer, data.Input, expected, CatalogPriceTestData.Meta("failure"))));
        foreach (var name in new[] { "orders", "orderSuborders", "reservations", "movements" }) Assert.Equal(0, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, (await data.Rows("stockBalances").Find(new BsonDocument()).SingleAsync())["reserved"].AsInt64);
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", OrderSubmissionBson.Operation)));
    }
    [Theory]
    [InlineData("quantity")] [InlineData("owner")] [InlineData("action")] [InlineData("duplicate")] [InlineData("total")]
    public async Task A_malformed_trusted_adapter_cannot_redirect_or_expand_stock_or_money(string fault) {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        data.Admission.Transform = value => fault switch {
            "quantity" => value with { Stock = [value.Stock[0] with { Change = value.Stock[0].Change with { Packages = 1 } }] },
            "owner" => value with { Stock = [value.Stock[0] with { Pool = value.Stock[0].Pool with { Location = value.Stock[0].Pool.Location with { ChannelId = new(999) } } }] },
            "action" => value with { Stock = [value.Stock[0] with { Change = value.Stock[0].Change with { Action = UnitToko.Platform.Inventory.InventoryBalanceAction.Receive } }] },
            "duplicate" => value with { Stock = [value.Stock[0], value.Stock[0]] },
            "total" => value with { Terms = value.Terms with { Total = new(0m) } }, _ => value };
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.PreviewAsync(data.Buyer, data.Input));
        Assert.Equal(0, await data.Rows("orders").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Production_constructor_has_no_implicit_policy_or_stock_adapter() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        var service = new OrderSubmissionService(data.Database, new(data.Prices.Catalog.Client), new(data.Database), data.Prices.Catalog.Clock);
        Assert.Equal("order_admission_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => service.PreviewAsync(data.Buyer, data.Input))).Code);
    }
    [Theory]
    [InlineData("receipt")] [InlineData("audit")] [InlineData("snapshot")] [InlineData("reservation")] [InlineData("movement")] [InlineData("suborder")] [InlineData("extra-malformed-child")]
    public async Task Broken_original_history_cannot_replay_or_recreate_an_order(string fault) {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var service = data.Service;
        var review = await service.PreviewAsync(data.Buyer, data.Input);
        var result = await service.SubmitAsync(data.Buyer, data.Input, review.Review, CatalogPriceTestData.Meta("original"));
        if (fault == "receipt") await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", OrderSubmissionBson.Operation));
        if (fault == "audit") await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("kind", "ordering.order.submitted"));
        if (fault == "snapshot") await data.Rows("orders").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("terms.charges", new BsonDecimal128(500m)));
        if (fault == "reservation") await data.Rows("reservations").DeleteOneAsync(new BsonDocument());
        if (fault == "movement") await data.Rows("movements").DeleteOneAsync(new BsonDocument());
        if (fault == "suborder") await data.Rows("orderSuborders").DeleteOneAsync(new BsonDocument());
        if (fault == "extra-malformed-child") await data.Rows("orderSuborders").InsertOneAsync(new BsonDocument { { "_id", 99999L }, { "orderId", result["orderId"] } }, new InsertOneOptions { BypassDocumentValidation = true }); // Deliberate local corruption must not disappear behind a validator filter.
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadOwnAsync(data.Buyer, result["orderId"].AsInt64));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.SubmitAsync(data.Buyer, data.Input, review.Review, CatalogPriceTestData.Meta("original")));
        Assert.Equal(1, await data.Rows("orders").CountDocumentsAsync(new BsonDocument()));
    }
}
