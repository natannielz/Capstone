using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class DeliveryAttemptTests(MongoFixture fixture)
{
    internal static DeliveryAttemptService Service(ShipmentDispatchTestData data) => new(data.Order.Database, new(data.Order.Prices.Catalog.Client), new(data.Order.Database),
        data.Order.Prices.Catalog.Clock, ShipmentReadTests.Service(data), ShipmentReceiptTests.Service(data));
    [Fact]
    public async Task Failure_revokes_old_QR_new_visit_can_receive_and_history_replays_without_stock_changes() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await ShipmentReceiptTests.Ready(data);
        var receipts = ShipmentReceiptTests.Service(data); var attempts = Service(data); var meta = CatalogPriceTestData.Meta("visit-failed");
        var qr = await receipts.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("before-failure"));
        var preview = await attempts.PreviewAsync(data.Courier, source.Shipment); Assert.Equal(0, preview.Sequence); Assert.False(preview.Received);
        var recorded = await attempts.RecordAsync(data.Courier, source.Shipment, 0, "recipient_unavailable", preview.ServerTime, meta);
        Assert.Equal(1, recorded.Sequence); Assert.Equal(1, recorded.RevokedQrCount);
        Assert.Equal(recorded, await attempts.RecordAsync(data.Courier, source.Shipment, 0, "recipient_unavailable", preview.ServerTime, meta));
        Assert.Equal(recorded, await attempts.RecoverAsync(data.Courier, meta.IdempotencyKey));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => receipts.ReviewAsync(data.Order.Buyer, qr.Id.Value, qr.Token!));
        Assert.Null((await receipts.RecoverChallengeAsync(data.Courier, "before-failure"))!.Token);
        var old = await data.Order.Rows("shipmentReceiptChallenges").Find(new BsonDocument("_id", qr.Id.Value)).SingleAsync(); Assert.Equal(0, old["failedAttempts"].AsInt32);
        var fresh = await receipts.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("new-visit"));
        var review = await receipts.ReviewAsync(data.Order.Buyer, fresh.Id.Value, fresh.Token!);
        var accepted = await receipts.ConfirmAsync(data.Order.Buyer, fresh.Id.Value, fresh.Token!, DeliveryReceiptPolicy.ConsentVersion, true,
            ImmutableArray.Create(new ReceiptLineDeclaration(review.Lines[0].SkuId, new(1, 0), new(0, 0), new(0, 0), null)), CatalogPriceTestData.Meta("new-visit-confirm"));
        Assert.False(accepted.HasException); Assert.True((await attempts.ReadAsync(data.Courier, source.Shipment)).Received);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => attempts.RecordAsync(data.Courier, source.Shipment, 1, "other", preview.ServerTime, CatalogPriceTestData.Meta("after-receipt")));
        Assert.Equal(recorded, await attempts.RecoverAsync(data.Courier, meta.IdempotencyKey));
        Assert.Equal(recorded, await attempts.RecordAsync(data.Courier, source.Shipment, 0, "recipient_unavailable", preview.ServerTime, meta));
        Assert.Equal(2, await data.Order.Rows("movements").CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Order.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Only_assigned_courier_can_report_current_history_and_invalid_visit_times_are_rejected() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await ShipmentReceiptTests.Ready(data); var service = Service(data); var now = data.Order.Prices.Catalog.Clock.UtcNow;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecordAsync(data.Picker, source.Shipment, 0, "other", now, CatalogPriceTestData.Meta("wrong-role")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.PreviewAsync(data.Order.Buyer, source.Shipment));
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => service.RecordAsync(data.Courier, source.Shipment, 0, "delivered", now, CatalogPriceTestData.Meta("no-self-accept")));
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => service.RecordAsync(data.Courier, source.Shipment, 0, "other", now.AddMinutes(1), CatalogPriceTestData.Meta("future")));
        var accepted = await service.RecordAsync(data.Courier, source.Shipment, 0, "address_unreachable", DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()), CatalogPriceTestData.Meta("gate-closed"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.RecordAsync(data.Courier, source.Shipment, 0, "other", now, CatalogPriceTestData.Meta("stale-history")));
        var history = await service.ReadAsync(data.Courier, source.Shipment); Assert.Equal(accepted, Assert.Single(history.Items)); Assert.False(history.HasMore);
        await data.Order.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Courier.UserId), Builders<BsonDocument>.Update.Pull("permissions", "shipment.read"));
        Assert.Equal(accepted, await service.RecoverAsync(data.Courier, "gate-closed"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.Courier, source.Shipment));
        await data.Order.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Courier.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(data.Courier, "gate-closed"));
    }
}
