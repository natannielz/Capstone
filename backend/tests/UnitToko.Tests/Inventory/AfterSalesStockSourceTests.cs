using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Inventory;
/// <summary>UNEXECUTED draft. Requires real future registry M95 after M94 and additive callback
/// integration. Existing operational stock, invoice policy and physical-label fixtures are synthetic;
/// actual independent setup/dispatch/recipient/return/inspection writers produce every source head.</summary>
[Collection("Mongo")]
public sealed class AfterSalesStockSourceTests(MongoFixture fixture)
{
    [Fact(Skip = Pending.FixtureRewrite)]
    public async Task Genuine_independent_setup_yields_scoped_incomplete_empty_after_sales_without_pool_activation()
    {
        Assert.True(CollectionMigrations.CurrentVersion >= 95, "Schema 95 or later must be installed");
        await using var data = await StoreTestData.CreateAsync(fixture);
        var location = new StockLocation(new(100), new(101), new(102), new(103));
        var maker = await data.AddActorAsync(950100, location.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
        var checker = await data.AddActorAsync(950200, location.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, checker.UserId);
        var unit = new MongoUnitOfWork(fixture.Client); var service = new StockSourceSetupService(data.Database, unit, new(data.Database), data.Clock);
        var proposed = await service.ProposeAsync(maker, new(location, "m95-authentic-empty-source", "Synthetic reviewed AfterSales provenance source",
            StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60000, 0, null, null, "Synthetic discrepancy owner", "Synthetic handoff"),
            new("m95-source-proposal", "Synthetic local proof", "m95-after-sales-tests"));
        var approved = await service.DecideAsync(checker, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Independent synthetic source review", new("m95-source-approve", "Synthetic local proof", "m95-after-sales-tests"));
        var pool = await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument("_id", approved["poolId"])).SingleAsync();
        var expected = new StockSourceObligationExpectation(pool["_id"].AsInt64, location, pool["revision"].AsInt64, pool["fence"].AsInt64);
        var result = await unit.ExecuteAsync((tx, ct) => new AfterSalesStockSourceObligationReader(data.Database, unit, new(data.Database), data.Clock).ReadAndFenceAsync(tx, checker, expected, ct));
        Assert.False(result.Complete); Assert.Equal(0L, result.OpenCount); Assert.Equal(1L, result.ProducerRevision);
        Assert.Equal(pool, await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument("_id", approved["poolId"])).SingleAsync()); Assert.False(pool["active"].AsBoolean);
        foreach (var name in new[] { "stockBalances", "movements", "lots", "journalEntries", "afterSalesCases", "returnQuarantineReceipts", "returnedLotVerifications" })
            Assert.Equal(0L, await data.Database.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
    }

    [Fact(Skip = Pending.FixtureRewrite)]
    public async Task Actual_return_inspection_and_whole_variant_verification_remain_open_and_original_keys_never_release_stock_or_money()
    {
        Assert.True(CollectionMigrations.CurrentVersion >= 95, "Schema 95 or later must be installed");
        await using var data = await WholePackageDataAsync();
        var units = Assert.Single(data.Issued.Detail.Lines).BaseUnitsPerPackage; Assert.True(units > 1);
        var (service, caseId, line) = await ReturnQuarantineTests.Setup(data, units);
        var actor = data.Invoice.Finance; var catalog = data.Invoice.Order.Prices.Catalog;
        await catalog.Base.AddGrantAsync(actor, 99832, CatalogAuthoringTestData.Shop, [Permissions.StockCount, Permissions.StockSourceRead]);
        var before = await StockAndMoneyAsync(data);
        var incoming = await service.ReadReturnsAsync(actor, caseId, true);
        var input = new ReturnedStockInput("Kemasan utuh diterima untuk pemeriksaan", [new(line.ReceiptId.Value, line.SkuId.Value, units, "sealed")]);
        var receiveMeta = CatalogPriceTestData.Meta("m95-intake");
        var saved = await service.ReceiveReturnAsync(actor, caseId, incoming.Revision, incoming.Binding, input, receiveMeta); var returnId = saved["returnId"].AsInt64;
        var receipt = await data.Invoice.Rows(ReturnQuarantineLedger.Collection).Find(new BsonDocument("_id", returnId)).SingleAsync();
        var poolId = Assert.Single(AfterSalesStockSourceEvents.ProvenancePools(receipt));
        Assert.Equal(1L, (await ReadAsync(data, actor, poolId)).OpenCount);
        var inspectionView = await service.ReadInspectionAsync(actor, caseId, returnId, true);
        var inspectionInput = new ReturnedStockInspectionInput("Varian dan kemasan utuh dicocokkan", [new(line.ReceiptId.Value, line.SkuId.Value, units,
            "matched", "sealed", "Identitas varian sesuai pengiriman asli", "LABEL-LOT-SINTETIS")]);
        await service.InspectReturnAsync(actor, caseId, returnId, 0, inspectionView.Binding, inspectionInput, CatalogPriceTestData.Meta("m95-inspection"));
        var review = await service.ReadLotVerificationAsync(actor, caseId, returnId); var choice = Assert.Single(Assert.Single(review.Lines).Choices);
        var verifyInput = new ReturnedLotVerificationInput("VERIFIKASI-FISIK-SINTETIS", true, [new(line.ReceiptId.Value, line.SkuId.Value, choice.OriginalLotId.Value, units)]);
        var verifyMeta = CatalogPriceTestData.Meta("m95-lot-original-key");
        var verified = await service.VerifyReturnedLotAsync(actor, caseId, returnId, 0, review.Binding, verifyInput, verifyMeta);
        Assert.Equal("verified_original_lot_quarantined", verified["state"].AsString);
        var read = await ReadAsync(data, actor, poolId); Assert.False(read.Complete); Assert.Equal(1L, read.OpenCount);
        var sourceBefore = await SourceStateAsync(data);
        Assert.Equal(saved, await service.ReceiveReturnAsync(actor, caseId, incoming.Revision, incoming.Binding, input, receiveMeta));
        Assert.Equal(saved, await service.RecoverReturnAsync(actor, caseId, receiveMeta.IdempotencyKey));
        Assert.Equal(verified, await service.VerifyReturnedLotAsync(actor, caseId, returnId, 0, review.Binding, verifyInput, verifyMeta));
        Assert.Equal(verified, await service.RecoverLotVerificationAsync(actor, caseId, returnId, verifyMeta.IdempotencyKey));
        Assert.Equal(sourceBefore, await SourceStateAsync(data)); Assert.Equal(before, await StockAndMoneyAsync(data));
        var originalInspection = await data.Invoice.Rows(ReturnInspectionRecords.Collection).Find(new BsonDocument("_id", returnId)).SingleAsync();
        Assert.Equal("unverified", originalInspection["lotIdentity"].AsString); Assert.Equal("unverified", receipt["lotIdentity"].AsString);
        var resolution = await service.ReadResolutionAsync(actor, caseId, returnId, "replacement");
        await service.RequestResolutionAsync(actor, caseId, returnId, 0, resolution.Binding, new("replacement", "Ajukan penanganan barang"), CatalogPriceTestData.Meta("m95-resolution-proposal"));
        Assert.Equal(sourceBefore, await SourceStateAsync(data)); Assert.Equal(1L, (await ReadAsync(data, actor, poolId)).OpenCount);
        Assert.Equal(before, await StockAndMoneyAsync(data));
    }

    [Fact(Skip = Pending.FixtureRewrite)]
    public async Task Independent_parallel_verifications_have_one_immutable_result_and_one_genuine_source_append()
    {
        Assert.True(CollectionMigrations.CurrentVersion >= 95, "Schema 95 or later must be installed");
        await using var data = await WholePackageDataAsync();
        var (service, caseId, returnId, view, input) = await ReadyWholeReturnAsync(data);
        var actor = data.Invoice.Finance; var before = await StockAndMoneyAsync(data);
        var original = await SourceStateAsync(data);
        var keys = new[] { "m95-verify-race-a", "m95-verify-race-b" };
        var attempts = await Task.WhenAll(keys.Select(key => Record.ExceptionAsync(() => service.VerifyReturnedLotAsync(actor,
            caseId, returnId, 0, view.Binding, input, CatalogPriceTestData.Meta(key)))));
        Assert.Single(attempts, error => error is null); Assert.Single(attempts, error => error is not null);
        var saved = await data.Invoice.Rows(ReturnedLotVerificationRecords.Collection).Find(new BsonDocument()).SingleAsync();
        var winner = CatalogPriceTestData.Meta(saved["requestKey"].AsString);
        Assert.Equal(ReturnedLotVerificationRecords.Result(saved), await service.RecoverLotVerificationAsync(actor, caseId, returnId, winner.IdempotencyKey));
        Assert.Equal(ReturnedLotVerificationRecords.Result(saved), await service.VerifyReturnedLotAsync(actor, caseId, returnId, 0, view.Binding, input, winner));
        Assert.Equal(1L, await data.Invoice.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", ReturnedLotVerificationRecords.Operation)));
        Assert.Equal(1L, await data.Invoice.Rows(AfterSalesStockSourceRecords.Transitions).CountDocumentsAsync(new BsonDocument("sourceKind", "verifyLot")));
        var poolId = Assert.Single((await data.Invoice.Rows(ReturnQuarantineLedger.Collection).Find(new BsonDocument("_id", returnId)).SingleAsync())["source"]["stock"][0]["originalLots"].AsBsonArray)["poolId"].AsInt64;
        var result = await ReadAsync(data, actor, poolId); Assert.False(result.Complete); Assert.Equal(1L, result.OpenCount);
        Assert.Equal(before, await StockAndMoneyAsync(data)); Assert.NotEqual(original, await SourceStateAsync(data));
    }

    [Theory]
    [InlineData("count-grant")]
    [InlineData("person")]
    public async Task Original_verification_replay_and_recovery_require_current_stock_permission_and_independent_person(string fault)
    {
        Assert.True(CollectionMigrations.CurrentVersion >= 95, "Schema 95 or later must be installed");
        await using var data = await WholePackageDataAsync();
        var (service, caseId, returnId, view, input) = await ReadyWholeReturnAsync(data);
        var actor = data.Invoice.Finance; var metadata = CatalogPriceTestData.Meta("m95-current-verifier");
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadLotVerificationAsync(data.Invoice.Order.Buyer, caseId, returnId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.VerifyReturnedLotAsync(actor with {
            AuthenticatedAt = data.Invoice.Order.Prices.Catalog.Clock.UtcNow.AddMinutes(-6) }, caseId, returnId, 0, view.Binding, input, metadata));
        await service.VerifyReturnedLotAsync(actor, caseId, returnId, 0, view.Binding, input, metadata); var before = await SourceStateAsync(data);
        if (fault == "person") await data.Invoice.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        else Assert.Equal(1L, (await data.Invoice.Rows("authorizationGrants").DeleteOneAsync(new BsonDocument("_id", 99832L))).DeletedCount);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverLotVerificationAsync(actor, caseId, returnId, metadata.IdempotencyKey));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.VerifyReturnedLotAsync(actor, caseId, returnId, 0, view.Binding, input, metadata));
        Assert.Equal(before, await SourceStateAsync(data)); Assert.Equal(1L, await data.Invoice.Rows(ReturnedLotVerificationRecords.Collection).CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Selected_lot_must_be_in_actual_original_dispatch_and_verified_quantity_must_fit_received_whole_packages()
    {
        Assert.True(CollectionMigrations.CurrentVersion >= 95, "Schema 95 or later must be installed");
        await using var data = await WholePackageDataAsync();
        var (service, caseId, returnId, view, input) = await ReadyWholeReturnAsync(data);
        var before = await SourceStateAsync(data); var stock = await StockAndMoneyAsync(data); var line = Assert.Single(input.Lines);
        var invalid = new[] {
            input with { ConfirmPhysicalOriginalLotMatch = false },
            input with { Lines = [line with { OriginalLotId = long.MaxValue - 2 }] },
            input with { Lines = [line with { VerifiedBaseUnits = line.VerifiedBaseUnits + 1 }] },
            input with { Lines = [line with { VerifiedBaseUnits = 1 }] }
        };
        for (var i = 0; i < invalid.Length; i++)
            Assert.Equal("invalid_return_lot_verification", (await Assert.ThrowsAsync<DomainValidationException>(() => service.VerifyReturnedLotAsync(data.Invoice.Finance,
                caseId, returnId, 0, view.Binding, invalid[i], CatalogPriceTestData.Meta("m95-invalid-association-" + i)))).Code);
        Assert.Equal(before, await SourceStateAsync(data)); Assert.Equal(stock, await StockAndMoneyAsync(data));
        Assert.False(await data.Invoice.Rows(ReturnedLotVerificationRecords.Collection).Find(new BsonDocument()).AnyAsync());
    }

    [Fact]
    public async Task Whole_sealed_package_with_a_different_variant_stays_manual_even_when_an_original_lot_exists()
    {
        Assert.True(CollectionMigrations.CurrentVersion >= 95, "Schema 95 or later must be installed");
        await using var data = await WholePackageDataAsync();
        var (service, caseId, returnId, view, input) = await ReadyWholeReturnAsync(data, "different");
        var line = Assert.Single(view.Lines); Assert.Equal("penanganan_manual", line.State); Assert.Empty(line.Choices);
        Assert.Equal(Assert.Single(data.Issued.Detail.Lines).BaseUnitsPerPackage, line.ManualBaseUnits);
        var before = await SourceStateAsync(data);
        Assert.Equal("invalid_return_lot_verification", (await Assert.ThrowsAsync<DomainValidationException>(() => service.VerifyReturnedLotAsync(data.Invoice.Finance,
            caseId, returnId, 0, view.Binding, input, CatalogPriceTestData.Meta("m95-wrong-variant")))).Code);
        Assert.Equal(before, await SourceStateAsync(data)); Assert.False(await data.Invoice.Rows(ReturnedLotVerificationRecords.Collection).Find(new BsonDocument()).AnyAsync());
    }

    [Fact]
    public async Task MFA_expiring_after_actual_verification_source_head_CAS_rolls_back_finding_and_history_together()
    {
        Assert.True(CollectionMigrations.CurrentVersion >= 95, "Schema 95 or later must be installed");
        await using var data = await WholePackageDataAsync();
        var (_, caseId, returnId, view, input) = await ReadyWholeReturnAsync(data); var actor = data.Invoice.Finance;
        var before = await SourceStateAsync(data); var business = await StockAndMoneyAsync(data); var clock = data.Invoice.Order.Prices.Catalog.Clock;
        var request = 0; var observed = 0; var settings = fixture.Client.Settings.Clone();
        settings.ApplicationName = "M95FinalReturnedLotMfa-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (request == 0 && command.DatabaseNamespace.DatabaseName == data.Invoice.Order.Database.DatabaseNamespace.DatabaseName &&
                    command.CommandName == "update" && command.Command.GetValue("update", BsonNull.Value) == AfterSalesStockSourceRecords.Heads &&
                    command.Command["updates"].AsBsonArray.Any(update => update["u"].AsBsonDocument.GetValue("$inc", new BsonDocument())
                        .AsBsonDocument.GetValue("revision", 0).ToInt64() == 1)) request = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (request != 0 && command.RequestId == request && command.Reply.GetValue("nModified", 0).ToInt64() == 1 &&
                    Interlocked.CompareExchange(ref observed, 1, 0) == 0) clock.UtcNow = clock.UtcNow.AddMinutes(6);
            });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Invoice.Order.Database.DatabaseNamespace.DatabaseName);
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database);
        var orders = new OrderSubmissionService(database, unit, ids, clock);
        var preparations = new ShipmentPreparationService(database, unit, ids, clock, orders);
        var shipments = new ShipmentQueryService(database, unit, clock, orders, preparations);
        var documents = new ShipmentDocumentService(database, unit, ids, clock, shipments);
        var recipients = new ShipmentReceiptService(database, unit, ids, clock, shipments, documents);
        var service = new AfterSalesCaseService(database, unit, ids, clock, new InvoiceService(database, unit, ids, clock, recipients));
        var metadata = CatalogPriceTestData.Meta("m95-final-verification-mfa");
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.VerifyReturnedLotAsync(actor,
            caseId, returnId, 0, view.Binding, input, metadata))).Code); Assert.Equal(1, observed);
        Assert.Equal(before, await SourceStateAsync(data)); Assert.Equal(business, await StockAndMoneyAsync(data));
        Assert.False(await data.Invoice.Rows(ReturnedLotVerificationRecords.Collection).Find(new BsonDocument("_id", returnId)).AnyAsync());
        Assert.False(await data.Invoice.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", ReturnedLotVerificationRecords.Operation }, { "key", metadata.IdempotencyKey } }).AnyAsync());
        Assert.False(await data.Invoice.Rows("outboxEvents").Find(new BsonDocument { { "kind", ReturnedLotVerificationRecords.Operation }, { "requestKey", metadata.IdempotencyKey } }).AnyAsync());
    }

    [Fact]
    public async Task Loose_opened_units_require_manual_handling_and_cannot_be_silently_converted_into_whole_packages()
    {
        Assert.True(CollectionMigrations.CurrentVersion >= 95, "Schema 95 or later must be installed");
        await using var data = await PaymentDraftData.CreateAsync(fixture);
        var (service, caseId, returnId, inspected) = await ReturnInspectionTests.Setup(data);
        var actor = data.Invoice.Finance; var preview = await service.ReadInspectionAsync(actor, caseId, returnId, true);
        await service.InspectReturnAsync(actor, caseId, returnId, 0, preview.Binding, inspected, CatalogPriceTestData.Meta("m95-opened-inspection"));
        var view = await service.ReadLotVerificationAsync(actor, caseId, returnId); var line = Assert.Single(view.Lines);
        Assert.Equal("penanganan_manual", line.State); Assert.Empty(line.Choices); Assert.Equal(1L, line.ManualBaseUnits);
        var receipt = await data.Invoice.Rows(ReturnQuarantineLedger.Collection).Find(new BsonDocument("_id", returnId)).SingleAsync();
        var original = receipt["source"]["stock"][0]["originalLots"][0]["lotId"].AsInt64;
        var bad = new ReturnedLotVerificationInput("LOOSE-UNITS-CANNOT-INFER-PACKAGE", true, [new(line.ReceiptId.Value, line.SkuId.Value, original, 1)]);
        var before = await SourceStateAsync(data);
        Assert.Equal("invalid_return_lot_verification", (await Assert.ThrowsAsync<DomainValidationException>(() => service.VerifyReturnedLotAsync(actor, caseId,
            returnId, 0, view.Binding, bad, CatalogPriceTestData.Meta("m95-refuse-loose")))).Code);
        Assert.Equal(before, await SourceStateAsync(data)); Assert.Equal(0L, await data.Invoice.Rows(ReturnedLotVerificationRecords.Collection).CountDocumentsAsync(new BsonDocument()));
    }

    [Theory]
    [InlineData("missing-head")]
    [InlineData("missing-command")]
    [InlineData("missing-outbox")]
    public async Task Authentic_return_membership_corruption_refuses_scoped_reader_without_repair(string fault)
    {
        Assert.True(CollectionMigrations.CurrentVersion >= 95, "Schema 95 or later must be installed");
        await using var data = await PaymentDraftData.CreateAsync(fixture); var (service, caseId, returnId, _) = await ReturnInspectionTests.Setup(data);
        var receipt = await data.Invoice.Rows(ReturnQuarantineLedger.Collection).Find(new BsonDocument("_id", returnId)).SingleAsync();
        var poolId = Assert.Single(AfterSalesStockSourceEvents.ProvenancePools(receipt)); var actor = data.Invoice.Finance;
        await data.Invoice.Order.Prices.Catalog.Base.AddGrantAsync(actor, 99839, CatalogAuthoringTestData.Shop, [Permissions.StockSourceRead]);
        if (fault == "missing-head") Assert.Equal(1L, (await data.Invoice.Rows(AfterSalesStockSourceRecords.Heads).DeleteOneAsync(new BsonDocument("_id", poolId))).DeletedCount);
        if (fault == "missing-command") Assert.Equal(1L, (await data.Invoice.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation", ReturnQuarantineLedger.Operation }, { "key", receipt["requestKey"] } })).DeletedCount);
        if (fault == "missing-outbox") Assert.Equal(1L, (await data.Invoice.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("sourceKey", ReturnQuarantineLedger.Operation + ":" + returnId))).DeletedCount);
        var before = await SourceStateAsync(data); var business = await StockAndMoneyAsync(data);
        Assert.NotNull(await Record.ExceptionAsync(() => ReadAsync(data, actor, poolId)));
        Assert.Equal(before, await SourceStateAsync(data)); Assert.Equal(business, await StockAndMoneyAsync(data));
        if (fault == "missing-head") Assert.Equal(returnId, (await service.RecoverReturnAsync(actor, caseId, receipt["requestKey"].AsString))!["returnId"].AsInt64);
    }

    private static async Task<StockSourceObligationResult> ReadAsync(PaymentDraftData data, ActorContext actor, long poolId)
    {
        var database = data.Invoice.Order.Database; var clock = data.Invoice.Order.Prices.Catalog.Clock; var unit = new MongoUnitOfWork(data.Invoice.Order.Prices.Catalog.Client);
        var pool = await data.Invoice.Rows("inventoryPools").Find(new BsonDocument("_id", poolId)).SingleAsync();
        var expected = new StockSourceObligationExpectation(poolId, InventoryGraph.Location(pool["location"].AsBsonDocument), pool["revision"].AsInt64, pool["fence"].AsInt64);
        return await unit.ExecuteAsync((tx, ct) => new AfterSalesStockSourceObligationReader(database, unit, new(database), clock).ReadAndFenceAsync(tx, actor, expected, ct));
    }
    private async Task<PaymentDraftData> WholePackageDataAsync()
    {
        // The retained payment fixture honestly accepts six loose units of a twelve-unit package.
        // Produce a separate real full-package dispatch/recipient declaration/invoice; never change
        // that first signed receipt, packaging definition, immutable invoice or any producer seed.
        var data = await PaymentDraftData.CreateAsync(fixture);
        try {
            var dispatch = data.Invoice.Dispatch;
            await dispatch.PrepareAsync(1, dispatch.Prepared.PreparationRevision, "m95-whole-prepare");
            var shipment = (await dispatch.DispatchAsync(await dispatch.PreviewAsync(), "m95-whole-dispatch"))["shipmentId"].AsInt64;
            var document = await ShipmentDocumentTests.Service(dispatch).IssueAsync(dispatch.Picker, shipment, 1, CatalogPriceTestData.Meta("m95-whole-document"));
            var recipients = ShipmentReceiptTests.Service(dispatch);
            var challenge = await recipients.IssueChallengeAsync(dispatch.Courier, shipment, document.Id.Value, dispatch.Order.Buyer.UserId, CatalogPriceTestData.Meta("m95-whole-challenge"));
            var review = await recipients.ReviewAsync(dispatch.Order.Buyer, challenge.Id.Value, challenge.Token!);
            var receipt = await recipients.ConfirmAsync(dispatch.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true,
                [new(review.Lines[0].SkuId, new(1, 0), new(0, 0), new(0, 0), null)], CatalogPriceTestData.Meta("m95-whole-consent"));
            var selected = System.Collections.Immutable.ImmutableArray.Create(new InvoiceReceiptSelection(receipt.ShipmentId, receipt.Id));
            var approved = await data.Invoice.Service.PreviewAsync(data.Invoice.Finance, selected);
            var issued = await data.Invoice.Service.IssueAsync(data.Invoice.Finance, selected, approved.Review, CatalogPriceTestData.Meta("m95-whole-invoice"));
            Assert.Equal(Assert.Single(issued.Detail.Lines).BaseUnitsPerPackage, Assert.Single(issued.Detail.Lines).AcceptedBaseUnits);
            return new(data.Invoice, issued);
        } catch { await data.DisposeAsync(); throw; }
    }
    private static async Task<(AfterSalesCaseService Service, long CaseId, long ReturnId, ReturnedLotVerificationView View, ReturnedLotVerificationInput Input)> ReadyWholeReturnAsync(PaymentDraftData data, string identity = "matched")
    {
        var units = Assert.Single(data.Issued.Detail.Lines).BaseUnitsPerPackage; var (service, caseId, line) = await ReturnQuarantineTests.Setup(data, units);
        var actor = data.Invoice.Finance;
        await data.Invoice.Order.Prices.Catalog.Base.AddGrantAsync(actor, 99832, CatalogAuthoringTestData.Shop, [Permissions.StockCount, Permissions.StockSourceRead]);
        var source = await service.ReadReturnsAsync(actor, caseId, true);
        var returnId = (await service.ReceiveReturnAsync(actor, caseId, source.Revision, source.Binding,
            new("Kemasan utuh diterima", [new(line.ReceiptId.Value, line.SkuId.Value, units, "sealed")]), CatalogPriceTestData.Meta("m95-whole-intake")))["returnId"].AsInt64;
        var inspected = await service.ReadInspectionAsync(actor, caseId, returnId, true);
        await service.InspectReturnAsync(actor, caseId, returnId, 0, inspected.Binding, new("Pemeriksaan kemasan utuh",
            [new(line.ReceiptId.Value, line.SkuId.Value, units, identity, "sealed", "Temuan varian fisik sintetis", "LABEL-FISIK-SINTETIS")]), CatalogPriceTestData.Meta("m95-whole-inspect"));
        var view = await service.ReadLotVerificationAsync(actor, caseId, returnId);
        var receipt = await data.Invoice.Rows(ReturnQuarantineLedger.Collection).Find(new BsonDocument("_id", returnId)).SingleAsync();
        var original = Assert.Single(receipt["source"]["stock"][0]["originalLots"].AsBsonArray)["lotId"].AsInt64;
        if (identity == "matched") Assert.Equal(original, Assert.Single(Assert.Single(view.Lines).Choices).OriginalLotId.Value);
        return (service, caseId, returnId, view, new("MANUAL-INDEPENDENT-PHYSICAL-REVIEW", true, [new(line.ReceiptId.Value, line.SkuId.Value, original, units)]));
    }
    private static async Task<BsonDocument> SourceStateAsync(PaymentDraftData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "inventoryPools", AfterSalesStockSourceRecords.Heads, AfterSalesStockSourceRecords.Transitions })
            result.Add(name, new BsonArray(await data.Invoice.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return result;
    }
    private static async Task<BsonDocument> StockAndMoneyAsync(PaymentDraftData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "stockBalances", "lots", "movements", "reservations", "journalEntries", "paymentSubmissions", "paymentAllocations", "cashMovements" })
        {
            var rows = await data.Invoice.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            // Existing return authorization serializes invoice/stock ownership by fencing it.
            // Compare every business field, while producer/canonical fences are checked separately.
            foreach (var row in rows) { row.Remove("fence"); row.Remove("postingFence"); }
            result.Add(name, new BsonArray(rows));
        }
        return result;
    }
}
