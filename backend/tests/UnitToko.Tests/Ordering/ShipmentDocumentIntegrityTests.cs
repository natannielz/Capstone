using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Fulfillment;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentDocumentIntegrityTests(MongoFixture fixture)
{
    [Fact]
    public async Task Concurrent_retries_issue_once_and_a_forged_content_hash_does_not_replace_the_accepted_copy() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture);
        var shipment = (await data.DispatchAsync(await data.PreviewAsync(), "race-document-dispatch"))["shipmentId"].AsInt64;
        var service = ShipmentDocumentTests.Service(data); var meta = CatalogPriceTestData.Meta("race-document");
        var results = await Task.WhenAll(service.IssueAsync(data.Picker, shipment, 1, meta), service.IssueAsync(data.Picker, shipment, 1, meta));
        Assert.Equal(results[0], results[1]); Assert.Equal(1, await data.Order.Rows("issuedDocuments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Order.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.surat-jalan.issued")));
        var updates = Builders<BsonDocument>.Update;
        await data.Order.Rows("issuedDocuments").UpdateOneAsync(new BsonDocument("_id", results[0].Id.Value),
            updates.Set("content", "forged").Set("byteLength", 6L).Set("contentHash", IssuedDocumentStore.Hash("forged")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DownloadAsync(data.Courier, shipment, results[0].Id.Value));
    }

    [Theory]
    [InlineData("receipt")] [InlineData("audit")] [InlineData("stock")] [InlineData("root")]
    public async Task Damaged_accepted_history_cannot_be_read_or_recovered(string fault) {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture);
        var shipment = (await data.DispatchAsync(await data.PreviewAsync(), "damage-document-dispatch"))["shipmentId"].AsInt64;
        var service = ShipmentDocumentTests.Service(data); var result = await service.IssueAsync(data.Picker, shipment, 1, CatalogPriceTestData.Meta("damage-document"));
        if (fault == "receipt") await data.Order.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", ShipmentDocumentService.Operation));
        if (fault == "audit") await data.Order.Rows("auditEvents").DeleteOneAsync(new BsonDocument("operation", ShipmentDocumentService.Operation));
        if (fault == "stock") await data.Order.Rows("movements").DeleteOneAsync(new BsonDocument("source.namespace", "fulfillment.shipment"));
        if (fault == "root") await data.Order.Rows("issuedDocuments").DeleteOneAsync(new BsonDocument("_id", result.Id.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DownloadAsync(data.Courier, shipment, result.Id.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(data.Picker, "damage-document"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IssueAsync(data.Picker, shipment, 1, CatalogPriceTestData.Meta("damage-document")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IssueAsync(data.Picker, shipment, 1, CatalogPriceTestData.Meta("new-key-must-not-repair-history")));
    }

    [Theory]
    [InlineData(false, "membership")] [InlineData(true, "membership")]
    [InlineData(false, "session")] [InlineData(true, "session")]
    [InlineData(false, "deadline")] [InlineData(true, "deadline")]
    public async Task Final_authorization_blocks_download_or_rolls_back_issuance_after_revocation(bool issue, string fault) {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order; var catalog = order.Prices.Catalog;
        var shipment = (await data.DispatchAsync(await data.PreviewAsync(), "document-fence-dispatch"))["shipmentId"].AsInt64;
        var original = issue ? null : await ShipmentDocumentTests.Service(data).IssueAsync(data.Picker, shipment, 1, CatalogPriceTestData.Meta("document-before-race"));
        var actor = issue ? data.Picker : data.Courier;
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = 0; var observed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                // Session writes already serialize after the command's initial identity fence.
                // Revoke before that fence; membership and wall-clock expiry are probed after effects.
                if (requestId == 0 && (issue && fault == "session" ? command.Command.GetValue("find", BsonNull.Value) == "shipments" :
                    issue ? command.Command.GetValue("insert", BsonNull.Value) == "auditEvents" :
                    command.Command.GetValue("find", BsonNull.Value) == "issuedDocuments")) requestId = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                    entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Document authority race did not release");
                }
            });
        };
        using var client = new MongoClient(settings); var db = client.GetDatabase(order.Database.DatabaseNamespace.DatabaseName);
        var orders = new UnitToko.Platform.Ordering.OrderSubmissionService(db, new(client), new(db), catalog.Clock);
        var queries = new ShipmentQueryService(db, new(client), catalog.Clock, orders, new(db, new(client), new(db), catalog.Clock, orders));
        var service = new ShipmentDocumentService(db, new(client), new(db), catalog.Clock, queries);
        var pending = Task.Run(async () => { if (issue) await service.IssueAsync(actor, shipment, 1, CatalogPriceTestData.Meta("document-race"));
            else await service.DownloadAsync(actor, shipment, original!.Id.Value); });
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (fault == "membership") await order.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", actor.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (fault == "session") await catalog.Sessions.RevokeAsync(catalog.Issued[actor.UserId].OpaqueToken, "Synthetic document revocation");
            if (fault == "deadline") catalog.Clock.UtcNow = catalog.Clock.UtcNow.AddHours(9);
        } finally { release.Set(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30))); Assert.Equal(1, observed);
        if (issue) {
            Assert.Equal(0, await order.Rows("issuedDocuments").CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(0, await order.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("operation", ShipmentDocumentService.Operation)));
            Assert.Equal(0, await order.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", ShipmentDocumentService.Operation)));
            Assert.Equal(0, await order.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.surat-jalan.issued")));
        }
    }
}
