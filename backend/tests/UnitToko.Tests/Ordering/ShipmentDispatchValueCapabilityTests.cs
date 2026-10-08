using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentDispatchValueCapabilityTests(MongoFixture fixture)
{
    [Fact]
    public async Task An_actual_dispatch_service_without_a_live_owned_command_cannot_mint_value_and_a_namespace_is_not_ownership() {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var unit = new MongoUnitOfWork(fixture.Client); var ids = new IdAllocator(data.Database);
        var orders = new OrderSubmissionService(data.Database, unit, ids, data.Clock);
        var preparations = new ShipmentPreparationService(data.Database, unit, ids, data.Clock, orders);
        var dispatch = new ShipmentDispatchService(data.Database, unit, ids, data.Clock, orders, preparations);
        var descriptive = new ShipmentDispatchStockSource(data.Database, orders, _ => Task.CompletedTask);
        Assert.Equal("fulfillment.shipment", descriptive.Namespace); Assert.False(ShipmentDispatchService.IsLedgerDispatchSource(descriptive));
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
        var failure = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => LotValueConsumptionProof.CreateAsync(dispatch, data.Database, tx, 10, 20, default));
        Assert.Equal("shipment_unavailable", failure.Code);
        var ancestor = StockReleaseBson.Source(30, 40);
        var callbackFailure = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => dispatch.AuthenticateValueSourceAsync(
            data.Database, tx, 10, 20, new string('a', 64), ancestor, new string('b', 64), default));
        Assert.Equal("shipment_unavailable", callbackFailure.Code);
        foreach (var name in new[] { "orders", "reservations", "shipments", "movements", LotValueStore.Transitions, "journalEntries" })
            Assert.False(await data.Database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
        await tx.AbortTransactionAsync();
    }
    [Theory]
    [InlineData("cycle")] [InlineData("foreign-session")] [InlineData("foreign-client")] [InlineData("foreign-database")]
    [InlineData("next-attempt")] [InlineData("clock-back")]
    public async Task Actual_native_history_context_refuses_cross_command_cycle_foreign_session_next_attempt_and_clock_regression(string fault) {
        await using var data = await StoreTestData.CreateAsync(fixture);
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
        _ = await data.Database.GetCollection<BsonDocument>("shipments").Find(tx, new BsonDocument()).AnyAsync();
        var read = new ShipmentDispatchValueRecords.HistoricalRead(data.Database, data.Clock, tx);
        read.Require(data.Database, tx); read.Enter(1); read.Enter(2);
        var sameDatabase = fixture.Client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        Assert.NotSame(data.Database, sameDatabase);
        Assert.NotSame(data.Database.DatabaseNamespace, sameDatabase.DatabaseNamespace);
        Assert.Equal(data.Database.DatabaseNamespace, sameDatabase.DatabaseNamespace);
        read.Require(sameDatabase, tx);
        if (fault == "cycle") Assert.Equal("shipment_unavailable", Assert.Throws<AuthorizationDeniedException>(() => read.Enter(1)).Code);
        else if (fault == "foreign-database") {
            var otherDatabase = fixture.Client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName + "_foreign");
            Assert.Equal("shipment_unavailable", Assert.Throws<AuthorizationDeniedException>(() => read.Require(otherDatabase, tx)).Code);
        } else if (fault == "foreign-client") {
            using var otherClient = new MongoClient(fixture.Client.Settings.Clone());
            var otherDatabase = otherClient.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
            Assert.Equal("inventory_transaction_required", Assert.Throws<DomainValidationException>(() => read.Require(otherDatabase, tx)).Code);
        }
        else if (fault == "next-attempt") {
            await tx.AbortTransactionAsync(); tx.StartTransaction();
            _ = await data.Database.GetCollection<BsonDocument>("shipments").Find(tx, new BsonDocument()).AnyAsync();
            Assert.Equal("shipment_unavailable", Assert.Throws<AuthorizationDeniedException>(() => read.Require(data.Database, tx)).Code);
        } else if (fault == "clock-back") {
            data.Clock.UtcNow = data.Clock.UtcNow.AddMilliseconds(-1);
            Assert.Equal("shipment_unavailable", Assert.Throws<AuthorizationDeniedException>(() => read.Require(data.Database, tx)).Code);
        } else {
            using var otherClient = new MongoClient(fixture.Client.Settings.Clone());
            using var other = await otherClient.StartSessionAsync(); other.StartTransaction();
            var otherDatabase = otherClient.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
            _ = await otherDatabase.GetCollection<BsonDocument>("shipments").Find(other, new BsonDocument()).AnyAsync();
            Assert.Equal("shipment_unavailable", Assert.Throws<AuthorizationDeniedException>(() => read.Require(otherDatabase, other)).Code);
            await other.AbortTransactionAsync();
        }
        if (tx.IsInTransaction) await tx.AbortTransactionAsync();
        foreach (var name in new[] { "shipments", LotValueStore.Transitions, "journalEntries" })
            Assert.False(await data.Database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
    }
}
