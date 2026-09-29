using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Fulfillment;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class DeliveryAttemptIntegrityTests(MongoFixture fixture)
{
    [Fact]
    public async Task Simultaneous_failure_and_recipient_confirmation_cannot_both_commit() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await ShipmentReceiptTests.Ready(data);
        var receipts = ShipmentReceiptTests.Service(data); var service = DeliveryAttemptTests.Service(data);
        var qr = await receipts.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("racing-qr"));
        var declarations = ImmutableArray.Create(new ReceiptLineDeclaration(data.Source.Lines[0].SkuId, new(1, 0), new(0, 0), new(0, 0), null));
        async Task<bool> Try(Func<Task> action) { try { await action(); return true; } catch (Exception error) when (error is PersistenceConflictException or AuthorizationDeniedException) { return false; } }
        var results = await Task.WhenAll(
            Try(async () => await service.RecordAsync(data.Courier, source.Shipment, 0, "recipient_unavailable", data.Order.Prices.Catalog.Clock.UtcNow, CatalogPriceTestData.Meta("race-visit"))),
            Try(async () => await receipts.ConfirmAsync(data.Order.Buyer, qr.Id.Value, qr.Token!, DeliveryReceiptPolicy.ConsentVersion, true, declarations, CatalogPriceTestData.Meta("race-receipt"))));
        Assert.Single(results, result => result);
        var attempts = await data.Order.Rows("deliveryAttempts").CountDocumentsAsync(new BsonDocument());
        Assert.Equal(1, attempts + await data.Order.Rows("shipmentReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(attempts, await data.Order.Rows("receiptChallengeRevocations").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Order.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Competing_visits_and_original_retry_have_one_sequence_and_one_effect_set() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await ShipmentReceiptTests.Ready(data); var service = DeliveryAttemptTests.Service(data);
        async Task<DeliveryAttemptView?> Try(string key) { try { return await service.RecordAsync(data.Courier, source.Shipment, 0, "transport_issue", data.Order.Prices.Catalog.Clock.UtcNow, CatalogPriceTestData.Meta(key)); } catch (PersistenceConflictException) { return null; } }
        var results = await Task.WhenAll(Try("driver-a"), Try("driver-b")); var accepted = Assert.Single(results, row => row is not null)!;
        Assert.Equal(1, accepted.Sequence);
        Assert.Equal(1, await data.Order.Rows("deliveryAttempts").CountDocumentsAsync(new BsonDocument()));
        foreach (var name in new[] { "auditEvents", "idempotencyRecords" }) Assert.Equal(1, await data.Order.Rows(name).CountDocumentsAsync(new BsonDocument("operation", DeliveryAttemptRecords.Operation)));
        Assert.Equal(1, await data.Order.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", DeliveryAttemptRecords.Operation)));
    }
    [Theory]
    [InlineData("root")] [InlineData("revocation")] [InlineData("reason")]
    public async Task Damaged_history_never_revives_old_QR_or_allows_new_challenge(string fault) {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await ShipmentReceiptTests.Ready(data);
        var receipts = ShipmentReceiptTests.Service(data); var service = DeliveryAttemptTests.Service(data);
        var qr = await receipts.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("integrity-qr"));
        await service.RecordAsync(data.Courier, source.Shipment, 0, "delivery_refused", data.Order.Prices.Catalog.Clock.UtcNow, CatalogPriceTestData.Meta("integrity-visit"));
        if (fault == "root") await data.Order.Rows("deliveryAttempts").DeleteManyAsync(new BsonDocument());
        if (fault == "revocation") await data.Order.Rows("receiptChallengeRevocations").DeleteManyAsync(new BsonDocument());
        if (fault == "reason") await data.Order.Rows("deliveryAttempts").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("reason", "Unaccepted altered explanation"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.Courier, source.Shipment));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(data.Courier, "integrity-visit"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => receipts.ReviewAsync(data.Order.Buyer, qr.Id.Value, qr.Token!));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => receipts.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("after-damage")));
        Assert.Equal(1, await data.Order.Rows("shipmentReceiptChallenges").CountDocumentsAsync(new BsonDocument()));
    }
    [Theory]
    [InlineData("membership")] [InlineData("expiry")]
    public async Task Final_authority_loss_rolls_back_attempt_revocations_and_effects(string fault) {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await ShipmentReceiptTests.Ready(data);
        var qr = await ShipmentReceiptTests.Service(data).IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("fence-qr"));
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var request = 0; var observed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (request == 0 && command.Command.GetValue("insert", BsonNull.Value) == "deliveryAttempts") request = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => { if (command.RequestId == request && Interlocked.CompareExchange(ref observed, 1, 0) == 0) { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Attempt fence was not released"); } });
        };
        using var client = new MongoClient(settings); var db = client.GetDatabase(data.Order.Database.DatabaseNamespace.DatabaseName); var clock = data.Order.Prices.Catalog.Clock;
        var orders = new UnitToko.Platform.Ordering.OrderSubmissionService(db, new(client), new(db), clock);
        var queries = new ShipmentQueryService(db, new(client), clock, orders, new(db, new(client), new(db), clock, orders));
        var receipts = new ShipmentReceiptService(db, new(client), new(db), clock, queries, new(db, new(client), new(db), clock, queries));
        var service = new DeliveryAttemptService(db, new(client), new(db), clock, queries, receipts);
        var pending = Task.Run(() => service.RecordAsync(data.Courier, source.Shipment, 0, "other", clock.UtcNow, CatalogPriceTestData.Meta("fenced-visit")));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (fault == "membership") await data.Order.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Courier.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (fault == "expiry") clock.UtcNow = clock.UtcNow.AddHours(9);
        } finally { release.Set(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30))); Assert.Equal(1, observed);
        foreach (var name in new[] { "deliveryAttempts", "receiptChallengeRevocations" }) Assert.Equal(0, await data.Order.Rows(name).CountDocumentsAsync(new BsonDocument()));
        foreach (var name in new[] { "auditEvents", "idempotencyRecords" }) Assert.Equal(0, await data.Order.Rows(name).CountDocumentsAsync(new BsonDocument("operation", DeliveryAttemptRecords.Operation)));
        Assert.Equal(0, await data.Order.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", DeliveryAttemptRecords.Operation)));
        Assert.Equal(0, (await data.Order.Rows("shipmentReceiptChallenges").Find(new BsonDocument("_id", qr.Id.Value)).SingleAsync())["failedAttempts"].AsInt32);
    }
}
