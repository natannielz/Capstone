using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Procurement;

/// <summary>Genuine synthetic-local private inspection and independent setup/policy commands.
/// Only bound held custody is covered. No active pool, inbound completeness or release is asserted.</summary>
[SupportedOSPlatform("windows")]
[Collection("Mongo")]
public sealed class ProcurementStockSourceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Actual_completed_held_receipt_advances_genuine_producer_once_and_partial_reader_never_claims_inbound_completeness()
    {
        await using var data = await ProcurementStockReceiptTestData.CreateAsync(fixture);
        Assert.Equal(93, CollectionMigrations.CurrentVersion);
        var records = new ProcurementStockSourceRecords(data.Database);
        var owner = new ProcurementStockSourceEvents(data.Database, new(data.Database), data.Clock);
        var initial = await data.Unit.ExecuteAsync((tx, ct) => records.HistoryAsync(tx, owner, data.PoolId, ct));
        Assert.Equal(1L, initial.Head["revision"].AsInt64); Assert.Single(initial.History);
        Assert.Equal("setup", initial.History[0]["sourceKind"].AsString); Assert.Empty(initial.Receipts);
        Assert.True(await data.Rows(PurchaseOrderBson.Orders).Find(new BsonDocument()).AnyAsync());
        var reader = new ProcurementStockSourceObligationReader(data.Database, data.Unit, new(data.Database), data.Clock);
        var emptyPool = await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
        var unbound = await data.Unit.ExecuteAsync((tx, ct) => reader.ReadAndFenceAsync(tx, data.SourceChecker,
            new(data.PoolId, InventoryGraph.Location(emptyPool["location"].AsBsonDocument), emptyPool["revision"].AsInt64, emptyPool["fence"].AsInt64), ct));
        Assert.False(unbound.Complete); Assert.Equal(0L, unbound.OpenCount); Assert.Equal(1L, unbound.ProducerRevision);
        var money = await MoneyAsync(data);
        var input = data.Input(); var preview = await data.Service.PreviewAsync(data.Maker, input);
        var beforePool = await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
        var result = await data.Service.ReceiveAsync(data.Maker, input, preview["binding"].AsString,
            ProcurementStockReceiptTestData.Meta("m93-actual-held"));
        var history = await data.Unit.ExecuteAsync((tx, ct) => records.HistoryAsync(tx, owner, data.PoolId, ct));
        Assert.Equal(2L, history.Head["revision"].AsInt64); Assert.Equal(2, history.History.Length);
        var receipt = Assert.Single(history.Receipts).Value;
        Assert.Equal(result["receiptId"], receipt.Record["_id"]); Assert.Equal("heldReceipt", history.History[1]["sourceKind"].AsString);
        Assert.Equal(beforePool["fence"], history.History[1]["beforePoolFence"]);
        Assert.Equal(checked(beforePool["fence"].AsInt64 + 1), history.History[1]["afterPoolFence"].AsInt64);
        Assert.Equal(receipt.Hash, history.History[1]["sourceHash"].AsString);
        Assert.NotNull(receipt.Movement); Assert.Equal((long)input.Packages, receipt.Movement.Current.Balance.Held);
        Assert.Equal(result["lotId"], receipt.Record["lotId"]); Assert.Equal(result["balanceId"], receipt.Record["balanceId"]);
        var command = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", data.Maker.UserId },
            { "operation", ProcurementStockReceiptBson.Operation }, { "key", "m93-actual-held" } }).SingleAsync();
        Assert.Equal(result, command["result"]); Assert.NotEmpty(command["result"].AsBsonDocument);
        var pool = await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
        Assert.False(pool["active"].AsBoolean); Assert.Equal(beforePool["revision"], pool["revision"]);
        Assert.Equal(checked(beforePool["fence"].AsInt64 + 2), pool["fence"].AsInt64);
        var effects = await data.StateAsync(); var head = history.Head.DeepClone().AsBsonDocument;
        Assert.Equal(result, await data.Service.ReceiveAsync(data.Maker, input, preview["binding"].AsString,
            ProcurementStockReceiptTestData.Meta("m93-actual-held")));
        Assert.Equal(effects, await data.StateAsync());
        Assert.Equal(head, await data.Rows(ProcurementStockSourceRecords.Heads).Find(new BsonDocument("_id", data.PoolId)).SingleAsync());
        Assert.Equal(new BsonArray(history.History), new BsonArray(await data.Rows(ProcurementStockSourceRecords.Transitions)
            .Find(new BsonDocument("poolId", data.PoolId)).Sort(new BsonDocument("revision", 1)).ToListAsync()));
        pool = await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
        var expected = new StockSourceObligationExpectation(data.PoolId, InventoryGraph.Location(pool["location"].AsBsonDocument),
            pool["revision"].AsInt64, pool["fence"].AsInt64);
        var partial = await data.Unit.ExecuteAsync((tx, ct) => reader.ReadAndFenceAsync(tx, data.SourceChecker, expected, ct));
        Assert.False(partial.Complete); Assert.Equal(StockSourceObligationDomain.Procurement, partial.Domain);
        Assert.Equal(2L, partial.ProducerRevision); Assert.Equal(1L, partial.OpenCount);
        Assert.Equal(effects, await data.StateAsync());
        Assert.Equal(pool, await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync());
        Assert.Equal(1L, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument { { "kind", ProcurementStockReceiptBson.Operation }, { "requestKey", "m93-actual-held" } }));
        Assert.Equal(money, await MoneyAsync(data));
        var foreign = await data.Base.AddActorAsync(930008, new(GrantScopeKind.FulfillmentWarehouse, 100,
            ShopId: data.Inspection.Receiving.Procurement.ForeignShopId,
            WarehouseId: data.Inspection.Receiving.Procurement.ForeignWarehouseId, ChannelId: 103), Permissions.StockSourceRead);
        var fencedHead = await data.Rows(ProcurementStockSourceRecords.Heads).Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync(
            (tx, ct) => reader.ReadAndFenceAsync(tx, foreign, expected, ct)))).Code);
        Assert.Equal(fencedHead, await data.Rows(ProcurementStockSourceRecords.Heads).Find(new BsonDocument("_id", data.PoolId)).SingleAsync());
        Assert.Equal(effects, await data.StateAsync());
        var pinFilter = new BsonDocument("_id", receipt.Record["inspection"]["pinId"]);
        var pin = await data.Rows("evidencePins").Find(pinFilter).SingleAsync();
        Assert.Equal("ProcurementInspection", pin["associationKind"].AsString);
        Assert.Equal(receipt.Record["inspectionId"], pin["associationId"]);
        Assert.Equal(1L, (await data.Rows("evidencePins").DeleteOneAsync(pin)).DeletedCount);
        try {
            Assert.Equal("evidence_validation_integrity", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Unit.ExecuteAsync(
                (tx, ct) => reader.ReadAndFenceAsync(tx, data.SourceChecker, expected, ct)))).Code);
            Assert.Equal(fencedHead, await data.Rows(ProcurementStockSourceRecords.Heads).Find(new BsonDocument("_id", data.PoolId)).SingleAsync());
            Assert.Equal(effects, await data.StateAsync()); Assert.Equal(money, await MoneyAsync(data));
        } finally { await data.Rows("evidencePins").InsertOneAsync(pin); }
    }

    [WindowsStorageFact]
    public async Task Missing_actual_producer_refuses_fresh_receipt_and_rolls_back_all_physical_effects_without_repair()
    {
        await using var data = await ProcurementStockReceiptTestData.CreateAsync(fixture);
        var head = await data.Rows(ProcurementStockSourceRecords.Heads).Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
        var originalTransitions = new BsonArray(await data.Rows(ProcurementStockSourceRecords.Transitions)
            .Find(new BsonDocument("poolId", data.PoolId)).Sort(new BsonDocument("revision", 1)).ToListAsync());
        Assert.Equal(1L, head["revision"].AsInt64);
        Assert.Equal(1L, (await data.Rows(ProcurementStockSourceRecords.Heads).DeleteOneAsync(head)).DeletedCount);
        var input = data.Input(); var preview = await data.Service.PreviewAsync(data.Maker, input);
        var before = await data.StateAsync();
        var pool = await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
        try {
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ReceiveAsync(data.Maker, input,
                preview["binding"].AsString, ProcurementStockReceiptTestData.Meta("m93-missing-head")));
            Assert.Equal("procurement_stock_receipt_integrity_unavailable", error.Code);
            Assert.Equal(before, await data.StateAsync());
            Assert.Equal(pool, await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync());
            Assert.False(await data.Rows(ProcurementStockSourceRecords.Heads).Find(new BsonDocument("_id", data.PoolId)).AnyAsync());
            Assert.Equal(originalTransitions, new BsonArray(await data.Rows(ProcurementStockSourceRecords.Transitions)
                .Find(new BsonDocument("poolId", data.PoolId)).Sort(new BsonDocument("revision", 1)).ToListAsync()));
            Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", ProcurementStockReceiptBson.Operation },
                { "actorId", data.Maker.UserId }, { "key", "m93-missing-head" } }).AnyAsync());
        } finally { await data.Rows(ProcurementStockSourceRecords.Heads).InsertOneAsync(head); }
    }
    [WindowsStorageFact]
    public async Task MFA_expiring_after_actual_custody_head_write_rolls_back_receipt_stock_and_producer_together()
    {
        await using var data = await ProcurementStockReceiptTestData.CreateAsync(fixture);
        var input = data.Input(); var preview = await data.Service.PreviewAsync(data.Maker, input);
        var before = await data.StateAsync(); var money = await MoneyAsync(data);
        var pool = await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
        var head = await data.Rows(ProcurementStockSourceRecords.Heads).Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
        var transitions = new BsonArray(await data.Rows(ProcurementStockSourceRecords.Transitions)
            .Find(new BsonDocument("poolId", data.PoolId)).Sort(new BsonDocument("revision", 1)).ToListAsync());
        var request = 0; var observed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "M93FinalCustodyMfa-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (request == 0 && command.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName &&
                    command.CommandName == "update" && command.Command.GetValue("update", BsonNull.Value) == ProcurementStockSourceRecords.Heads &&
                    command.Command["updates"].AsBsonArray.Any(update => update["u"].AsBsonDocument.GetValue("$inc", new BsonDocument())
                        .AsBsonDocument.GetValue("revision", 0).ToInt64() == 1)) request = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (request != 0 && command.RequestId == request && command.Reply.GetValue("nModified", 0).ToInt64() == 1 &&
                    Interlocked.CompareExchange(ref observed, 1, 0) == 0) data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
            });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database);
        var catalog = new CatalogAuthoringService(database, unit, ids, data.Clock);
        var requests = new ProcurementRequestService(database, unit, ids, data.Clock, catalog);
        var orders = new PurchaseOrderService(database, unit, ids, data.Clock, catalog, requests,
            new SupplierService(database, unit, ids, data.Clock, catalog));
        var service = new ProcurementStockReceiptService(database, unit, ids, data.Clock, catalog, orders,
            new ProcurementReceivingService(database, unit, ids, data.Clock, catalog, orders, requests),
            new StockSourceSetupService(database, unit, ids, data.Clock));
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReceiveAsync(data.Maker,
            input, preview["binding"].AsString, ProcurementStockReceiptTestData.Meta("m93-final-custody-mfa")))).Code);
        Assert.Equal(1, observed);
        Assert.Equal(before, await data.StateAsync()); Assert.Equal(money, await MoneyAsync(data));
        Assert.Equal(pool, await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync());
        Assert.Equal(head, await data.Rows(ProcurementStockSourceRecords.Heads).Find(new BsonDocument("_id", data.PoolId)).SingleAsync());
        Assert.Equal(transitions, new BsonArray(await data.Rows(ProcurementStockSourceRecords.Transitions)
            .Find(new BsonDocument("poolId", data.PoolId)).Sort(new BsonDocument("revision", 1)).ToListAsync()));
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", ProcurementStockReceiptBson.Operation },
            { "actorId", data.Maker.UserId }, { "key", "m93-final-custody-mfa" } }).AnyAsync());
    }

    private static async Task<BsonDocument> MoneyAsync(ProcurementStockReceiptTestData data)
    {
        var state = new BsonDocument();
        foreach (var name in new[] { "journalEntries", "cashMovements", "procurementDisbursements", "procurementSettlements", "paymentSubmissions" })
            state[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
}
