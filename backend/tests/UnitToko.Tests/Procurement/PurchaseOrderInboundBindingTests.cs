using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;
using I = UnitToko.Platform.Procurement.PurchaseOrderInboundBindingBson;

namespace UnitToko.Tests.Procurement;

// Genuine registered migration94, explicit PO planning and native private custody.
[Collection("Mongo")]
public sealed class PurchaseOrderInboundBindingTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Actual_selected_approved_PO_propagates_through_private_inspection_without_backfill_activation_or_money_and_replays_exactly() {
        await using var data = await PurchaseOrderInboundBindingTestData.CreateAsync(fixture);
        Assert.Equal(94, CollectionMigrations.CurrentVersion);
        var legacy = await data.Rows(PurchaseOrderTestData.Orders).Find(new BsonDocument()).SingleAsync();
        var proposal = await data.ProposeAsync("bound-po"); var approved = await data.ApproveAsync(proposal, "bound-po-approved");
        var selection = await data.Rows(I.Selections).Find(new BsonDocument()).SingleAsync();
        var decision = await data.Rows(I.Decisions).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(proposal["proposalId"], selection["proposalId"]); Assert.Equal(approved["decisionId"], decision["_id"]);
        Assert.Equal(approved["orderId"], decision["orderId"]); Assert.Equal(selection["snapshotHash"], decision["selectionHash"]);
        var proposedRow = await data.Rows(PurchaseOrderTestData.Proposals).Find(new BsonDocument("_id", proposal["proposalId"])).SingleAsync();
        var approvedRow = await data.Rows(PurchaseOrderTestData.Decisions).Find(new BsonDocument("_id", approved["decisionId"])).SingleAsync();
        Assert.Equal(proposal, await data.Service.ProposeInboundAsync(data.Maker, data.Input, data.Selection,
            proposedRow["binding"].AsString, PurchaseOrderTestData.Meta("bound-po")));
        Assert.Equal(approved, await data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1,
            PurchaseOrderDecision.Approve, approvedRow["binding"].AsString, PurchaseOrderTestData.Meta("bound-po-approved")));
        Assert.Equal(selection, await data.Rows(I.Selections).Find(new BsonDocument()).SingleAsync());
        Assert.Equal(decision, await data.Rows(I.Decisions).Find(new BsonDocument()).SingleAsync());
        Assert.Equal(legacy, await data.Rows(PurchaseOrderTestData.Orders).Find(new BsonDocument("_id", legacy["_id"])).SingleAsync());
        var receiving = data.Data.Inspection.Receiving;
        var noticeInput = receiving.NoticeInput("SYNTHETIC-BOUND-NOTICE") with { OrderId = approved["orderId"].AsInt64, OrderApprovalId = approved["decisionId"].AsInt64 };
        var noticePreview = await receiving.Service.PreviewNoticeAsync(data.Maker, noticeInput);
        var notice = await receiving.Service.RecordNoticeAsync(data.Maker, noticeInput, noticePreview["binding"].AsString, ProcurementReceivingTestData.Meta("bound-notice"));
        var intakeInput = receiving.IntakeInput("SYNTHETIC-BOUND-CONTAINERS");
        var intakePreview = await receiving.Service.PreviewIntakeAsync(data.Maker, notice["noticeId"].AsInt64, 0, intakeInput);
        var intake = await receiving.Service.ReceiveAsync(data.Maker, notice["noticeId"].AsInt64, 0, intakeInput, intakePreview["binding"].AsString, ProcurementReceivingTestData.Meta("bound-intake"));
        var inspection = data.Data.Inspection; inspection.Actor = data.Maker;
        var inspectionInput = inspection.Input("SYNTHETIC-BOUND-UNBOXING") with { NoticeId = notice["noticeId"].AsInt64, IntakeId = intake["receiptId"].AsInt64 };
        inspection.Draft = await inspection.DraftAsync("bound-inspection-draft", inspectionInput);
        await inspection.UploadAsync(); await inspection.ValidateAsync(); var submitted = await inspection.SubmitAsync(await inspection.PreviewSubmitAsync(), "bound-inspection-submitted");
        var actual = await data.Rows(ProcurementInspectionTestData.Submissions).Find(new BsonDocument("_id", submitted["inspectionId"])).SingleAsync();
        Assert.Equal(approved["orderId"], actual["source"]["notice"]["source"]["order"]["_id"]);
        Assert.Equal(notice["noticeId"], actual["noticeId"]); Assert.Equal(intake["receiptId"], actual["intakeId"]);
        Assert.NotEqual(BsonNull.Value, actual["pinId"]); Assert.Equal("submitted_quarantine", actual["state"].AsString);
        var view = await data.Service.ReadAsync(data.Maker, proposal["proposalId"].AsInt64);
        Assert.Equal("approve", view["inboundPools"]["state"].AsString);
        Assert.False(view["inboundPools"]["unplannedPackagesBound"].AsBoolean); Assert.False(view["inboundPools"]["physicalStockReleased"].AsBoolean);
        Assert.False((await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.Data.PoolId)).SingleAsync())["active"].AsBoolean);
        await data.Orders.NoFinancialOrReceiptEffectsAsync();
        // The old PO stays explicitly unbound; no later held pool is attached to it.
        Assert.False(await data.Rows(I.Selections).Find(new BsonDocument("proposalId", legacy["proposalId"])).AnyAsync());
        var restart = new PurchaseOrderService(data.Data.Database, data.Data.Unit, new(data.Data.Database), data.Data.Clock,
            data.Orders.Procurement.Catalog, data.Orders.Procurement.Service, data.Orders.Suppliers.Service);
        Assert.Equal(view["inboundPools"], (await restart.ReadAsync(data.Maker, proposal["proposalId"].AsInt64))["inboundPools"]);
        // Actual observed packages exceed the planned2. Only the selected planned
        // subset can enter this pool; the extra package remains unresolved custody.
        var receiptInput = data.Data.Input("SYNTHETIC-BOUND-HELD-RECEIPT", 2) with {
            OrderId = approved["orderId"].AsInt64, OrderApprovalId = approved["decisionId"].AsInt64, InspectionId = submitted["inspectionId"].AsInt64 };
        var receiptPreview = await data.Data.Service.PreviewAsync(data.Maker, receiptInput);
        var receipt = await data.Data.Service.ReceiveAsync(data.Maker, receiptInput, receiptPreview["binding"].AsString,
            ProcurementStockReceiptTestData.Meta("bound-held-receipt", receiptInput.Reason));
        Assert.Equal(receipt, await data.Data.Service.ReceiveAsync(data.Maker, receiptInput, receiptPreview["binding"].AsString,
            ProcurementStockReceiptTestData.Meta("bound-held-receipt", receiptInput.Reason)));
        var balance = await data.Rows("stockBalances").Find(new BsonDocument("_id", receipt["balanceId"])).SingleAsync();
        Assert.Equal(2L, balance["onHand"].AsInt64); Assert.Equal(2L, balance["held"].AsInt64); Assert.Equal(0L, balance["reserved"].AsInt64);
        var excess = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Data.Service.PreviewAsync(data.Maker,
            receiptInput with { Packages = 1, ReceiptReference = "SYNTHETIC-UNPLANNED-EXCESS" }));
        Assert.Equal("procurement_stock_receipt_inbound_capacity_exceeded", excess.Code);
        Assert.Single(await data.Rows("procurementStockReceipts").Find(new BsonDocument()).ToListAsync());
        foreach (var name in new[] { "journalEntries", "invoices", "paymentAllocations", "refundPayouts", "disbursements", "bankReceipts" })
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
    }
    [WindowsStorageFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Selected_path_requires_real_stock_scope_and_reconstructs_exact_selection_without_repair_or_retroactive_pool_assignment() {
        await using var data = await PurchaseOrderInboundBindingTestData.CreateAsync(fixture);
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewInboundAsync(data.Orders.Maker, data.Input, data.Selection));
        Assert.Equal("permission_denied", denied.Code);
        Assert.False(await data.Rows(I.Selections).Find(new BsonDocument()).AnyAsync());
        var proposal = await data.ProposeAsync("bound-refusal-po"); _ = await data.ApproveAsync(proposal, "bound-refusal-approved");
        var actual = await data.Rows(I.Selections).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(1L, (await data.Rows(I.Selections).DeleteOneAsync(actual)).DeletedCount);
        try {
            var broken = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ReadAsync(data.Maker, proposal["proposalId"].AsInt64));
            Assert.Equal("purchase_order_integrity_unavailable", broken.Code);
            Assert.False(await data.Rows(I.Selections).Find(new BsonDocument()).AnyAsync());
            Assert.Single(await data.Rows(I.Decisions).Find(new BsonDocument()).ToListAsync());
        } finally { await data.Rows(I.Selections).InsertOneAsync(actual); }
        Assert.Equal("approve", (await data.Service.ReadAsync(data.Maker, proposal["proposalId"].AsInt64))["inboundPools"]["state"].AsString);
        await data.Orders.NoFinancialOrReceiptEffectsAsync();
    }
    [WindowsStorageFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Two_real_bound_receipts_cannot_spend_the_same_planned_packages_and_original_keys_never_reopen_capacity() {
        await using var data = await PurchaseOrderInboundBindingTestData.CreateAsync(fixture);
        var first = await data.CreateBoundReceiptInputAsync("bound-capacity-race");
        var second = first with { ReceiptReference = "SYNTHETIC-BOUND-CAPACITY-SECOND" };
        var inputs = new[] { first, second }; var keys = new[] { "bound-full2-a", "bound-full2-b" };
        var previews = new[] { await data.Data.Service.PreviewAsync(data.Maker, first), await data.Data.Service.PreviewAsync(data.Maker, second) };
        Assert.Equal(3L, previews[0]["capacity"]["remainingPackages"].AsInt64); Assert.Equal(3L, previews[1]["capacity"]["remainingPackages"].AsInt64);
        var beforePool = await SafetyPoolAsync(data); var beforeHead = await data.Rows(ProcurementStockSourceRecords.Heads).Find(new BsonDocument("_id", data.Data.PoolId)).SingleAsync();
        var beforeTransitions = await data.Rows(ProcurementStockSourceRecords.Transitions).Find(new BsonDocument("poolId", data.Data.PoolId)).ToListAsync();
        var results = new BsonDocument?[2];
        var errors = await Task.WhenAll(Enumerable.Range(0, 2).Select(async at => await Record.ExceptionAsync(async () => {
            results[at] = await data.Data.Service.ReceiveAsync(data.Maker, inputs[at], previews[at]["binding"].AsString,
                ProcurementStockReceiptTestData.Meta(keys[at], inputs[at].Reason));
        })));
        Assert.Single(errors, error => error is null);
        var lost = Assert.IsType<PersistenceConflictException>(Assert.Single(errors, error => error is not null));
        Assert.Contains(lost.Code, new[] { "version_conflict", "procurement_stock_receipt_capacity_exceeded", "procurement_stock_receipt_inbound_capacity_exceeded", "procurement_stock_receipt_changed" });
        var winner = Array.FindIndex(errors, error => error is null); var loser = 1 - winner; var committed = Assert.IsType<BsonDocument>(results[winner]);
        Assert.Null(results[loser]);
        var receipt = await data.Rows("procurementStockReceipts").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(committed["receiptId"], receipt["_id"]); Assert.Equal(first.OrderId, receipt["orderId"].AsInt64);
        Assert.Equal(keys[winner], receipt["requestKey"].AsString); Assert.Equal(2, receipt["input"]["packages"].AsInt32);
        Assert.Single(await data.Rows("lots").Find(new BsonDocument()).ToListAsync());
        var balance = await data.Rows("stockBalances").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(committed["balanceId"], balance["_id"]); Assert.Equal(2L, balance["onHand"].AsInt64); Assert.Equal(2L, balance["held"].AsInt64); Assert.Equal(0L, balance["reserved"].AsInt64);
        Assert.Single(await data.Rows("movements").Find(new BsonDocument()).ToListAsync());
        Assert.Single(await data.Rows("procurementQuarantineDispositions").Find(new BsonDocument()).ToListAsync());
        var afterHead = await data.Rows(ProcurementStockSourceRecords.Heads).Find(new BsonDocument("_id", data.Data.PoolId)).SingleAsync();
        var transitions = await data.Rows(ProcurementStockSourceRecords.Transitions).Find(new BsonDocument("poolId", data.Data.PoolId)).Sort(new BsonDocument("revision", 1)).ToListAsync();
        Assert.Equal(beforeTransitions.Count + 1, transitions.Count);
        var actual = transitions[^1]; Assert.Equal("heldReceipt", actual["sourceKind"].AsString); Assert.Equal(receipt["_id"], actual["receiptId"]);
        Assert.Equal(beforePool["fence"], actual["beforePoolFence"]); Assert.Equal(beforePool["fence"].AsInt64 + 1, actual["afterPoolFence"].AsInt64);
        var expectedPool = beforePool.DeepClone().AsBsonDocument; expectedPool["fence"] = beforePool["fence"].AsInt64 + 2;
        Assert.Equal(expectedPool, await SafetyPoolAsync(data)); // actual held-writer CAS and final source CAS, no activation/revision edit
        var expectedHead = beforeHead.DeepClone().AsBsonDocument;
        expectedHead["revision"] = beforeHead["revision"].AsInt64 + 1; expectedHead["fence"] = beforeHead["fence"].AsInt64 + 1;
        expectedHead["lastTransitionId"] = actual["_id"]; expectedHead["lastHash"] = actual["transitionHash"]; expectedHead["updatedAt"] = actual["recordedAt"];
        Assert.Equal(expectedHead, afterHead);
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") }) {
            Assert.Equal(1L, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, ProcurementStockReceiptTestData.Operation)));
            var keyField = table == "idempotencyRecords" ? "key" : "requestKey";
            Assert.False(await data.Rows(table).Find(new BsonDocument { { field, ProcurementStockReceiptTestData.Operation }, { keyField, keys[loser] } }).AnyAsync());
        }
        // Full2 retries are the original exact body/key. No broad catch or new key
        // is used to turn a bounded contention result into a second successful write.
        var beforeFailed = await SafetyStateAsync(data);
        var rejected = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Data.Service.ReceiveAsync(data.Maker,
            inputs[loser], previews[loser]["binding"].AsString, ProcurementStockReceiptTestData.Meta(keys[loser], inputs[loser].Reason)));
        Assert.Equal("procurement_stock_receipt_capacity_exceeded", rejected.Code);
        Assert.Equal(beforeFailed, await SafetyStateAsync(data));
        Assert.Null(await data.Data.Service.LookupAsync(data.Maker, ProcurementStockReceiptTestData.Operation, keys[loser]));
        Assert.Equal(beforeFailed, await SafetyStateAsync(data));
        // There is still one genuinely observed package, so this checks the NEW
        // planned bound, rather than relying only on physical-disposition capacity.
        var extra = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Data.Service.PreviewAsync(data.Maker,
            first with { Packages = 1, ReceiptReference = "SYNTHETIC-UNPLANNED-REMAINDER" }));
        Assert.Equal("procurement_stock_receipt_inbound_capacity_exceeded", extra.Code);
        Assert.Equal(beforeFailed, await SafetyStateAsync(data));
        var beforeReplayPool = await SafetyPoolAsync(data);
        Assert.Equal(committed, await data.Data.Service.ReceiveAsync(data.Maker, inputs[winner], previews[winner]["binding"].AsString,
            ProcurementStockReceiptTestData.Meta(keys[winner], inputs[winner].Reason)));
        // The established existing-key path intentionally fences the actual pool
        // once as a read. Every other pool byte and the producer head stay exact.
        var expectedReplayPool = beforeReplayPool.DeepClone().AsBsonDocument; expectedReplayPool["fence"] = beforeReplayPool["fence"].AsInt64 + 1;
        Assert.Equal(expectedReplayPool, await SafetyPoolAsync(data));
        Assert.Equal(afterHead, await data.Rows(ProcurementStockSourceRecords.Heads).Find(new BsonDocument("_id", data.Data.PoolId)).SingleAsync());
        Assert.Equal(new BsonArray(transitions), new BsonArray(await data.Rows(ProcurementStockSourceRecords.Transitions).Find(new BsonDocument("poolId", data.Data.PoolId)).Sort(new BsonDocument("revision", 1)).ToListAsync()));
        Assert.Equal(receipt, await data.Rows("procurementStockReceipts").Find(new BsonDocument()).SingleAsync());
        Assert.Equal(balance, await data.Rows("stockBalances").Find(new BsonDocument()).SingleAsync());
        Assert.False((await SafetyPoolAsync(data))["active"].AsBoolean);
        foreach (var name in new[] { "journalEntries", "paymentAllocations", "invoices", "procurementDisbursements", "procurementSettlements", "refundPayouts" })
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
    }
    [WindowsStorageFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task MFA_expiring_after_actual_selected_pool_CAS_aborts_independent_PO_approval_and_all_bound_commitments_without_repair() {
        await using var data = await PurchaseOrderInboundBindingTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync("bound-final-factor-proposal");
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, PurchaseOrderDecision.Approve);
        var before = await SafetyStateAsync(data); var originalTime = data.Data.Clock.UtcNow; var pool = await SafetyPoolAsync(data);
        var captured = 0; var fired = 0; BsonDocument? actualPoolFilter = null;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "M94BoundApprovalFinalMfa-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(value => {
                if (Volatile.Read(ref captured) != 0 || value.DatabaseNamespace.DatabaseName != data.Data.Database.DatabaseNamespace.DatabaseName ||
                    value.CommandName != "update" || value.Command.GetValue("update", BsonNull.Value) != "inventoryPools") return;
                foreach (var update in value.Command["updates"].AsBsonArray) {
                    var query = update["q"].AsBsonDocument; var change = update["u"].AsBsonDocument;
                    if (query.GetValue("_id", BsonNull.Value) == data.Data.PoolId && change.GetValue("$inc", new BsonDocument()).AsBsonDocument.GetValue("fence", BsonNull.Value) == 1L &&
                        !change["$inc"].AsBsonDocument.Contains("revision") && Interlocked.CompareExchange(ref captured, value.RequestId, 0) == 0) {
                        actualPoolFilter = query.DeepClone().AsBsonDocument; break;
                    }
                }
            });
            builder.Subscribe<CommandSucceededEvent>(value => {
                if (captured != 0 && value.RequestId == captured && value.Reply.GetValue("nModified", BsonNull.Value) == 1 && Interlocked.CompareExchange(ref fired, 1, 0) == 0)
                    data.Data.Clock.UtcNow = originalTime.AddMinutes(6);
            });
        };
        using var client = new MongoClient(settings); var database = client.GetDatabase(data.Data.Database.DatabaseNamespace.DatabaseName);
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database); var catalog = new CatalogAuthoringService(database, unit, ids, data.Data.Clock);
        var requests = new ProcurementRequestService(database, unit, ids, data.Data.Clock, catalog);
        var service = new PurchaseOrderService(database, unit, ids, data.Data.Clock, catalog, requests, new SupplierService(database, unit, ids, data.Data.Clock, catalog));
        const string key = "bound-final-factor-approval";
        try {
            var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64,
                1, PurchaseOrderDecision.Approve, preview["binding"].AsString, PurchaseOrderTestData.Meta(key)));
            Assert.Equal("step_up_required", error.Code); Assert.Equal(1, fired); Assert.Equal(pool, actualPoolFilter);
            // No normalization: actual pool, producer, commitments, decision,
            // order, side owners, receipt, audit, outbox and custody rows all exact.
            Assert.Equal(before, await SafetyStateAsync(data));
            Assert.False(await data.Rows(I.Decisions).Find(new BsonDocument("proposalId", proposal["proposalId"])).AnyAsync());
            Assert.False(await data.Rows(PurchaseOrderTestData.Decisions).Find(new BsonDocument("proposalId", proposal["proposalId"])).AnyAsync());
            Assert.False(await data.Rows(PurchaseOrderTestData.Orders).Find(new BsonDocument("proposalId", proposal["proposalId"])).AnyAsync());
            Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", data.Checker.UserId }, { "operation", PurchaseOrderTestData.Prefix + "approve" }, { "key", key } }).AnyAsync());
        } finally { data.Data.Clock.UtcNow = originalTime; }
        Assert.Null(await data.Service.LookupAsync(data.Checker, PurchaseOrderTestData.Prefix + "approve", key));
        Assert.Equal(pool, await SafetyPoolAsync(data));
        Assert.Equal(before, await SafetyStateAsync(data));
        Assert.Equal("proposed", (await data.Service.ReadAsync(data.Maker, proposal["proposalId"].AsInt64))["inboundPools"]["state"].AsString);
        Assert.Equal(pool, await SafetyPoolAsync(data));
    }
    [WindowsStorageFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Current_runtime_PO_custody_reader_and_receipt_refuse_damaged94_storage_without_legacy_fallback_or_repair() {
        // Actual current94 setup, selected PO, independent approval and private
        // inspection; synthetic local inputs never activate the reviewed pool.
        await using var data = await PurchaseOrderInboundBindingTestData.CreateAsync(fixture);
        Assert.Equal(94, CollectionMigrations.CurrentVersion);
        var input = await data.CreateBoundReceiptInputAsync("bound-runtime-storage");
        var preview = await data.Data.Service.PreviewAsync(data.Maker, input);
        var order = await data.Rows(PurchaseOrderTestData.Orders).Find(new BsonDocument("_id", input.OrderId)).SingleAsync();
        var proposalId = order["proposalId"].AsInt64;
        Assert.Equal("approve", (await data.Service.ReadAsync(data.Maker, proposalId))["inboundPools"]["state"].AsString);
        var pool = await SafetyPoolAsync(data);
        var expected = new StockSourceObligationExpectation(data.Data.PoolId, InventoryGraph.Location(pool["location"].AsBsonDocument),
            pool["revision"].AsInt64, pool["fence"].AsInt64);
        var reader = new ProcurementStockSourceObligationReader(data.Data.Database, data.Data.Unit, new(data.Data.Database), data.Data.Clock);
        var authentic = await data.Data.Unit.ExecuteAsync((tx, ct) => reader.ReadAndFenceAsync(tx, data.Data.SourceChecker, expected, ct));
        Assert.False(authentic.Complete); Assert.Equal(0L, authentic.OpenCount); Assert.Equal(1L, authentic.ProducerRevision);
        var before = await SafetyStateAsync(data);
        var counters = new BsonArray(await data.Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var definition = await (await data.Data.Database.ListCollectionsAsync(new ListCollectionsOptions {
            Filter = new BsonDocument("name", I.Decisions) })).SingleAsync();
        var indexes = new BsonArray(await (await data.Rows(I.Decisions).Indexes.ListAsync()).ToListAsync());
        Assert.Single(before[I.Decisions].AsBsonArray);
        var originalLevel = definition["options"].AsBsonDocument.GetValue("validationLevel", "strict");
        Assert.Equal("strict", originalLevel.AsString);
        const string key = "bound-runtime-storage-refused-receipt";
        // Only this own random fixture DB's schema setting is corrupted. Actual
        // rows, UUID, validators, indexes, receipts and source history stay intact.
        await data.Data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", I.Decisions }, { "validationLevel", "moderate" } });
        try {
            var damaged = await (await data.Data.Database.ListCollectionsAsync(new ListCollectionsOptions {
                Filter = new BsonDocument("name", I.Decisions) })).SingleAsync();
            Assert.Equal("moderate", damaged["options"]["validationLevel"].AsString);
            var poError = await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.ReadAsync(data.Maker, proposalId));
            Assert.Equal("inventory_storage_unavailable", poError.Code);
            Assert.Equal(before, await SafetyStateAsync(data));
            var sourceError = await Assert.ThrowsAsync<DomainValidationException>(() => data.Data.Unit.ExecuteAsync(
                (tx, ct) => reader.ReadAndFenceAsync(tx, data.Data.SourceChecker, expected, ct)));
            Assert.Equal("inventory_storage_unavailable", sourceError.Code);
            Assert.Equal(before, await SafetyStateAsync(data));
            var receiptError = await Assert.ThrowsAsync<DomainValidationException>(() => data.Data.Service.ReceiveAsync(data.Maker,
                input, preview["binding"].AsString, ProcurementStockReceiptTestData.Meta(key, input.Reason)));
            Assert.Equal("inventory_storage_unavailable", receiptError.Code);
            Assert.Equal(before, await SafetyStateAsync(data));
            Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument {
                { "actorId", data.Maker.UserId }, { "operation", ProcurementStockReceiptTestData.Operation }, { "key", key } }).AnyAsync());
            Assert.Equal(counters, new BsonArray(await data.Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            Assert.Equal(damaged, await (await data.Data.Database.ListCollectionsAsync(new ListCollectionsOptions {
                Filter = new BsonDocument("name", I.Decisions) })).SingleAsync());
            Assert.Equal(indexes, new BsonArray(await (await data.Rows(I.Decisions).Indexes.ListAsync()).ToListAsync()));
            Assert.False((await SafetyPoolAsync(data))["active"].AsBoolean);
        } finally {
            await data.Data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "collMod", I.Decisions }, { "validationLevel", originalLevel } });
        }
        Assert.Equal(definition, await (await data.Data.Database.ListCollectionsAsync(new ListCollectionsOptions {
            Filter = new BsonDocument("name", I.Decisions) })).SingleAsync());
        Assert.Equal(indexes, new BsonArray(await (await data.Rows(I.Decisions).Indexes.ListAsync()).ToListAsync()));
        Assert.Equal(before, await SafetyStateAsync(data));
        Assert.Equal(counters, new BsonArray(await data.Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Task<BsonDocument> SafetyPoolAsync(PurchaseOrderInboundBindingTestData data) =>
        data.Rows("inventoryPools").Find(new BsonDocument("_id", data.Data.PoolId)).SingleAsync();
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<BsonDocument> SafetyStateAsync(PurchaseOrderInboundBindingTestData data) {
        var state = new BsonDocument();
        foreach (var name in new[] { PurchaseOrderTestData.Proposals, PurchaseOrderTestData.Decisions, PurchaseOrderTestData.Orders,
            I.Selections, I.Decisions, "procurementRequestCommitments", "procurementRequestAllocations", "inventoryPools",
            "stockSourcePolicies", "stockSourcePolicyProposals", "stockSourcePolicyDecisions", "procurementStockReceipts", "lots", "stockBalances", "movements",
            "procurementQuarantineDispositionHeads", "procurementQuarantineDispositions", "procurementContainerHeads", "procurementContainerIntakes", "procurementArrivalNotices", "procurementInspectionHeads", "procurementInspectionDrafts", "procurementInspectionSubmissions",
            ProcurementStockSourceRecords.Heads, ProcurementStockSourceRecords.Transitions, "auditEvents", "idempotencyRecords", "outboxEvents",
            "journalEntries", "paymentAllocations", "invoices", "procurementDisbursements", "procurementSettlements", "refundPayouts" })
            state[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        // Allocators intentionally spend IDs before a failed transaction. They
        // are excluded, never reset or treated as evidence of business mutation.
        return state;
    }

}
