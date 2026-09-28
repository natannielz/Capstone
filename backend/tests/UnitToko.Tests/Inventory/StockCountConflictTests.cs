using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockCountTests
{
    [Fact]
    public async Task Exhausted_transaction_conflicts_leave_no_count_and_same_key_can_be_submitted_after_contention()
    {
        await using var data = await Setup();
        var input = Input(data);
        var beforePhysical = await Physical(data);
        var settings = fixture.Client.Settings.Clone();
        settings.ApplicationName = "StockCountConflict-" + Guid.NewGuid().ToString("N");
        var updates = 0;
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command =>
        { if (command.CommandName == "update") Interlocked.Increment(ref updates); });
        var client = new MongoClient(settings);
        var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new StockCountService(db, new(client), new(db), data.Clock);
        try
        {
            await fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "configureFailPoint", "failCommand" }, { "mode", "alwaysOn" }, { "data", new BsonDocument
                    {
                        { "appName", settings.ApplicationName }, { "failCommands", new BsonArray { "update" } },
                        { "errorCode", 112 }, { "errorLabels", new BsonArray { "TransientTransactionError" } }
                    } }
            });
            var conflict = await Assert.ThrowsAsync<PersistenceConflictException>(() =>
                service.RecordCountAsync(data.WarehouseActor, input, Metadata("bounded-conflict")));
            Assert.Equal("version_conflict", conflict.Code);
            Assert.Equal(MongoUnitOfWork.MaximumAttempts, Volatile.Read(ref updates));
        }
        finally
        {
            await fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                new BsonDocument { { "configureFailPoint", "failCommand" }, { "mode", "off" } });
        }
        Assert.Equal(0, await data.CountAsync("stockCountObservations"));
        Assert.Equal(0, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "inventory.stock-count.recorded")));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "inventory.stock-count.record")));
        Assert.Equal(beforePhysical, await Physical(data));
        Assert.Null(await service.RecoverAsync(data.WarehouseActor, "bounded-conflict"));
        var saved = await service.RecordCountAsync(data.WarehouseActor, input, Metadata("bounded-conflict"));
        Assert.Equal(saved, await service.RecoverAsync(data.WarehouseActor, "bounded-conflict"));
        Assert.Equal(saved, await service.RecordCountAsync(data.WarehouseActor, input, Metadata("bounded-conflict")));
        Assert.Equal(1, await data.CountAsync("stockCountObservations"));
        Assert.Equal(1, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "inventory.stock-count.recorded")));
        Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "inventory.stock-count.record")));
        Assert.Equal(beforePhysical, await Physical(data));
    }
}
