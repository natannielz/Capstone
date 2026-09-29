using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Fulfillment;
using UnitToko.Tests.Catalog;
namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class ShipmentReceiptProofRaceTests(MongoFixture fixture)
{
    [Fact]
    public async Task Competing_authorized_issuers_create_one_canonical_proof_and_one_effect_set() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var receipt = await ShipmentReceiptProofTests.Accept(data); var service = ShipmentReceiptTests.Service(data);
        async Task<bool> Attempt(bool recipient) {
            try { await service.IssueProofAsync(recipient ? data.Order.Buyer : data.Picker, receipt.ShipmentId.Value, receipt.Id.Value, recipient, CatalogPriceTestData.Meta(recipient ? "recipient-wins" : "staff-wins")); return true; }
            catch (UnitToko.Platform.Mongo.PersistenceConflictException) { return false; }
        }
        var outcomes = await Task.WhenAll(Attempt(true), Attempt(false)); Assert.Single(outcomes, success => success);
        Assert.Equal(1, await data.Order.Rows(IssuedDocumentStore.ProofCollection).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Order.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", IssuedDocumentStore.ProofOperation)));
        Assert.Equal(1, await data.Order.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", IssuedDocumentStore.ProofKind + ".issued")));
        Assert.Equal(1, await data.Order.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("operation", IssuedDocumentStore.ProofOperation)));
        Assert.Equal(2, await data.Order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Recipient_membership_revoked_during_private_download_cannot_return_the_file() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var receipt = await ShipmentReceiptProofTests.Accept(data);
        var proof = await ShipmentReceiptTests.Service(data).IssueProofAsync(data.Order.Buyer, receipt.ShipmentId.Value, receipt.Id.Value, true, CatalogPriceTestData.Meta("download-race"));
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var request = 0; var observed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (request == 0 && command.Command.GetValue("find", BsonNull.Value) == IssuedDocumentStore.ProofCollection) request = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => { if (command.RequestId == request && Interlocked.CompareExchange(ref observed, 1, 0) == 0) { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Download race did not release"); } });
        };
        using var client = new MongoClient(settings); var db = client.GetDatabase(data.Order.Database.DatabaseNamespace.DatabaseName); var clock = data.Order.Prices.Catalog.Clock;
        var orders = new UnitToko.Platform.Ordering.OrderSubmissionService(db, new(client), new(db), clock);
        var queries = new ShipmentQueryService(db, new(client), clock, orders, new(db, new(client), new(db), clock, orders));
        var service = new ShipmentReceiptService(db, new(client), new(db), clock, queries, new(db, new(client), new(db), clock, queries));
        var pending = Task.Run(() => service.DownloadProofAsync(data.Order.Buyer, receipt.ShipmentId.Value, proof.Id.Value, true));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await data.Order.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Order.Buyer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        } finally { release.Set(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30))); Assert.Equal(1, observed);
    }
    [Theory]
    [InlineData("membership")] [InlineData("person")] [InlineData("expiry")]
    public async Task Final_authority_fences_roll_back_proof_and_document_effects(string fault) {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var receipt = await ShipmentReceiptProofTests.Accept(data);

        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var request = 0; var observed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (request == 0 && command.Command.GetValue("insert", BsonNull.Value) == IssuedDocumentStore.ProofCollection) request = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => { if (command.RequestId == request && Interlocked.CompareExchange(ref observed, 1, 0) == 0) { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Receipt race did not release"); } });
        };
        using var client = new MongoClient(settings); var db = client.GetDatabase(data.Order.Database.DatabaseNamespace.DatabaseName); var clock = data.Order.Prices.Catalog.Clock;
        var orders = new UnitToko.Platform.Ordering.OrderSubmissionService(db, new(client), new(db), clock);
        var queries = new ShipmentQueryService(db, new(client), clock, orders, new(db, new(client), new(db), clock, orders));
        var service = new ShipmentReceiptService(db, new(client), new(db), clock, queries, new(db, new(client), new(db), clock, queries));
        var pending = Task.Run(() => service.IssueProofAsync(data.Order.Buyer, receipt.ShipmentId.Value, receipt.Id.Value, true, CatalogPriceTestData.Meta("fenced-proof")));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (fault == "membership") await data.Order.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Order.Buyer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (fault == "person") await data.Order.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", data.Courier.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (fault == "expiry") clock.UtcNow = clock.UtcNow.AddHours(9);
        } finally { release.Set(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30))); Assert.Equal(1, observed);
        Assert.Equal(0, await data.Order.Rows(IssuedDocumentStore.ProofCollection).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Order.Rows("shipmentReceipts").CountDocumentsAsync(new BsonDocument()));
        foreach (var collection in new[] { "auditEvents", "idempotencyRecords" }) Assert.Equal(0, await data.Order.Rows(collection).CountDocumentsAsync(new BsonDocument("operation", IssuedDocumentStore.ProofOperation)));
    }
}
