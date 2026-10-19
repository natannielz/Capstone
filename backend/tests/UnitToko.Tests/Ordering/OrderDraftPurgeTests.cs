using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderDraftPurgeTests(MongoFixture fixture)
{
    private static OrderDraftPurge Purge(CatalogPriceTestData data, int batch = OrderDraftPurge.BatchLimit) =>
        new(data.Catalog.Database, new(data.Catalog.Client), new(data.Catalog.Database), data.Catalog.Clock, batch);
    private static async Task<long> DiscardedDraftAsync(CatalogPriceTestData data, OrderDraftService service, UnitToko.Modules.Ordering.OrderDraftInput input, string key, int ageDays, bool edited = false) {
        var actor = data.Catalog.Buyer;
        var id = (await service.SaveAsync(actor, null, 0, input, CatalogPriceTestData.Meta(key + "-save")))["draftId"].AsInt64; var revision = 1L;
        if (edited) revision = (await service.SaveAsync(actor, id, 1, input with { PurchaseReference = "PO revisi" }, CatalogPriceTestData.Meta(key + "-edit")))["revision"].AsInt64;
        await service.DiscardAsync(actor, id, revision, CatalogPriceTestData.Meta(key + "-discard"));
        await OrderDraftDiscardTests.AgeDiscardAsync(data, id, TimeSpan.FromDays(ageDays));
        return id;
    }

    [Fact]
    public async Task Purge_removes_every_draft_row_after_thirty_days_and_keeps_the_audit_trail() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var service = OrderDraftServiceTests.Service(data); var input = await OrderDraftServiceTests.Prepare(data);
        var id = await DiscardedDraftAsync(data, service, input, "old", 31, edited: true);
        var result = await Purge(data).RunOnceAsync();
        Assert.Equal((1, 0), (result.Purged, result.Refused));
        Assert.Equal(0, await data.Rows("orderDrafts").CountDocumentsAsync(new BsonDocument("_id", id)));
        Assert.Equal(0, await data.Rows("orderDraftVersions").CountDocumentsAsync(new BsonDocument("draftId", id)));
        Assert.Equal(0, await data.Catalog.CountAsync("orderDraftDiscards"));
        foreach (var operation in new[] { "ordering.draft.save", "ordering.draft.discard" })
            Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", operation)));
        Assert.Equal(0, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "ordering.draft.saved")));
        Assert.Equal(2, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "ordering.draft.saved")));
        Assert.Equal(1, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "ordering.draft.discarded")));
        var purged = await data.Rows("auditEvents").Find(new BsonDocument("kind", "ordering.draft.purged")).SingleAsync();
        Assert.Equal((id, 2, 3), (purged["result"]["draftId"].AsInt64, purged["result"]["versions"].AsInt32, purged["result"]["receipts"].AsInt32));
        Assert.Equal(0, (await Purge(data).RunOnceAsync()).Purged);
    }

    [Fact]
    public async Task Drafts_inside_the_window_or_never_discarded_are_kept() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var service = OrderDraftServiceTests.Service(data); var input = await OrderDraftServiceTests.Prepare(data);
        var recent = await DiscardedDraftAsync(data, service, input, "recent", 29);
        var active = (await service.SaveAsync(data.Catalog.Buyer, null, 0, input, CatalogPriceTestData.Meta("active-save")))["draftId"].AsInt64;
        Assert.Equal((0, 0), ((await Purge(data).RunOnceAsync()).Purged, 0));
        foreach (var id in new[] { recent, active }) Assert.Equal(1, await data.Rows("orderDrafts").CountDocumentsAsync(new BsonDocument("_id", id)));
        Assert.Single((await service.ListDiscardedAsync(data.Catalog.Buyer, input.Context)).Items);
    }

    [Fact]
    public async Task Each_run_purges_at_most_one_batch() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var service = OrderDraftServiceTests.Service(data); var input = await OrderDraftServiceTests.Prepare(data);
        await DiscardedDraftAsync(data, service, input, "first", 40); await DiscardedDraftAsync(data, service, input, "second", 35);
        Assert.Equal(1, (await Purge(data, batch: 1).RunOnceAsync()).Purged);
        Assert.Equal(1, await data.Catalog.CountAsync("orderDraftDiscards"));
        Assert.Equal(1, (await Purge(data, batch: 1).RunOnceAsync()).Purged);
        Assert.Equal(0, await data.Catalog.CountAsync("orderDrafts"));
    }

    // A discard date moved back without its audit event is not trusted: the draft is never deleted early.
    [Fact]
    public async Task A_discard_whose_date_does_not_match_its_audit_is_refused_not_purged() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture); var service = OrderDraftServiceTests.Service(data); var input = await OrderDraftServiceTests.Prepare(data);
        var id = await DiscardedDraftAsync(data, service, input, "tampered", 1);
        var at = DateTime.UtcNow.AddDays(-40);
        await data.Rows("orderDraftDiscards").UpdateOneAsync(new BsonDocument("_id", id),
            Builders<BsonDocument>.Update.Set("discardedAt", at).Set("restorableUntil", at.AddDays(30)));
        Assert.Equal((0, 1), ((await Purge(data).RunOnceAsync()) is var result ? (result.Purged, result.Refused) : default));
        Assert.Equal(1, await data.Rows("orderDrafts").CountDocumentsAsync(new BsonDocument("_id", id)));
        Assert.Equal(1, await data.Catalog.CountAsync("orderDraftVersions"));
    }
}
