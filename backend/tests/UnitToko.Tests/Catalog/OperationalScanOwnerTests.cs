using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;
using UnitToko.Tests.Procurement;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class OperationalScanOwnerTests(MongoFixture fixture)
{
    [Fact]
    public async Task Retirement_after_the_actual_count_receipt_write_cannot_escape_the_final_mapping_fence()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync();
        var counter = await data.ActorAsync(7000, Warehouse, Permissions.StockCount, Permissions.StockRead);
        var assigned = await data.Service.AssignIdentifierAsync(data.Maker, new(sku.Id, 1, sku.CurrentDefinitionId!.Value,
            "gtin", null, "4006381333931", "Operator checked the genuine package label"), Metadata("race-label"));
        var request = new ScanRequest("stocktake", "gtin", "4006381333931", null, null, Warehouse, null, null, null, null, null);
        var selected = Assert.Single(ScanRules.Resolve(request, await data.Service.StocktakeScanChoicesAsync(counter, request, "race-scan", default)).Candidates).Selection!;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var requestId = 0; var observed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "OperationalScanFinalFence-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName &&
                    command.Command.GetValue("insert", BsonNull.Value) == "idempotencyRecords" &&
                    command.Command.GetValue("documents", new BsonArray()).AsBsonArray.Any(row => row["operation"] == "inventory.stock-count.record")) requestId = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (requestId != 0 && command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                    entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Mapping retirement barrier was not released");
                }
            });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new StockCountService(database, new(client), new(database), data.Clock);
        var pending = Task.Run(() => service.RecordCountAsync(counter, new(Warehouse, sku.Id.Value, assigned.SkuRevision, sku.CurrentDefinitionId.Value.Value, 2),
            Metadata("mapping-race-count"), scanSelections: [selected]));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await data.Service.RetireIdentifierAsync(data.Maker, new(sku.Id, assigned.SkuRevision, assigned.IdentifierId, 1,
                "Independent operator retired the label while a count was being committed"), Metadata("retire-during-count"));
        } finally { release.Set(); }
        var failure = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.True(failure is PersistenceConflictException or UnitToko.Modules.Contracts.DomainValidationException, failure?.ToString() ?? "Unexpected successful count");
        Assert.Equal(1, observed); Assert.Equal(0, await data.CountAsync("stockCountObservations"));
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", "inventory.stock-count.record" }, { "key", "mapping-race-count" } }).AnyAsync());
        Assert.False(await data.Rows(OperationalScanCommandBindings.Collection).Find(new BsonDocument("requestKey", "mapping-race-count")).AnyAsync());
        Assert.NotNull((await data.Service.IdentifiersAsync(data.Maker, sku.Id.Value, Audit)).Identifiers.Single(row => row.Id == assigned.IdentifierId).RetiredAt);
    }

    [Fact]
    public async Task Actual_count_binds_the_exact_mapping_and_original_key_through_retirement_and_rejects_missing_sidecars()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync();
        var counter = await data.ActorAsync(7000, Warehouse, Permissions.StockCount, Permissions.StockRead);
        var assigned = await data.Service.AssignIdentifierAsync(data.Maker, new(sku.Id, 1, sku.CurrentDefinitionId!.Value,
            "gtin", null, "4006381333931", "The operator inspected this exact package identifier"), Metadata("count-label"));
        var request = new ScanRequest("stocktake", "gtin", "4006381333931", null, null, Warehouse, null, null, null, null, null);
        var match = Assert.Single(ScanRules.Resolve(request, await data.Service.StocktakeScanChoicesAsync(counter, request, "count-scan", default)).Candidates);
        var proof = match.Selection!; var input = new StockCountInput(Warehouse, sku.Id.Value, assigned.SkuRevision, sku.CurrentDefinitionId.Value.Value, 2);
        var service = new StockCountService(data.Database, new(data.Client), new(data.Database), data.Clock);
        var saved = await service.RecordCountAsync(counter, input, Metadata("scanned-count"), scanSelections: [proof]);
        var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", counter.UserId }, { "operation", "inventory.stock-count.record" }, { "key", "scanned-count" } }).SingleAsync();
        var binding = await data.Rows(OperationalScanCommandBindings.Collection).Find(new BsonDocument("requestKey", "scanned-count")).SingleAsync();
        Assert.Equal("scanned", binding["mode"].AsString);
        Assert.Equal(saved, await service.RecordCountAsync(counter, input, Metadata("scanned-count"), scanSelections: [proof]));
        await SelectionChanged(() => service.RecordCountAsync(counter, input, Metadata("scanned-count")));
        await SelectionChanged(() => service.RecordCountAsync(counter, input, Metadata("scanned-count"), scanSelections:
            [proof with { Identifier = proof.Identifier! with { IdentifierRevision = proof.Identifier.IdentifierRevision + 1 } }]));
        await data.Service.RetireIdentifierAsync(data.Maker, new(sku.Id, assigned.SkuRevision, assigned.IdentifierId, 1,
            "Confirmed package label retirement; keep original observations traceable"), Metadata("retire-count-label"));
        Assert.Equal(saved, await service.RecoverAsync(counter, "scanned-count"));
        Assert.Equal(saved, await service.RecordCountAsync(counter, input, Metadata("scanned-count"), scanSelections: [proof]));
        Assert.Equal("unknown", ScanRules.Resolve(request, await data.Service.StocktakeScanChoicesAsync(counter, request, "retired-count-scan", default)).Kind);
        var fresh = input with { ExpectedSkuRevision = assigned.SkuRevision + 1 };
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.RecordCountAsync(counter, fresh, Metadata("retired-new-write"), scanSelections: [proof]));
        Assert.Equal(1, await data.CountAsync("stockCountObservations"));
        Assert.Equal(receipt, await data.Rows("idempotencyRecords").Find(new BsonDocument("_id", receipt["_id"])).SingleAsync());
        Assert.Equal(1, (await data.Rows(OperationalScanCommandBindings.Collection).DeleteOneAsync(new BsonDocument("_id", binding["_id"]))).DeletedCount);
        await BindingUnavailable(() => service.RecoverAsync(counter, "scanned-count"));
        await BindingUnavailable(() => service.RecordCountAsync(counter, input, Metadata("scanned-count"), scanSelections: [proof]));
        Assert.Equal(1, await data.CountAsync("stockCountObservations"));
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Real_receiving_scan_binds_approved_supplier_namespace_inspection_and_one_held_stock_effect()
    {
        await using var data = await ProcurementStockReceiptTestData.CreateAsync(fixture);
        var input = data.Input(packages: 1); var preview = await data.Service.PreviewAsync(data.Maker, input);
        var product = Assert.Single(preview["source"]["order"]["lines"].AsBsonArray).AsBsonDocument;
        var request = new ScanRequest("receiving", "supplier", product["externalItemCode"].AsString, product["sourceSystem"].AsString,
            product["sourceAccountReference"].AsString, ProcurementRequestTestData.Scope, input.OrderId, input.OrderVersion, input.InspectionId, 1, null);
        var match = Assert.Single(ScanRules.Resolve(request, await data.Service.ScanChoicesAsync(data.Maker, request, default)).Candidates);
        Assert.Null(match.Selection!.Identifier); var proof = Assert.IsType<ApprovedSourceScanSelection>(match.Selection.Source);
        Assert.Equal(input.InspectionLineNumber, proof.InspectionLineNumber); Assert.Equal(input.OrderId, proof.OrderId.Value);
        var wrong = request with { SourceAccount = "unrelated-supplier-account" };
        Assert.Equal("unknown", ScanRules.Resolve(wrong, await data.Service.ScanChoicesAsync(data.Maker, wrong, default)).Kind);
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ReceiveAsync(data.Maker, input, preview["binding"].AsString,
            ProcurementStockReceiptTestData.Meta("wrong-source"), scanSelections: [new(null, proof with { SourceAccount = "unrelated-supplier-account" })]));
        Assert.Equal(before, await data.StateAsync());
        var saved = await data.Service.ReceiveAsync(data.Maker, input, preview["binding"].AsString, ProcurementStockReceiptTestData.Meta("scanned-receive"), scanSelections: [match.Selection]);
        var after = await data.StateAsync();
        Assert.Equal(saved, await data.Service.ReceiveAsync(data.Maker, input, preview["binding"].AsString, ProcurementStockReceiptTestData.Meta("scanned-receive"), scanSelections: [match.Selection]));
        Assert.Equal(saved, await data.Service.LookupAsync(data.Maker, ProcurementStockReceiptTestData.Operation, "scanned-receive"));
        await SelectionChanged(() => data.Service.ReceiveAsync(data.Maker, input, preview["binding"].AsString, ProcurementStockReceiptTestData.Meta("scanned-receive")));
        Assert.Equal(after, await data.StateAsync());
        var held = await data.Rows("stockBalances").Find(new BsonDocument("_id", saved["balanceId"])).SingleAsync();
        Assert.Equal(1L, held["onHand"].AsInt64); Assert.Equal(1L, held["held"].AsInt64); Assert.Equal(0L, held["reserved"].AsInt64);
        Assert.Equal("scanned", (await data.Rows(OperationalScanCommandBindings.Collection).Find(new BsonDocument("requestKey", "scanned-receive")).SingleAsync())["mode"].AsString);
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Real_reserved_finite_stock_uses_confirmed_picking_and_dispatch_mappings_without_duplicate_physical_or_accounting_effects()
    {
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, false, reviewedSafetyBufferBaseUnits: 0);
        var source = await data.Orders.ReadFulfillmentOrderAsync(data.Dispatcher, data.SuborderId);
        var line = Assert.Single(source.Lines);
        var request = new ScanRequest("picking", "sku", line.SkuCode, null, null, null, data.SuborderId, source.Revision, null, null, source.Review);
        var picking = Assert.Single(ScanRules.Resolve(request, await data.Orders.ScanChoicesAsync(data.Dispatcher, request, default)).Candidates).Selection!;
        var prepared = await data.Preparations.SaveAsync(data.Dispatcher, data.SuborderId, 0, source.Review, [new(line.SkuId, 1)],
            GenuineFiniteDispatchTestData.Meta("scanned-picking"), scanSelections: [picking]);
        Assert.Equal(prepared, await data.Preparations.RecoverAsync(data.Dispatcher, "scanned-picking"));
        await SelectionChanged(() => data.Preparations.SaveAsync(data.Dispatcher, data.SuborderId, 0, source.Review, [new(line.SkuId, 1)], GenuineFiniteDispatchTestData.Meta("scanned-picking")));
        var ready = await data.Dispatch.ReadPreparationAsync(data.Dispatcher, data.SuborderId);
        var dispatchRequest = request with { Purpose = "dispatch", DocumentRevision = ready.Source.Revision, SourceReview = ready.Source.Review,
            AuxiliaryId = ready.PreparationId.Value, AuxiliaryRevision = ready.PreparationRevision };
        var dispatched = Assert.Single(ScanRules.Resolve(dispatchRequest, await data.Dispatch.ScanChoicesAsync(data.Dispatcher, dispatchRequest, default)).Candidates).Selection!;
        var review = await data.Dispatch.PreviewAsync(data.Dispatcher, data.SuborderId, ready.PreparationId.Value, ready.PreparationRevision, data.Courier.UserId,
            [new(Assert.Single(ready.Lots).Reservation.ReservationId, 1)]);
        var before = await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        var saved = await data.Dispatch.DispatchAsync(data.Dispatcher, data.SuborderId, ready.PreparationId.Value, ready.PreparationRevision, data.Courier.UserId,
            review.Selected, review.Review, GenuineFiniteDispatchTestData.Meta("scanned-dispatch"), scanSelections: [dispatched]);
        var after = await data.BusinessAsync();
        Assert.Equal(saved, await data.Dispatch.RecoverAsync(data.Dispatcher, "scanned-dispatch"));
        Assert.Equal(saved, await data.Dispatch.DispatchAsync(data.Dispatcher, data.SuborderId, ready.PreparationId.Value, ready.PreparationRevision, data.Courier.UserId,
            review.Selected, review.Review, GenuineFiniteDispatchTestData.Meta("scanned-dispatch"), scanSelections: [dispatched]));
        await SelectionChanged(() => data.DispatchAsync(review, "scanned-dispatch")); Assert.Equal(after, await data.BusinessAsync());
        var current = await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        Assert.Equal(before["onHand"].AsInt64 - 1, current["onHand"].AsInt64); Assert.Equal(0L, current["reserved"].AsInt64);
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", "fulfillment.shipment")));
        foreach (var key in new[] { "scanned-picking", "scanned-dispatch" })
            Assert.Equal("scanned", (await data.Rows(OperationalScanCommandBindings.Collection).Find(new BsonDocument("requestKey", key)).SingleAsync())["mode"].AsString);
    }

    private static async Task SelectionChanged(Func<Task> action) => Assert.Equal("operational_scan_selection_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(action)).Code);
    private static async Task BindingUnavailable(Func<Task> action) => Assert.Equal("operational_scan_binding_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(action)).Code);
}
