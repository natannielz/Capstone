using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentReceiptIntegrityTests(MongoFixture fixture)
{
    private static ImmutableArray<ReceiptLineDeclaration> Whole(ShipmentDispatchTestData data) => [new(data.Source.Lines[0].SkuId, new(1, 0), new(0, 0), new(0, 0), null)];
    [Fact]
    public async Task Competing_confirmations_cannot_consume_two_challenges_or_duplicate_receipt_effects() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await ShipmentReceiptTests.Ready(data); var service = ShipmentReceiptTests.Service(data);
        var first = await service.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("challenge-a"));
        var second = await service.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("challenge-b"));
        async Task<ShipmentReceiptView?> Confirm(ShipmentReceiptChallengeView challenge, string key) {
            try { return await service.ConfirmAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true, Whole(data), CatalogPriceTestData.Meta(key)); }
            catch (Exception error) when (error is AuthorizationDeniedException or PersistenceConflictException) { return null; }
        }
        var outcomes = await Task.WhenAll(Confirm(first, "confirm-a"), Confirm(second, "confirm-b")); var accepted = Assert.Single(outcomes.OfType<ShipmentReceiptView>());
        Assert.Equal(1, await data.Order.Rows("shipmentReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Order.Rows("shipmentReceiptChallenges").CountDocumentsAsync(new BsonDocument("consumedReceiptId", accepted.Id.Value)));
        foreach (var collection in new[] { "auditEvents", "idempotencyRecords" }) Assert.Equal(1, await data.Order.Rows(collection).CountDocumentsAsync(new BsonDocument("operation", ShipmentReceiptRecords.Confirm)));
        Assert.Equal(1, await data.Order.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", ShipmentReceiptRecords.Confirm)));
        Assert.Equal(2, await data.Order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Wrong_recipient_cannot_exhaust_challenge_and_courier_cannot_target_self_or_an_unverified_person() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await ShipmentReceiptTests.Ready(data); var service = ShipmentReceiptTests.Service(data);
        var other = await data.Order.Prices.Catalog.ActorAsync(99001, CatalogAuthoringTestData.Audience, Permissions.ReceiptAccept);
        var challenge = await service.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("target"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReviewAsync(other, challenge.Id.Value, challenge.Token!));
        Assert.Equal(0, (await data.Order.Rows("shipmentReceiptChallenges").Find(new BsonDocument("_id", challenge.Id.Value)).SingleAsync())["failedAttempts"].AsInt32);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Courier.UserId, CatalogPriceTestData.Meta("self")));
        await data.Order.Rows("personAccountBindings").DeleteOneAsync(new BsonDocument("_id", data.Courier.UserId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ConfirmAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true, Whole(data), CatalogPriceTestData.Meta("unknown-person")));
        Assert.Equal(0, await data.Order.Rows("shipmentReceipts").CountDocumentsAsync(new BsonDocument()));
    }
    [Theory]
    [InlineData("root")] [InlineData("audit")] [InlineData("operation")] [InlineData("document")] [InlineData("consumed")]
    public async Task Missing_or_changed_accepted_history_blocks_read_replay_and_new_acceptance(string fault) {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await ShipmentReceiptTests.Ready(data); var service = ShipmentReceiptTests.Service(data);
        var challenge = await service.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("integrity-qr"));
        var metadata = CatalogPriceTestData.Meta("integrity-confirm");
        var receipt = await service.ConfirmAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true, Whole(data), metadata);
        if (fault == "root") await data.Order.Rows("shipmentReceipts").DeleteOneAsync(new BsonDocument("_id", receipt.Id.Value));
        if (fault == "audit") await data.Order.Rows("auditEvents").DeleteOneAsync(new BsonDocument("operation", ShipmentReceiptRecords.Confirm));
        if (fault == "operation") await data.Order.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", ShipmentReceiptRecords.Confirm));
        if (fault == "document") await data.Order.Rows("issuedDocuments").DeleteOneAsync(new BsonDocument("_id", source.Document));
        if (fault == "consumed") await data.Order.Rows("shipmentReceiptChallenges").UpdateOneAsync(new BsonDocument("_id", challenge.Id.Value), Builders<BsonDocument>.Update.Set("consumedReceiptId", BsonNull.Value).Set("consumedAt", BsonNull.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.Courier, source.Shipment));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ConfirmAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true, Whole(data), metadata));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("new-key-not-repair")));
    }
    [Theory]
    [InlineData("membership")] [InlineData("person")] [InlineData("expiry")]
    public async Task Final_authority_and_expiry_fences_roll_back_receipt_and_challenge_consumption(string fault) {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await ShipmentReceiptTests.Ready(data); var original = ShipmentReceiptTests.Service(data);
        var challenge = await original.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("fence-qr"));
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var request = 0; var observed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (request == 0 && command.Command.GetValue("insert", BsonNull.Value) == "shipmentReceipts") request = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => { if (command.RequestId == request && Interlocked.CompareExchange(ref observed, 1, 0) == 0) { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Receipt race did not release"); } });
        };
        using var client = new MongoClient(settings); var db = client.GetDatabase(data.Order.Database.DatabaseNamespace.DatabaseName); var clock = data.Order.Prices.Catalog.Clock;
        var orders = new UnitToko.Platform.Ordering.OrderSubmissionService(db, new(client), new(db), clock);
        var queries = new ShipmentQueryService(db, new(client), clock, orders, new(db, new(client), new(db), clock, orders));
        var service = new ShipmentReceiptService(db, new(client), new(db), clock, queries, new(db, new(client), new(db), clock, queries));
        var pending = Task.Run(() => service.ConfirmAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true, Whole(data), CatalogPriceTestData.Meta("fenced-confirm")));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (fault == "membership") await data.Order.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Order.Buyer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (fault == "person") await data.Order.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", data.Courier.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (fault == "expiry") clock.UtcNow = challenge.ExpiresAt;
        } finally { release.Set(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30))); Assert.Equal(1, observed);
        Assert.Equal(0, await data.Order.Rows("shipmentReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.True((await data.Order.Rows("shipmentReceiptChallenges").Find(new BsonDocument("_id", challenge.Id.Value)).SingleAsync())["consumedReceiptId"].IsBsonNull);
        foreach (var collection in new[] { "auditEvents", "idempotencyRecords" }) Assert.Equal(0, await data.Order.Rows(collection).CountDocumentsAsync(new BsonDocument("operation", ShipmentReceiptRecords.Confirm)));
    }
}
