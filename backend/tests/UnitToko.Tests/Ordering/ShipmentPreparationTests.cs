using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentPreparationTests(MongoFixture fixture)
{
    internal static ShipmentPreparationService Service(OrderSubmissionTestData data, MongoClient? client = null) {
        client ??= data.Prices.Catalog.Client; var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        return new(db, new(client), new(db), data.Prices.Catalog.Clock, new(db, new(client), new(db), data.Prices.Catalog.Clock));
    }
    internal static async Task<(ActorContext Picker, FulfillmentOrderView Source)> Ready(OrderSubmissionTestData data) {
        var actors = await OrderDecisionTests.Actors(data);
        var picker = await data.Prices.Catalog.ActorAsync(8800, CatalogAuthoringTestData.Shop, Permissions.ShipmentPrepare);
        var id = await OrderDecisionTests.Submit(data);
        var spending = await data.Service.ReviewOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending);
        await data.Service.DecideOrderAsync(actors.Approver, id, null, OrderReviewStage.Spending, spending.Revision, spending.Review, CatalogPriceTestData.Meta("prepare-spending"));
        var store = Assert.Single((await data.Service.ReviewListAsync(actors.Store, OrderReviewStage.Store)).Items);
        await data.Service.DecideOrderAsync(actors.Store, id, store.SuborderId, OrderReviewStage.Store, store.Revision, store.Review, CatalogPriceTestData.Meta("prepare-store"));
        return (picker, await data.Service.ReadFulfillmentOrderAsync(picker, store.SuborderId!.Value));
    }
    [Fact]
    public async Task Partial_preparation_is_shared_versioned_and_recovered_without_stock_or_money_effects() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var ready = await Ready(data); var service = Service(data);
        var source = ready.Source; var line = Assert.Single(source.Lines);
        Assert.Null(await service.ReadAsync(ready.Picker, source.SuborderId.Value));
        var saved = await service.SaveAsync(ready.Picker, source.SuborderId.Value, 0, source.Review, [new(line.SkuId, 1)], CatalogPriceTestData.Meta("prepare-one"));
        Assert.Equal(saved, await service.RecoverAsync(ready.Picker, "prepare-one"));
        Assert.Equal(saved, await service.SaveAsync(ready.Picker, source.SuborderId.Value, 0, source.Review, [new(line.SkuId, 1)], CatalogPriceTestData.Meta("prepare-one")));
        var read = Assert.IsType<ShipmentPreparationView>(await service.ReadAsync(ready.Picker, source.SuborderId.Value));
        Assert.Equal(1L, read.Revision); Assert.True(read.CurrentSourceMatches); Assert.Equal(1, Assert.Single(read.Lines).Packages);
        Assert.Equal(12, Assert.Single(read.AcceptedSource.Lines).Package.BaseUnitsPerPackage);
        var other = await data.Prices.Catalog.ActorAsync(8900, CatalogAuthoringTestData.Shop, Permissions.ShipmentPrepare);
        var revised = await service.SaveAsync(other, source.SuborderId.Value, 1, source.Review, [new(line.SkuId, 2)], CatalogPriceTestData.Meta("prepare-two"));
        Assert.Equal(saved["preparationId"], revised["preparationId"]); Assert.Equal(2L, revised["revision"].AsInt64);
        Assert.Equal(saved, await service.RecoverAsync(ready.Picker, "prepare-one"));
        Assert.Null(await service.RecoverAsync(other, "prepare-one"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.SaveAsync(ready.Picker, source.SuborderId.Value, 1, source.Review, [new(line.SkuId, 1)], CatalogPriceTestData.Meta("stale-preparation")));
        var unrelated = await data.Prices.Catalog.ActorAsync(8950, CatalogAuthoringTestData.OtherOwner, Permissions.ShipmentPrepare);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(unrelated, source.SuborderId.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.Buyer, source.SuborderId.Value));
        Assert.Equal(1, await data.Rows(ShipmentPreparationBson.Heads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows(ShipmentPreparationBson.Versions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        var stock = await data.Rows("stockBalances").Find(new BsonDocument()).SingleAsync(); Assert.Equal(2L, stock["reserved"].AsInt64); Assert.Equal(10L, stock["onHand"].AsInt64);
        Assert.Equal(0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Cancelled_order_retains_old_preparation_and_receipt_but_no_new_preparation_is_authorized() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var ready = await Ready(data); var service = Service(data); var source = ready.Source;
        var saved = await service.SaveAsync(ready.Picker, source.SuborderId.Value, 0, source.Review, [new(source.Lines[0].SkuId, 1)], CatalogPriceTestData.Meta("before-cancel"));
        await data.Prices.Catalog.Base.AddGrantAsync(data.Buyer, 6500, CatalogAuthoringTestData.Audience, [Permissions.OrderCancel]);
        var cancel = await data.Service.ReviewOrderAsync(data.Buyer, source.OrderId.Value, null, OrderReviewStage.Cancellation);
        await data.Service.DecideOrderAsync(data.Buyer, source.OrderId.Value, null, OrderReviewStage.Cancellation, cancel.Revision, cancel.Review, CatalogPriceTestData.Meta("cancel-pick"), action: OrderReviewAction.CancelRemainder);
        var read = Assert.IsType<ShipmentPreparationView>(await service.ReadAsync(ready.Picker, source.SuborderId.Value));
        Assert.False(read.CurrentSourceMatches); Assert.True(read.AcceptedSource.CanPrepare);
        Assert.Equal(saved, await service.RecoverAsync(ready.Picker, "before-cancel"));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.SaveAsync(ready.Picker, source.SuborderId.Value, 1, source.Review, [new(source.Lines[0].SkuId, 1)], CatalogPriceTestData.Meta("after-cancel")));
        Assert.Equal(1, await data.Rows(ShipmentPreparationBson.Versions).CountDocumentsAsync(new BsonDocument()));
    }
    [Theory] [InlineData("receipt")] [InlineData("audit")] [InlineData("head")]
    public async Task Corrupted_preparation_cannot_be_read_or_silently_recreated(string fault) {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var ready = await Ready(data); var service = Service(data); var source = ready.Source;
        var saved = await service.SaveAsync(ready.Picker, source.SuborderId.Value, 0, source.Review, [new(source.Lines[0].SkuId, 1)], CatalogPriceTestData.Meta("saved"));
        var version = await data.Rows(ShipmentPreparationBson.Versions).Find(new BsonDocument("_id", saved["versionId"])).SingleAsync();
        if (fault == "receipt") await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation", ShipmentPreparationBson.Operation }, { "key", "saved" } });
        if (fault == "audit") await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", version["auditId"]));
        if (fault == "head") await data.Rows(ShipmentPreparationBson.Heads).DeleteOneAsync(new BsonDocument("_id", saved["preparationId"]));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(ready.Picker, source.SuborderId.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.SaveAsync(ready.Picker, source.SuborderId.Value, fault == "head" ? 0 : 1, source.Review, [new(source.Lines[0].SkuId, 1)], CatalogPriceTestData.Meta("replacement")));
        Assert.Equal(1, await data.Rows(ShipmentPreparationBson.Versions).CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Session_expiry_after_writing_outbox_aborts_the_entire_preparation() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var ready = await Ready(data); var fired = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(value => {
            if (value.CommandName == "insert" && value.Command["insert"] == "outboxEvents" &&
                value.Command["documents"].AsBsonArray.Any(row => row["kind"] == "fulfillment.preparation.saved") && Interlocked.Exchange(ref fired, 1) == 0)
                data.Prices.Catalog.Clock.UtcNow = data.Prices.Catalog.Clock.UtcNow.AddHours(9);
        });
        using var client = new MongoClient(settings);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service(data, client).SaveAsync(ready.Picker, ready.Source.SuborderId.Value, 0, ready.Source.Review,
            [new(ready.Source.Lines[0].SkuId, 1)], CatalogPriceTestData.Meta("expires-final")));
        Assert.Equal(1, fired);
        foreach (var name in ShipmentPreparationBsonCollectionNames()) Assert.Equal(0, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", ShipmentPreparationBson.Operation)));
        Assert.Equal(0, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.preparation.saved")));
    }
    [Fact]
    public async Task Two_staff_saving_the_same_preparation_revision_have_one_winner() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var ready = await Ready(data); var source = ready.Source; var service = Service(data);
        var other = await data.Prices.Catalog.ActorAsync(8900, CatalogAuthoringTestData.Shop, Permissions.ShipmentPrepare);
        var results = await Task.WhenAll(new[] { ready.Picker, other }.Select((actor, index) => Record.ExceptionAsync(() => service.SaveAsync(actor,
            source.SuborderId.Value, 0, source.Review, [new(source.Lines[0].SkuId, index + 1)], CatalogPriceTestData.Meta("racing-pick-" + index)))));
        Assert.Single(results, error => error is null); Assert.Single(results, error => error is PersistenceConflictException);
        Assert.Equal(1, await data.Rows(ShipmentPreparationBson.Heads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows(ShipmentPreparationBson.Versions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", ShipmentPreparationBson.Operation)));
    }
    [Theory] [InlineData("grant")] [InlineData("cancellation")]
    public async Task Authority_or_order_change_during_preparation_prevents_stale_save(string change) {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var ready = await Ready(data); var source = ready.Source;
        await data.Prices.Catalog.Base.AddGrantAsync(data.Buyer, 6500, CatalogAuthoringTestData.Audience, [Permissions.OrderCancel]);
        var cancel = await data.Service.ReviewOrderAsync(data.Buyer, source.OrderId.Value, null, OrderReviewStage.Cancellation);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = 0; var observed = 0; var reads = 0; var settings = fixture.Client.Settings.Clone();
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(value => { if (value.Command.GetValue("find", BsonNull.Value) == "orderSuborders" && Interlocked.Increment(ref reads) == 1) requestId = value.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(value => {
                if (requestId != 0 && value.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                    entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Preparation race did not complete");
                }
            });
        };
        using var client = new MongoClient(settings);
        var pending = Task.Run(() => Service(data, client).SaveAsync(ready.Picker, source.SuborderId.Value, 0, source.Review,
            [new(source.Lines[0].SkuId, 1)], CatalogPriceTestData.Meta("stale-during-save")));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "grant") Assert.True((await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", ready.Picker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"))).ModifiedCount > 0);
            else await data.Service.DecideOrderAsync(data.Buyer, source.OrderId.Value, null, OrderReviewStage.Cancellation, cancel.Revision, cancel.Review, CatalogPriceTestData.Meta("concurrent-cancel"), action: OrderReviewAction.CancelRemainder);
        } finally { release.Set(); }
        var error = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, observed); Assert.True(error is AuthorizationDeniedException or DomainValidationException, error?.ToString());
        Assert.Equal(0, await data.Rows(ShipmentPreparationBson.Heads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows(ShipmentPreparationBson.Versions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", ShipmentPreparationBson.Operation)));
    }
    private static string[] ShipmentPreparationBsonCollectionNames() => [ShipmentPreparationBson.Heads, ShipmentPreparationBson.Versions];
}
