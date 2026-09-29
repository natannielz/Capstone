using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Fulfillment;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentReceiptProofTests(MongoFixture fixture)
{
    internal static async Task<ShipmentReceiptView> Accept(ShipmentDispatchTestData data) {
        var source = await ShipmentReceiptTests.Ready(data); var service = ShipmentReceiptTests.Service(data);
        var challenge = await service.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("proof-challenge"));
        var review = await service.ReviewAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!);
        return await service.ConfirmAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true,
            ImmutableArray.Create(new ReceiptLineDeclaration(review.Lines[0].SkuId, new(0, 6), new(0, 4), new(0, 2), "Rusak <img src=x> dan kurang")), CatalogPriceTestData.Meta("proof-receipt"));
    }
    [Fact]
    public async Task Recipient_issues_immutable_private_proof_with_original_quantities_and_recovers_once() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var receipt = await Accept(data); var service = ShipmentReceiptTests.Service(data);
        Assert.Null(await service.ReadProofAsync(data.Order.Buyer, receipt.ShipmentId.Value, true));
        var meta = CatalogPriceTestData.Meta("proof-issue");
        var issued = await service.IssueProofAsync(data.Order.Buyer, receipt.ShipmentId.Value, receipt.Id.Value, true, meta);
        Assert.Equal(issued, await service.IssueProofAsync(data.Order.Buyer, receipt.ShipmentId.Value, receipt.Id.Value, true, meta));
        Assert.Equal(issued, await service.RecoverProofAsync(data.Order.Buyer, meta.IdempotencyKey));
        var file = await service.DownloadProofAsync(data.Courier, receipt.ShipmentId.Value, issued.Id.Value, false);
        Assert.Equal(issued.ContentHash, IssuedDocumentStore.Hash(file.Content)); Assert.Contains("Bukti Penerimaan", file.Content);
        Assert.Contains("Penerima fixture", file.Content); Assert.Contains("Alamat cabang sintetis 1", file.Content);
        Assert.Contains("Diterima</dt><dd>0 kemasan + 6", file.Content); Assert.Contains("Ditolak</dt><dd>0 kemasan + 4", file.Content);
        Assert.Contains("Belum diterima</dt><dd>0 kemasan + 2", file.Content); Assert.Contains("&lt;img", file.Content); Assert.DoesNotContain("<img", file.Content);
        Assert.DoesNotContain("tokenHash", file.Content); Assert.DoesNotContain("7000.25", file.Content);
        // A legal 100-line receipt with long, encoded discrepancy text exceeds the smaller
        // Surat Jalan envelope. Proof limits must retain every declared quantity and reason.
        var large = (await data.Order.Rows(IssuedDocumentStore.ProofCollection).Find(new BsonDocument("_id", issued.Id.Value)).SingleAsync())["snapshot"].DeepClone().AsBsonDocument;
        var dispatchLine = large["lines"][0].DeepClone().AsBsonDocument; var consentLine = large["receipt"]["lines"][0].DeepClone().AsBsonDocument;
        large["lines"] = new BsonArray(); large["receipt"]["lines"] = new BsonArray();
        for (var index = 1; index <= 100; index++) {
            var line = dispatchLine.DeepClone().AsBsonDocument; line["skuId"] = (long)index; line["productName"] = new string('&', 227); line["definition"]["packageLabel"] = new string('&', 160); line["definition"]["baseUnitCode"] = new string('&', 64);
            var declaration = consentLine.DeepClone().AsBsonDocument; declaration["skuId"] = (long)index; declaration["exceptionReason"] = new string('&', 1000);
            large["lines"].AsBsonArray.Add(line); large["receipt"]["lines"].AsBsonArray.Add(declaration);
        }
        var rendered = ReceiptProofRenderer.Render(large, issued.Id.Value, issued.IssuedAt); var length = System.Text.Encoding.UTF8.GetByteCount(rendered);
        Assert.True(length > IssuedDocumentStore.MaximumBytes); Assert.InRange(length, 1, IssuedDocumentStore.MaximumProofBytes);
        Assert.Equal(100, rendered.Split("Catatan selisih:").Length - 1); Assert.Equal(100, rendered.Split("Diterima</dt><dd>0 kemasan + 6").Length - 1);
        await data.Order.Rows("accountProfiles").UpdateOneAsync(new BsonDocument("_id", data.Order.Buyer.UserId), Builders<BsonDocument>.Update.Set("displayName", "Nama terbaru").Inc("version", 1L));
        Assert.Equal(file, await service.DownloadProofAsync(data.Order.Buyer, receipt.ShipmentId.Value, issued.Id.Value, true));
        Assert.Equal(1, await data.Order.Rows(IssuedDocumentStore.ProofCollection).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Order.Rows("issuedDocuments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Order.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Courier_and_foreign_account_cannot_issue_or_borrow_recipient_authority() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var receipt = await Accept(data); var service = ShipmentReceiptTests.Service(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IssueProofAsync(data.Courier, receipt.ShipmentId.Value, receipt.Id.Value, false, CatalogPriceTestData.Meta("courier-proof")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IssueProofAsync(data.Courier, receipt.ShipmentId.Value, receipt.Id.Value, true, CatalogPriceTestData.Meta("borrow-recipient")));
        var stranger = await data.Order.Prices.Catalog.ActorAsync(9770, CatalogAuthoringTestData.OtherOwner, Permissions.ReceiptAccept);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadProofAsync(stranger, receipt.ShipmentId.Value, true));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IssueProofAsync(data.Order.Buyer, receipt.ShipmentId.Value, receipt.Id.Value + 1, true, CatalogPriceTestData.Meta("wrong-receipt")));
        var issued = await service.IssueProofAsync(data.Picker, receipt.ShipmentId.Value, receipt.Id.Value, false, CatalogPriceTestData.Meta("staff-proof"));
        Assert.NotNull(await service.ReadProofAsync(data.Courier, receipt.ShipmentId.Value, false));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DownloadProofAsync(data.Courier, receipt.ShipmentId.Value + 1, issued.Id.Value, false));
        await data.Order.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Order.Buyer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DownloadProofAsync(data.Order.Buyer, receipt.ShipmentId.Value, issued.Id.Value, true));
    }
    [Theory]
    [InlineData("content")] [InlineData("receipt")] [InlineData("root")] [InlineData("audit")] [InlineData("source")]
    public async Task Damaged_proof_or_source_cannot_be_downloaded_recovered_or_reissued(string fault) {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var receipt = await Accept(data); var service = ShipmentReceiptTests.Service(data);
        var meta = CatalogPriceTestData.Meta("proof-integrity"); var issued = await service.IssueProofAsync(data.Order.Buyer, receipt.ShipmentId.Value, receipt.Id.Value, true, meta);
        var row = await data.Order.Rows(IssuedDocumentStore.ProofCollection).Find(new BsonDocument("_id", issued.Id.Value)).SingleAsync();
        if (fault == "content") await data.Order.Rows(IssuedDocumentStore.ProofCollection).UpdateOneAsync(new BsonDocument("_id", issued.Id.Value), Builders<BsonDocument>.Update.Set("content", "damaged").Set("byteLength", 7L));
        if (fault == "root") await data.Order.Rows(IssuedDocumentStore.ProofCollection).DeleteOneAsync(new BsonDocument("_id", issued.Id.Value));
        if (fault == "audit") await data.Order.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", row["auditId"]));
        if (fault == "receipt") await data.Order.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation", IssuedDocumentStore.ProofOperation }, { "key", meta.IdempotencyKey } });
        if (fault == "source") await data.Order.Rows("shipmentReceipts").DeleteOneAsync(new BsonDocument("_id", receipt.Id.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DownloadProofAsync(data.Courier, receipt.ShipmentId.Value, issued.Id.Value, false));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverProofAsync(data.Order.Buyer, meta.IdempotencyKey));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IssueProofAsync(data.Picker, receipt.ShipmentId.Value, receipt.Id.Value, false, CatalogPriceTestData.Meta("no-repair")));
    }
}
