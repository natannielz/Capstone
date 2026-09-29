using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Fulfillment;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentDocumentTests(MongoFixture fixture)
{
    internal static ShipmentDocumentService Service(ShipmentDispatchTestData data) => new(data.Order.Database,
        new(data.Order.Prices.Catalog.Client), new(data.Order.Database), data.Order.Prices.Catalog.Clock, ShipmentReadTests.Service(data));

    [Fact]
    public async Task Issued_copy_is_fixed_to_actual_packages_and_same_key_recovers_one_document() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture);
        var shipment = (await data.DispatchAsync(await data.PreviewAsync(), "document-dispatch"))["shipmentId"].AsInt64;
        var service = Service(data); Assert.Null(await service.ReadAsync(data.Courier, shipment));
        var metadata = CatalogPriceTestData.Meta("issue-surat-jalan");
        var first = await service.IssueAsync(data.Picker, shipment, 1, metadata);
        var replay = await service.IssueAsync(data.Picker, shipment, 1, metadata);
        Assert.Equal(first, replay); Assert.Equal(first, await service.RecoverAsync(data.Picker, metadata.IdempotencyKey));
        var downloaded = await service.DownloadAsync(data.Courier, shipment, first.Id.Value);
        Assert.Contains("Penjual fixture", downloaded.Content); Assert.Contains("Alamat penjual sintetis", downloaded.Content);
        Assert.Contains("Alamat cabang sintetis 1", downloaded.Content); Assert.Contains("Surat Jalan", downloaded.Content);
        Assert.Contains("12", downloaded.Content); Assert.Equal(first.ContentHash, IssuedDocumentStore.Hash(downloaded.Content));
        Assert.DoesNotContain("7000.25", downloaded.Content); Assert.DoesNotContain("<script", downloaded.Content);
        var snapshot = (await data.Order.Rows("issuedDocuments").Find(new BsonDocument("_id", first.Id.Value)).SingleAsync())["snapshot"].DeepClone().AsBsonDocument;
        snapshot["buyer"]["name"] = "<script>alert('buyer')</script>"; snapshot["delivery"]["instructions"] = "<img src=x onerror=alert(1)>";
        var encoded = SuratJalanRenderer.Render(snapshot, first.Id.Value, first.IssuedAt);
        Assert.Contains("&lt;script&gt;", encoded); Assert.Contains("&lt;img", encoded); Assert.DoesNotContain("<script", encoded); Assert.DoesNotContain("<img", encoded);
        await data.Order.Rows("accountProfiles").UpdateOneAsync(new BsonDocument("_id", data.Courier.UserId), Builders<BsonDocument>.Update.Set("displayName", "Nama baru"));
        Assert.Equal(downloaded, await service.DownloadAsync(data.Courier, shipment, first.Id.Value));
        Assert.Equal(first, await service.ReadAsync(data.Courier, shipment));
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => service.IssueAsync(data.Picker, shipment, 1, CatalogPriceTestData.Meta("duplicate-document")));
        Assert.Equal(1, await data.Order.Rows("issuedDocuments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Order.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", ShipmentDocumentService.Operation)));
        Assert.Equal(1, await data.Order.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.surat-jalan.issued")));
        Assert.Equal(2, await data.Order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Document_authority_and_corruption_fail_closed() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture);
        var shipment = (await data.DispatchAsync(await data.PreviewAsync(), "private-document-dispatch"))["shipmentId"].AsInt64;
        var service = Service(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IssueAsync(data.Courier, shipment, 1, CatalogPriceTestData.Meta("courier-cannot-issue")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.Picker, shipment));
        var document = await service.IssueAsync(data.Picker, shipment, 1, CatalogPriceTestData.Meta("private-document"));
        var stranger = await data.Order.Prices.Catalog.ActorAsync(9710, CatalogAuthoringTestData.OtherOwner, Permissions.ShipmentRead);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DownloadAsync(stranger, shipment, document.Id.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DownloadAsync(data.Courier, shipment + 1, document.Id.Value));
        await data.Order.Rows("issuedDocuments").UpdateOneAsync(new BsonDocument("_id", document.Id.Value), Builders<BsonDocument>.Update.Set("content", "altered content").Set("byteLength", 15L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.Courier, shipment));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DownloadAsync(data.Courier, shipment, document.Id.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(data.Picker, "private-document"));
    }
}
