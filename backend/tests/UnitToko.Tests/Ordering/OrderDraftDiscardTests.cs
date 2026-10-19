using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderDraftDiscardTests(MongoFixture fixture)
{
    // Simulates time passing for one discard: the row, its audit event and its receipt shift together, as a real 31-day gap would look.
    internal static async Task AgeDiscardAsync(CatalogPriceTestData data, long draftId, TimeSpan by) {
        var row = await data.Rows("orderDraftDiscards").Find(new BsonDocument("_id", draftId)).SingleAsync();
        var discardedAt = row["discardedAt"].ToUniversalTime() - by; var until = discardedAt.AddDays(30);
        await data.Rows("orderDraftDiscards").UpdateOneAsync(new BsonDocument("_id", draftId),
            Builders<BsonDocument>.Update.Set("discardedAt", discardedAt).Set("restorableUntil", until));
        await data.Rows("auditEvents").UpdateOneAsync(new BsonDocument("_id", row["auditId"]),
            Builders<BsonDocument>.Update.Set("createdAt", discardedAt).Set("result.discardedAt", discardedAt).Set("result.restorableUntil", until));
        await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument { { "actorId", row["discardedBy"] }, { "operation", "ordering.draft.discard" }, { "key", row["requestKey"] } },
            Builders<BsonDocument>.Update.Set("result.discardedAt", discardedAt).Set("result.restorableUntil", until));
    }
    private static string Code(Exception? error) => Assert.IsType<PersistenceConflictException>(error).Code;

    [Fact]
    public async Task Discard_hides_the_draft_restore_brings_it_back_and_both_are_audited() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var input = await OrderDraftServiceTests.Prepare(data);
        var service = OrderDraftServiceTests.Service(data); var actor = data.Catalog.Buyer;
        var id = (await service.SaveAsync(actor, null, 0, input, CatalogPriceTestData.Meta("draft-1")))["draftId"].AsInt64;
        var discarded = await service.DiscardAsync(actor, id, 1, CatalogPriceTestData.Meta("discard-1"));
        Assert.Equal(discarded["discardedAt"].ToUniversalTime().AddDays(30), discarded["restorableUntil"].ToUniversalTime());
        Assert.Empty((await service.ListAsync(actor, input.Context)).Items);
        var hidden = Assert.Single((await service.ListDiscardedAsync(actor, input.Context)).Items);
        Assert.Equal((id, 1L, input.Delivery), (hidden.Id.Value, hidden.Revision, hidden.Input.Delivery));
        Assert.Equal(hidden.DiscardedAt.AddDays(30), hidden.RestorableUntil);
        Assert.Equal("order_draft_discarded", Code(await Record.ExceptionAsync(() => service.ReadAsync(actor, id))));
        await service.RestoreAsync(actor, id, CatalogPriceTestData.Meta("restore-1"));
        Assert.Equal(id, Assert.Single((await service.ListAsync(actor, input.Context)).Items).Id.Value);
        Assert.Empty((await service.ListDiscardedAsync(actor, input.Context)).Items);
        Assert.Equal(2L, (await service.SaveAsync(actor, id, 1, input with { PurchaseReference = "PO setelah pulih" }, CatalogPriceTestData.Meta("draft-2")))["revision"].AsInt64);
        foreach (var kind in new[] { "ordering.draft.discarded", "ordering.draft.restored" })
            Assert.Equal(1, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", kind)));
        Assert.Equal(0, await data.Catalog.CountAsync("orderDraftDiscards"));
    }

    [Fact]
    public async Task Save_after_discard_is_refused_and_a_racing_save_never_lands_on_a_discarded_draft() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var input = await OrderDraftServiceTests.Prepare(data);
        var service = OrderDraftServiceTests.Service(data); var actor = data.Catalog.Buyer;
        var changed = input with { PurchaseReference = "PO berubah" };
        var id = (await service.SaveAsync(actor, null, 0, input, CatalogPriceTestData.Meta("draft-1")))["draftId"].AsInt64;
        await service.DiscardAsync(actor, id, 1, CatalogPriceTestData.Meta("discard-1"));
        Assert.Equal("order_draft_discarded", Code(await Record.ExceptionAsync(() => service.SaveAsync(actor, id, 1, changed, CatalogPriceTestData.Meta("draft-2")))));
        Assert.Equal(1, await data.Catalog.CountAsync("orderDraftVersions"));

        var raced = (await service.SaveAsync(actor, null, 0, input, CatalogPriceTestData.Meta("draft-race")))["draftId"].AsInt64;
        var outcomes = await Task.WhenAll(
            Record.ExceptionAsync(() => service.DiscardAsync(actor, raced, 1, CatalogPriceTestData.Meta("discard-race"))),
            Record.ExceptionAsync(() => service.SaveAsync(actor, raced, 1, changed, CatalogPriceTestData.Meta("save-race"))));
        Assert.Single(outcomes, error => error is null);
        var discardWon = outcomes[0] is null;
        Assert.Equal(discardWon ? 1 : 2, (int)await data.Rows("orderDraftVersions").CountDocumentsAsync(new BsonDocument("draftId", raced)));
        Assert.Equal(discardWon, await data.Rows("orderDraftDiscards").CountDocumentsAsync(new BsonDocument("_id", raced)) == 1);
    }

    [Fact]
    public async Task Restore_after_the_thirty_day_window_is_refused_even_before_the_purge_runs() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var input = await OrderDraftServiceTests.Prepare(data);
        var service = OrderDraftServiceTests.Service(data); var actor = data.Catalog.Buyer;
        var id = (await service.SaveAsync(actor, null, 0, input, CatalogPriceTestData.Meta("draft-1")))["draftId"].AsInt64;
        await service.DiscardAsync(actor, id, 1, CatalogPriceTestData.Meta("discard-1"));
        await AgeDiscardAsync(data, id, TimeSpan.FromDays(31));
        Assert.Equal("order_draft_restore_expired", Code(await Record.ExceptionAsync(() => service.RestoreAsync(actor, id, CatalogPriceTestData.Meta("restore-late")))));
        Assert.Single((await service.ListDiscardedAsync(actor, input.Context)).Items);
    }

    [Fact]
    public async Task Another_actor_cannot_discard_restore_or_list_discarded_drafts() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var input = await OrderDraftServiceTests.Prepare(data);
        var service = OrderDraftServiceTests.Service(data); var actor = data.Catalog.Buyer; var other = data.Maker;
        var id = (await service.SaveAsync(actor, null, 0, input, CatalogPriceTestData.Meta("draft-1")))["draftId"].AsInt64;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DiscardAsync(other, id, 1, CatalogPriceTestData.Meta("discard-other")));
        await service.DiscardAsync(actor, id, 1, CatalogPriceTestData.Meta("discard-1"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RestoreAsync(other, id, CatalogPriceTestData.Meta("restore-other")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListDiscardedAsync(other, input.Context));
        Assert.Single((await service.ListDiscardedAsync(actor, input.Context)).Items);
    }

    [Fact]
    public async Task Discard_replays_by_key_and_refuses_a_second_discard_or_a_reused_key() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var input = await OrderDraftServiceTests.Prepare(data);
        var service = OrderDraftServiceTests.Service(data); var actor = data.Catalog.Buyer;
        var id = (await service.SaveAsync(actor, null, 0, input, CatalogPriceTestData.Meta("draft-1")))["draftId"].AsInt64;
        var first = await service.DiscardAsync(actor, id, 1, CatalogPriceTestData.Meta("discard-1"));
        Assert.Equal(first, await service.DiscardAsync(actor, id, 1, CatalogPriceTestData.Meta("discard-1")));
        Assert.Equal("order_draft_discarded", Code(await Record.ExceptionAsync(() => service.DiscardAsync(actor, id, 1, CatalogPriceTestData.Meta("discard-2")))));
        Assert.Equal("idempotency_key_reused", Code(await Record.ExceptionAsync(() => service.DiscardAsync(actor, id, 2, CatalogPriceTestData.Meta("discard-1")))));
        Assert.Equal(1, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "ordering.draft.discarded")));
    }
}
