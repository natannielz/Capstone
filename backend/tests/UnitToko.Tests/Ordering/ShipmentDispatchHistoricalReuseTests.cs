using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ShipmentDispatchHistoricalReuseTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Accepted_dispatch_history_reuses_ancestry_but_freshly_refuses_changed_acquisition_and_rolls_back() {
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, false, reviewedSafetyBufferBaseUnits: 0);
        var review = await data.PrepareDispatchAsync(1, "historical-reuse-preparation");
        var result = await data.DispatchAsync(review, "historical-reuse-dispatch");
        var shipment = await data.Rows("shipments").Find(new BsonDocument("_id", result["shipmentId"])).SingleAsync();
        var original = await data.Rows(ProcurementStockValuationBson.Decisions).Find(new BsonDocument("decision", "approve")).SingleAsync();
        var business = await data.BusinessAsync(); var valuation = await data.Stock.Valuation.StateAsync();
        var warmed = 0; var changed = 0;
        var failure = await Record.ExceptionAsync(() => data.Unit.ExecuteAsync(async (tx, ct) => {
            var history = new ShipmentDispatchValueRecords.HistoricalRead(data.Database, data.Clock, tx);
            var source = history.Sources(data.Database, data.Clock, tx);
            Assert.Same(source, history.Sources(data.Database, data.Clock, tx));
            var records = new ShipmentDispatchRecords(data.Database, data.Clock, history);
            Assert.Equal(shipment, await records.ReadAsync(tx, shipment["_id"].AsInt64, ct)); warmed++;
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Dispatch.RequireLiveValueCaptureAsync(data.Database, data.Clock, tx, shipment, ct));
            var corrupt = original.DeepClone().AsBsonDocument;
            var changedInventoryId = checked(corrupt["accounting"]["inventory"]["accountId"].AsInt64 + 100000);
            Assert.NotEqual(corrupt["policy"]["receiptAccrualAccountId"].AsInt64, changedInventoryId);
            // This negative fault is structurally legal; its original immutable
            // approval hash must still fail fresh owner authentication.
            corrupt["accounting"]["inventory"]["accountId"] = changedInventoryId;
            corrupt["policy"]["inventoryAccountId"] = changedInventoryId;
            Assert.Equal(original["snapshotHash"], corrupt["snapshotHash"]);
            Assert.NotEqual(corrupt["snapshotHash"].AsString, ProcurementStockValuationBson.Hash(corrupt));
            Assert.Equal(1L, (await data.Rows(ProcurementStockValuationBson.Decisions).ReplaceOneAsync(tx,
                new BsonDocument("_id", original["_id"]), corrupt, cancellationToken: ct)).ModifiedCount); changed++;
            await records.ReadAsync(tx, shipment["_id"].AsInt64, ct);
            return true;
        }));
        Assert.True(failure is AuthorizationDeniedException or DomainValidationException or PersistenceConflictException, failure?.ToString());
        Assert.Equal(1, warmed); Assert.Equal(1, changed);
        Assert.Equal(business, await data.BusinessAsync()); Assert.Equal(valuation, await data.Stock.Valuation.StateAsync());
        Assert.Equal(original, await data.Rows(ProcurementStockValuationBson.Decisions).Find(new BsonDocument("_id", original["_id"])).SingleAsync());
    }
}
