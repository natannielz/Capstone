using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Inventory;

/// <summary>Actual owner commands and Mongo histories. Existing active/on-hand/admission fixtures
/// are synthetic isolation only, not seven-domain activation, production admission or release.</summary>
[Collection("Mongo")]
public sealed class FulfillmentStockSourceTests(MongoFixture fixture)
{
    [Fact]
    public async Task Fresh_independent_setup_initializes_exact_empty_custody_owner_without_activating_or_changing_stock()
    {
        await using var data = await FulfillmentStockSourceTestData.CreateAsync(fixture);
        var pool = await data.PoolAsync(); var head = await data.HeadAsync();
        Assert.False(pool["active"].AsBoolean); Assert.Equal(1L, pool["revision"].AsInt64); Assert.Equal(0L, pool["fence"].AsInt64);
        Assert.Equal(1L, head["revision"].AsInt64); Assert.Equal(0L, head["fence"].AsInt64);
        Assert.Equal(data.Approval!["decisionId"], head["setupDecisionId"]);
        var history = await data.Rows(FulfillmentStockSourceRecords.Transitions).Find(new BsonDocument()).ToListAsync();
        var setup = Assert.Single(history); Assert.Equal("setup", setup["sourceKind"].AsString);
        Assert.Equal(setup["_id"], head["lastTransitionId"]); Assert.Equal(FulfillmentStockSourceRecords.Hash(setup), head["lastHash"].AsString);
        var result = await data.ReadAsync(); Assert.True(result.Complete); Assert.Equal(0L, result.OpenCount); Assert.Equal(1L, result.ProducerRevision);
        Assert.Equal(pool, await data.PoolAsync());
        foreach (var name in new[] { "stockBalances", "movements", "lots", "journalEntries", "shipments", "shipmentReceipts" })
            Assert.Equal(0L, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_dispatch_is_open_until_full_recipient_acceptance_and_exception_stays_open_without_replay_effect(bool exception)
    {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture, packages: 2);
        var poolId = (await data.Order.Rows("reservations").Find(new BsonDocument()).SingleAsync())["allocation"]["poolId"].AsInt64;
        var reader = await data.Order.Prices.Catalog.ActorAsync(920000 + poolId, CatalogAuthoringTestData.Warehouse, Permissions.StockSourceRead);
        var beforeDispatch = await PoolAsync(data, poolId); var beforeHead = await HeadAsync(data, poolId);
        var source = await ShipmentReceiptTests.Ready(data);
        var dispatched = await data.Order.Rows("shipments").Find(new BsonDocument("_id", source.Shipment)).SingleAsync();
        Assert.Equal(new[] { poolId }, FulfillmentStockSourceEvents.PoolIds(dispatched));
        Assert.Equal(beforeDispatch["fence"].AsInt64 + 3, (await PoolAsync(data, poolId))["fence"].AsInt64); // ledger + Ordering + actual custody CAS
        Assert.Equal(beforeHead["revision"].AsInt64 + 1, (await HeadAsync(data, poolId))["revision"].AsInt64);
        Assert.Equal(1L, await OpenAsync(data, poolId, reader));
        var service = ShipmentReceiptTests.Service(data);
        var challenge = await service.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("m92-challenge"));
        var review = await service.ReviewAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!);
        var line = exception ? new ReceiptLineDeclaration(review.Lines[0].SkuId, new(1, 0), new(0, 6), new(0, 6), "Synthetic damaged/missing quantity") :
            new ReceiptLineDeclaration(review.Lines[0].SkuId, new(2, 0), new(0, 0), new(0, 0), null);
        var physical = await SnapshotAsync(data, ["stockBalances", "movements", "lots", "reservations", "journalEntries"]);
        var poolBeforeReceipt = await PoolAsync(data, poolId); var headBeforeReceipt = await HeadAsync(data, poolId);
        var meta = CatalogPriceTestData.Meta("m92-confirm");
        var result = await service.ConfirmAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true, [line], meta);
        Assert.Equal(exception, result.HasException); Assert.Equal(exception ? 1L : 0L, await OpenAsync(data, poolId, reader));
        Assert.Equal(physical, await SnapshotAsync(data, ["stockBalances", "movements", "lots", "reservations", "journalEntries"]));
        var currentPool = await PoolAsync(data, poolId); Assert.Equal(poolBeforeReceipt["revision"], currentPool["revision"]);
        Assert.Equal(poolBeforeReceipt["fence"].AsInt64 + 1, currentPool["fence"].AsInt64);
        Assert.Equal(headBeforeReceipt["revision"].AsInt64 + 1, (await HeadAsync(data, poolId))["revision"].AsInt64);
        var latest = await data.Order.Rows(FulfillmentStockSourceRecords.Transitions).Find(new BsonDocument("poolId", poolId)).Sort(new BsonDocument("revision", -1)).FirstAsync();
        Assert.Equal("receipt", latest["sourceKind"].AsString); Assert.Equal(result.Id.Value, latest["sourceId"].AsInt64);
        Assert.Equal(source.Shipment, latest["shipmentId"].AsInt64); Assert.True(latest["beforeOpen"].AsBoolean); Assert.Equal(exception, latest["afterOpen"].AsBoolean);
        var beforeReplay = await ReplayEffectsAsync(data);
        Assert.Equal(result.Id, (await service.ConfirmAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true, [line], meta)).Id);
        Assert.Equal(result.Id, (await service.RecoverConfirmationAsync(data.Order.Buyer, meta.IdempotencyKey))!.Id);
        Assert.Equal(beforeReplay, await ReplayEffectsAsync(data, orderFenceAdvance: 3));
    }

    [Theory]
    [InlineData("missing-head")]
    [InlineData("missing-outbox")]
    [InlineData("missing-command-receipt")]
    [InlineData("empty-command-result")]
    public async Task Authentic_dispatch_corruption_refuses_custody_read_without_repair_or_read_effects(string fault)
    {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture);
        var sent = await data.DispatchAsync(await data.PreviewAsync(), "m92-actual-dispatch");
        var shipment = await data.Order.Rows("shipments").Find(new BsonDocument("_id", sent["shipmentId"])).SingleAsync();
        var poolId = FulfillmentStockSourceEvents.PoolIds(shipment).Single();
        if (fault == "missing-head") Assert.Equal(1L, (await data.Order.Rows(FulfillmentStockSourceRecords.Heads).DeleteOneAsync(new BsonDocument("_id", poolId))).DeletedCount);
        if (fault == "missing-outbox") Assert.Equal(1L, (await data.Order.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("sourceKey", "fulfillment.shipment.dispatched:" + sent["shipmentId"].AsInt64))).DeletedCount);
        if (fault == "missing-command-receipt") Assert.Equal(1L, (await data.Order.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("key", "m92-actual-dispatch"))).DeletedCount);
        if (fault == "empty-command-result") Assert.Equal(1L, (await data.Order.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument("key", "m92-actual-dispatch"),
            Builders<BsonDocument>.Update.Set("result", new BsonDocument()))).ModifiedCount);
        var actor = await data.Order.Prices.Catalog.ActorAsync(920500, CatalogAuthoringTestData.Warehouse, Permissions.StockSourceRead);
        var before = await EffectsAsync(data);
        var refusal = await Record.ExceptionAsync(() => OpenAsync(data, poolId, actor));
        Assert.NotNull(refusal);
        if (fault is "missing-command-receipt" or "empty-command-result") Assert.Equal("shipment_unavailable", Assert.IsType<AuthorizationDeniedException>(refusal).Code);
        else Assert.Equal(fault == "missing-head" ? "fulfillment_source_obligation_unavailable" : "order_source_obligation_unavailable",
            Assert.IsType<DomainValidationException>(refusal).Code);
        Assert.Equal(before, await EffectsAsync(data));
    }

    [Fact]
    public async Task Dispatch_fences_all_order_pools_but_changes_custody_only_for_its_actual_stock_effect_pool()
    {
        var order = await OrderSubmissionTestData.CreateAsync(fixture);
        try { await order.AddSecondFulfillmentAsync(10); }
        catch { await order.DisposeAsync(); throw; }
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture, attachedOrder: order);
        var poolsBefore = await order.Rows("inventoryPools").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var headsBefore = await order.Rows(FulfillmentStockSourceRecords.Heads).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(2, poolsBefore.Count); Assert.Equal(2, headsBefore.Count);
        var dispatched = await data.DispatchAsync(await data.PreviewAsync(), "m92-one-pool-only");
        var shipment = await order.Rows("shipments").Find(new BsonDocument("_id", dispatched["shipmentId"])).SingleAsync();
        var selected = Assert.Single(FulfillmentStockSourceEvents.PoolIds(shipment));
        var otherPool = Assert.Single(poolsBefore, row => row["_id"] != selected);
        var expectedOtherPool = otherPool.DeepClone().AsBsonDocument;
        expectedOtherPool["fence"] = checked(otherPool["fence"].AsInt64 + 1); // Ordering fences every actual order pool.
        Assert.Equal(expectedOtherPool, await PoolAsync(data, otherPool["_id"].AsInt64));
        Assert.Equal(Assert.Single(headsBefore, row => row["_id"] == otherPool["_id"]), await HeadAsync(data, otherPool["_id"].AsInt64));
        Assert.Equal(0L, await order.Rows(FulfillmentStockSourceRecords.Transitions).CountDocumentsAsync(new BsonDocument {
            { "poolId", otherPool["_id"] }, { "sourceKind", "dispatch" } }));
        Assert.Equal(1L, await order.Rows(FulfillmentStockSourceRecords.Transitions).CountDocumentsAsync(new BsonDocument("sourceKind", "dispatch")));
        var transition = await order.Rows(FulfillmentStockSourceRecords.Transitions).Find(new BsonDocument("sourceKind", "dispatch")).SingleAsync();
        Assert.Equal(selected, transition["poolId"].AsInt64); Assert.Equal(shipment["_id"], transition["shipmentId"]);
        var targetReader = await order.Prices.Catalog.ActorAsync(920900, CatalogAuthoringTestData.Warehouse, Permissions.StockSourceRead);
        Assert.Equal(1L, await OpenAsync(data, selected, targetReader));
    }

    [Fact]
    public async Task Missing_genuine_head_rolls_back_fresh_recipient_confirmation_and_original_key_never_repairs_it()
    {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture);
        var source = await ShipmentReceiptTests.Ready(data); var service = ShipmentReceiptTests.Service(data);
        var row = await data.Order.Rows("shipments").Find(new BsonDocument("_id", source.Shipment)).SingleAsync();
        var poolId = FulfillmentStockSourceEvents.PoolIds(row).Single();
        var challenge = await service.IssueChallengeAsync(data.Courier, source.Shipment, source.Document, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("m92-fresh-receipt-challenge"));
        var review = await service.ReviewAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!);
        ReceiptLineDeclaration[] declarations = [new(review.Lines[0].SkuId, new(1, 0), new(0, 0), new(0, 0), null)];
        var head = await HeadAsync(data, poolId);
        Assert.Equal(1L, (await data.Order.Rows(FulfillmentStockSourceRecords.Heads).DeleteOneAsync(head)).DeletedCount);
        var before = await ReplayEffectsAsync(data);
        Assert.Equal("fulfillment_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => service.ConfirmAsync(
            data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true, declarations.ToImmutableArray(),
            CatalogPriceTestData.Meta("m92-fresh-receipt-refused")))).Code);
        Assert.Equal(before, await ReplayEffectsAsync(data, orderFenceAdvance: 1));
        // Restore only the exact genuine row deliberately removed for the negative test.
        await data.Order.Rows(FulfillmentStockSourceRecords.Heads).InsertOneAsync(head);
        var meta = CatalogPriceTestData.Meta("m92-receipt-original-key");
        var accepted = await service.ConfirmAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true, declarations.ToImmutableArray(), meta);
        Assert.Equal(1L, (await data.Order.Rows(FulfillmentStockSourceRecords.Heads).DeleteOneAsync(new BsonDocument("_id", poolId))).DeletedCount);
        before = await ReplayEffectsAsync(data);
        Assert.Equal(accepted.Id, (await service.RecoverConfirmationAsync(data.Order.Buyer, meta.IdempotencyKey))!.Id);
        Assert.Equal(accepted.Id, (await service.ConfirmAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true, declarations.ToImmutableArray(), meta)).Id);
        Assert.Equal(before, await ReplayEffectsAsync(data, orderFenceAdvance: 3));
        Assert.Equal(0L, await data.Order.Rows(FulfillmentStockSourceRecords.Heads).CountDocumentsAsync(new BsonDocument("_id", poolId)));
    }

    [Fact]
    public async Task Missing_genuine_head_rolls_back_fresh_dispatch_without_stock_receipt_or_producer_repair()
    {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture);
        var poolId = (await data.Order.Rows("reservations").Find(new BsonDocument()).SingleAsync())["allocation"]["poolId"].AsInt64;
        var review = await data.PreviewAsync();
        Assert.Equal(1L, (await data.Order.Rows(FulfillmentStockSourceRecords.Heads).DeleteOneAsync(new BsonDocument("_id", poolId))).DeletedCount);
        var before = await EffectsAsync(data);
        Assert.Equal("fulfillment_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => data.DispatchAsync(review, "m92-refuse-missing-head"))).Code);
        Assert.Equal(before, await EffectsAsync(data));
        Assert.Equal(0L, await data.Order.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "m92-refuse-missing-head")));
    }

    [Fact]
    public async Task Reader_foreign_stale_and_late_grant_expiry_failures_abort_every_fence_without_mutating_the_canonical_pool()
    {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture);
        var poolId = (await data.Order.Rows("reservations").Find(new BsonDocument()).SingleAsync())["allocation"]["poolId"].AsInt64;
        var catalog = data.Order.Prices.Catalog;
        var actor = await catalog.ActorAsync(920600, CatalogAuthoringTestData.Warehouse, Permissions.StockSourceRead);
        var outsider = await catalog.ActorAsync(920601, CatalogAuthoringTestData.OtherOwner, Permissions.StockSourceRead);
        var before = await EffectsAsync(data);
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => OpenAsync(data, poolId, outsider))).Code);
        Assert.Equal("stale_or_inactive_actor", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => OpenAsync(data, poolId, actor with { GrantVersion = 2 }))).Code);
        Assert.Equal(before, await EffectsAsync(data));
        var at = catalog.Clock.UtcNow; var request = 0; var expired = 0;
        var changedGrants = await data.Order.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument { { "userId", actor.UserId }, { "status", "active" } },
            Builders<BsonDocument>.Update.Set("expiresAt", at.AddMinutes(1).UtcDateTime));
        Assert.True(changedGrants.MatchedCount > 0); Assert.Equal(changedGrants.MatchedCount, changedGrants.ModifiedCount);
        before = await EffectsAsync(data); // Preserve the intentional expiry update in the exact rollback baseline.
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "M92FinalReadAuthority-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(value => {
                if (value.DatabaseNamespace.DatabaseName == data.Order.Database.DatabaseNamespace.DatabaseName && request == 0 &&
                    value.Command.GetValue("update", BsonNull.Value) == FulfillmentStockSourceRecords.Heads) request = value.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(value => {
                if (request != 0 && value.RequestId == request && value.Reply.GetValue("nModified", 0).ToInt64() == 1 &&
                    Interlocked.CompareExchange(ref expired, 1, 0) == 0) catalog.Clock.UtcNow = at.AddMinutes(2);
            });
        };
        using var client = new MongoClient(settings); var database = client.GetDatabase(data.Order.Database.DatabaseNamespace.DatabaseName);
        var pool = await PoolAsync(data, poolId); var expected = new StockSourceObligationExpectation(poolId, InventoryGraph.Location(pool["location"].AsBsonDocument), pool["revision"].AsInt64, pool["fence"].AsInt64);
        try {
            var reader = new FulfillmentStockSourceObligationReader(database, new(client), new(database), catalog.Clock);
            Assert.Equal("permission_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new MongoUnitOfWork(client)
                .ExecuteAsync((tx, ct) => reader.ReadAndFenceAsync(tx, actor, expected, ct)))).Code);
            Assert.Equal(1, expired); Assert.Equal(before, await EffectsAsync(data));
        } finally { catalog.Clock.UtcNow = at; }
    }

    private static Task<BsonDocument> PoolAsync(ShipmentDispatchTestData data, long id) => data.Order.Rows("inventoryPools").Find(new BsonDocument("_id", id)).SingleAsync();
    private static Task<BsonDocument> HeadAsync(ShipmentDispatchTestData data, long id) => data.Order.Rows(FulfillmentStockSourceRecords.Heads).Find(new BsonDocument("_id", id)).SingleAsync();
    private static async Task<long> OpenAsync(ShipmentDispatchTestData data, long id, ActorContext? actor = null)
    {
        var pool = await PoolAsync(data, id); var location = InventoryGraph.Location(pool["location"].AsBsonDocument);
        actor ??= await data.Order.Prices.Catalog.ActorAsync(920000 + id, location.ToScope(), Permissions.StockSourceRead);
        var expected = new StockSourceObligationExpectation(id, location, pool["revision"].AsInt64, pool["fence"].AsInt64);
        var result = await new MongoUnitOfWork(data.Order.Prices.Catalog.Client).ExecuteAsync((tx, ct) => new FulfillmentStockSourceObligationReader(
            data.Order.Database, new(data.Order.Prices.Catalog.Client), new(data.Order.Database), data.Order.Prices.Catalog.Clock).ReadAndFenceAsync(tx, actor, expected, ct));
        Assert.True(result.Complete); Assert.Equal(expected, result.Pool); return result.OpenCount;
    }
    private static Task<BsonDocument> EffectsAsync(ShipmentDispatchTestData data) => SnapshotAsync(data,
        [FulfillmentStockSourceRecords.Heads, FulfillmentStockSourceRecords.Transitions, OrderStockSourceRecords.Heads, OrderStockSourceRecords.Transitions,
         ReconciliationStockSourceRecords.Heads, ReconciliationStockSourceRecords.Transitions, "inventoryPools", "stockBalances", "movements", "lots", "reservations",
         "orders", "orderSuborders", "shipments", "shipmentReceiptChallenges", "shipmentReceipts", "auditEvents", "idempotencyRecords", "outboxEvents", "journalEntries",
         "identities", "authorizationGrants", "memberships", "authorizationScopes"]);
    private static async Task<BsonDocument> ReplayEffectsAsync(ShipmentDispatchTestData data, int orderFenceAdvance = 0)
    {
        var result = await EffectsAsync(data);
        // Probe and successful replay recheck current authority and deliberately advance these fences.
        // Keep all other authority fields and every business/stock/producer effect byte-exact.
        foreach (var name in new[] { "identities", "authorizationScopes", "authorizationGrants", "memberships" })
            foreach (var row in result[name].AsBsonArray) row.AsBsonDocument.Remove("fence");
        // The committed document-source read touches this exact order once. Confirm replay
        // commits Probe plus command capture; recovery commits one more. Keep the fence field
        // and require the exact expected delta, including on a later business-write rollback.
        var order = Assert.Single(result["orders"].AsBsonArray.Select(row => row.AsBsonDocument), row => row["_id"] == data.Source.OrderId.Value);
        order["fence"] = checked(order["fence"].AsInt64 - orderFenceAdvance);
        return result;
    }
    private static async Task<BsonDocument> SnapshotAsync(ShipmentDispatchTestData data, string[] names)
    {
        var result = new BsonDocument();
        foreach (var name in names) result.Add(name, new BsonArray(await data.Order.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return result;
    }
}
