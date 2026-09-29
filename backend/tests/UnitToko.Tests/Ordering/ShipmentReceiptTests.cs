using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentReceiptTests(MongoFixture fixture)
{
    internal static ShipmentReceiptService Service(ShipmentDispatchTestData data) => new(data.Order.Database, new(data.Order.Prices.Catalog.Client),
        new(data.Order.Database), data.Order.Prices.Catalog.Clock, ShipmentReadTests.Service(data), ShipmentDocumentTests.Service(data));
    internal static async Task<(long Shipment, long Document)> Ready(ShipmentDispatchTestData data) {
        var catalog = data.Order.Prices.Catalog;
        await catalog.Base.AddGrantAsync(data.Courier, 99100, new(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103), [Permissions.DeliveryEvidence]);
        await catalog.Base.AddGrantAsync(data.Order.Buyer, 99101, CatalogAuthoringTestData.Audience, [Permissions.ReceiptAccept]);
        foreach (var userId in new[] { data.Courier.UserId, data.Order.Buyer.UserId })
            if (!await data.Order.Rows("personRecords").Find(new BsonDocument("_id", PersonTestData.PersonId(userId))).AnyAsync())
                await PersonTestData.SeedDistinctPeopleAsync(data.Order.Database, catalog.Clock, userId);
        await data.Order.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", data.Order.Buyer.UserId }, { "schemaVersion", 1 }, { "version", 1L },
            { "displayName", "Penerima fixture" }, { "verifiedEmail", "recipient@example.invalid" }, { "verifiedAt", catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", catalog.Clock.UtcNow.UtcDateTime } });
        var shipment = (await data.DispatchAsync(await data.PreviewAsync(), "receipt-dispatch"))["shipmentId"].AsInt64;
        var document = await ShipmentDocumentTests.Service(data).IssueAsync(data.Picker, shipment, 1, CatalogPriceTestData.Meta("receipt-document"));
        return (shipment, document.Id.Value);
    }
    [Fact]
    public async Task Separate_recipient_confirms_exact_partial_receipt_once_without_stock_or_money_changes() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await Ready(data); var service = Service(data);
        var eligible = await service.RecipientsAsync(data.Courier, source.Shipment);
        Assert.Equal(data.Order.Buyer.UserId, Assert.Single(eligible.Items).Id.Value);
        var issue = CatalogPriceTestData.Meta("receipt-challenge");
        var challenge = await service.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, issue);
        Assert.NotNull(challenge.Token); Assert.Null((await service.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, issue)).Token);
        var review = await service.ReviewAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!);
        Assert.Equal("Penerima fixture", review.RecipientName); Assert.Equal(source.Document, review.DocumentId.Value);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReviewAsync(data.Courier, challenge.Id.Value, challenge.Token!));
        var lines = new[] { new ReceiptLineDeclaration(review.Lines[0].SkuId, new(0, 6), new(0, 4), new(0, 2), "Kemasan rusak dan dua satuan belum diterima") }.ToImmutableArray();
        var meta = CatalogPriceTestData.Meta("receipt-confirm");
        var accepted = await service.ConfirmAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true, lines, meta);
        Assert.True(accepted.HasException); Assert.Equal(6, accepted.Lines[0].AcceptedBaseUnits);
        Assert.Equal(accepted.Id, (await service.ConfirmAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true, lines, meta)).Id);
        Assert.Equal(accepted.Id, (await service.RecoverConfirmationAsync(data.Order.Buyer, meta.IdempotencyKey))!.Id);
        Assert.Equal(accepted.Id, (await service.ReadAsync(data.Courier, source.Shipment))!.Id);
        Assert.Equal(1, await data.Order.Rows("shipmentReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Order.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        var stored = await data.Order.Rows("shipmentReceiptChallenges").Find(new BsonDocument("_id", challenge.Id.Value)).SingleAsync();
        Assert.DoesNotContain(challenge.Token!, stored.ToJson()); Assert.Equal(accepted.Id.Value, stored["consumedReceiptId"].AsInt64);
    }
    [Fact]
    public async Task Wrong_tokens_exhaust_attempts_without_accepting_and_expired_challenge_fails() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await Ready(data); var service = Service(data);
        var challenge = await service.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("attempts"));
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReviewAsync(data.Order.Buyer, challenge.Id.Value, new string('0', 64)));
        var stored = await data.Order.Rows("shipmentReceiptChallenges").Find(new BsonDocument("_id", challenge.Id.Value)).SingleAsync();
        Assert.Equal(5, stored["failedAttempts"].AsInt32);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReviewAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!));
        var next = await service.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("expired"));
        data.Order.Prices.Catalog.Clock.UtcNow = next.ExpiresAt;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReviewAsync(data.Order.Buyer, next.Id.Value, next.Token!));
        Assert.Equal(0, await data.Order.Rows("shipmentReceipts").CountDocumentsAsync(new BsonDocument()));
    }
}
