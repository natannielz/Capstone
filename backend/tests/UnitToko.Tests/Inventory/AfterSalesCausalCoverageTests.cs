using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Inventory;
[Collection("Mongo")]
public sealed class AfterSalesCausalCoverageTests(MongoFixture fixture)
{
    [Fact]
    public async Task Genuine_empty_configured_source_has_complete_zero_coverage_without_activation_or_business_effects()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var location = new StockLocation(new(100), new(101), new(102), new(103));
        var maker = await data.AddActorAsync(990100, location.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
        var checker = await data.AddActorAsync(990200, location.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, checker.UserId);
        var unit = new MongoUnitOfWork(fixture.Client); var service = new StockSourceSetupService(data.Database, unit, new(data.Database), data.Clock);
        var proposal = await service.ProposeAsync(maker, new(location, "m99-empty-authentic-source", "Synthetic AfterSales coverage source",
            StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60000, 0, null, null, "Synthetic owner", "Synthetic reviewed handoff"), CatalogPriceTestData.Meta("m99-empty-propose"));
        var approval = await service.DecideAsync(checker, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Independent synthetic review", CatalogPriceTestData.Meta("m99-empty-approve"));
        var pool = await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument("_id", approval["poolId"])).SingleAsync();
        var expected = new StockSourceObligationExpectation(pool["_id"].AsInt64, location, pool["revision"].AsInt64, pool["fence"].AsInt64);
        var result = await unit.ExecuteAsync((tx, ct) => new AfterSalesStockSourceObligationReader(data.Database, unit, new(data.Database), data.Clock).ReadAndFenceAsync(tx, checker, expected, ct));
        Assert.True(result.Complete); Assert.Equal(0L, result.OpenCount); Assert.Equal(1L, result.ProducerRevision);
        Assert.Equal(pool, await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument("_id", approval["poolId"])).SingleAsync());
        Assert.False(pool["active"].AsBoolean);
        foreach (var name in new[] { "stockBalances", "movements", "lots", "journalEntries", "afterSalesCases", "returnQuarantineReceipts" })
            Assert.Equal(0L, await data.Database.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Real_branch_request_and_independent_rejection_cover_provenance_before_receiving_with_exact_original_key_replay()
    {
        await using var data = await PaymentDraftData.CreateAsync(fixture);
        var service = await ServiceAsync(data); var buyer = data.Invoice.Order.Buyer; var finance = data.Invoice.Finance;
        var sale = await service.SourceAsync(buyer, data.Issued.Id.Value); var line = sale.Lines[0];
        var input = new AfterSalesCaseInput(sale.InvoiceId.Value, sale.Binding, "damaged", [new(line.ReceiptId.Value, line.SkuId.Value, 1)]);
        var metadata = CatalogPriceTestData.Meta("m99-branch-cause"); var before = await BusinessAsync(data);
        var attempts = await Task.WhenAll(Record.ExceptionAsync(() => service.RequestAsync(buyer, input, metadata)), Record.ExceptionAsync(() => service.RequestAsync(buyer, input, metadata)));
        Assert.Contains(attempts, error => error is null);
        foreach (var error in attempts.Where(error => error is not null)) Assert.Equal("version_conflict", Assert.IsType<PersistenceConflictException>(error).Code);
        var result = await service.RequestAsync(buyer, input, metadata); var caseId = result["caseId"].AsInt64; var poolId = await PoolAsync(data, caseId);
        Assert.Equal(result, await service.RecoverAsync(buyer, metadata.IdempotencyKey));
        Assert.Equal(1L, await data.Invoice.Rows(AfterSalesStockSourceRecords.CausalTransitions).CountDocumentsAsync(new BsonDocument("sourceKind", "request")));
        Assert.False(await data.Invoice.Rows(ReturnQuarantineLedger.Collection).Find(new BsonDocument()).AnyAsync());
        var pending = await ReadAsync(data, finance, poolId); Assert.True(pending.Complete); Assert.Equal(1L, pending.OpenCount);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadDecisionAsync(buyer, caseId));
        var decision = await service.ReadDecisionAsync(finance, caseId); var rejected = new AfterSalesDecisionInput("reject", "Permintaan tidak memenuhi syarat", []);
        var rejectMetadata = CatalogPriceTestData.Meta("m99-reject-cause");
        var saved = await service.DecideAsync(finance, caseId, 1, decision.Binding, rejected, rejectMetadata);
        Assert.Equal(saved, await service.DecideAsync(finance, caseId, 1, decision.Binding, rejected, rejectMetadata));
        Assert.Equal(saved, await service.RecoverDecisionAsync(finance, caseId, rejectMetadata.IdempotencyKey));
        var closed = await ReadAsync(data, finance, poolId); Assert.True(closed.Complete); Assert.Equal(0L, closed.OpenCount); Assert.Equal(pending.ProducerRevision + 1, closed.ProducerRevision);
        Assert.Equal(1L, await data.Invoice.Rows(AfterSalesStockSourceRecords.CausalTransitions).CountDocumentsAsync(new BsonDocument("sourceKind", "decision")));
        Assert.Equal(before, await BusinessAsync(data));
    }

    [Fact]
    public async Task Authorized_unreceived_return_and_received_inspected_resolution_request_remain_open_with_no_stock_or_financial_shortcut()
    {
        await using var data = await PaymentDraftData.CreateAsync(fixture);
        var (service, caseId, line) = await ReturnQuarantineTests.Setup(data); var actor = data.Invoice.Finance;
        await data.Invoice.Order.Prices.Catalog.Base.AddGrantAsync(actor, 99930, CatalogAuthoringTestData.Shop, [Permissions.StockCount, Permissions.StockSourceRead]);
        var poolId = await PoolAsync(data, caseId); var before = await BusinessAsync(data);
        var authorized = await ReadAsync(data, actor, poolId); Assert.True(authorized.Complete); Assert.Equal(1L, authorized.OpenCount);
        var receive = await service.ReadReturnsAsync(actor, caseId, true);
        var returned = await service.ReceiveReturnAsync(actor, caseId, receive.Revision, receive.Binding,
            new("Satu barang dalam karantina", [new(line.ReceiptId.Value, line.SkuId.Value, 1, "opened")]), CatalogPriceTestData.Meta("m99-return-intake"));
        var returnId = returned["returnId"].AsInt64;
        var inspection = await service.ReadInspectionAsync(actor, caseId, returnId, true);
        await service.InspectReturnAsync(actor, caseId, returnId, 0, inspection.Binding,
            new("Pemeriksaan fisik sintetis", [new(line.ReceiptId.Value, line.SkuId.Value, 1, "matched", "opened", "Varian yang sama", "LABEL-SINTETIS")]), CatalogPriceTestData.Meta("m99-return-inspect"));
        var received = await ReadAsync(data, actor, poolId); Assert.True(received.Complete); Assert.Equal(2L, received.OpenCount);
        var resolution = await service.ReadResolutionAsync(actor, caseId, returnId, "replacement");
        await service.RequestResolutionAsync(actor, caseId, returnId, 0, resolution.Binding, new("replacement", "Ajukan penanganan lanjutan"), CatalogPriceTestData.Meta("m99-resolution-request"));
        var requested = await ReadAsync(data, actor, poolId); Assert.True(requested.Complete); Assert.Equal(received.OpenCount, requested.OpenCount);
        Assert.Equal(received.ProducerRevision, requested.ProducerRevision); Assert.Equal(before, await BusinessAsync(data));
    }

    [Theory]
    [InlineData("causal-head")]
    [InlineData("request-membership")]
    [InlineData("original-command")]
    public async Task Missing_early_cause_never_becomes_zero_and_reader_never_repairs_history(string fault)
    {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var service = await ServiceAsync(data); var actor = data.Invoice.Order.Buyer;
        var sale = await service.SourceAsync(actor, data.Issued.Id.Value); var line = sale.Lines[0]; var metadata = CatalogPriceTestData.Meta("m99-missing-cause");
        var saved = await service.RequestAsync(actor, new(sale.InvoiceId.Value, sale.Binding, "damaged", [new(line.ReceiptId.Value, line.SkuId.Value, 1)]), metadata);
        var poolId = await PoolAsync(data, saved["caseId"].AsInt64);
        var removed = fault switch {
            "causal-head" => await data.Invoice.Rows(AfterSalesStockSourceRecords.CausalHeads).DeleteOneAsync(new BsonDocument("_id", poolId)),
            "request-membership" => await data.Invoice.Rows(AfterSalesStockSourceRecords.CausalTransitions).DeleteOneAsync(new BsonDocument { { "poolId", poolId }, { "sourceKind", "request" } }),
            _ => await data.Invoice.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation", AfterSalesCaseRecords.Operation }, { "key", metadata.IdempotencyKey } }) };
        Assert.Equal(1L, removed.DeletedCount);
        var before = await CoverageAsync(data); var business = await BusinessAsync(data);
        Assert.NotNull(await Record.ExceptionAsync(() => ReadAsync(data, data.Invoice.Finance, poolId)));
        Assert.Equal(before, await CoverageAsync(data)); Assert.Equal(business, await BusinessAsync(data));
        if (fault != "original-command") Assert.Equal(saved, await service.RecoverAsync(actor, metadata.IdempotencyKey));
    }

    [Fact]
    public async Task MFA_expiry_after_real_causal_head_CAS_rolls_back_decision_membership_pool_and_original_protocols()
    {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var service = await ServiceAsync(data); var buyer = data.Invoice.Order.Buyer;
        var sale = await service.SourceAsync(buyer, data.Issued.Id.Value); var line = sale.Lines[0];
        var caseId = (await service.RequestAsync(buyer, new(sale.InvoiceId.Value, sale.Binding, "damaged", [new(line.ReceiptId.Value, line.SkuId.Value, 1)]), CatalogPriceTestData.Meta("m99-expiry-case")))["caseId"].AsInt64;
        var actor = data.Invoice.Finance; var review = await service.ReadDecisionAsync(actor, caseId); var clock = data.Invoice.Order.Prices.Catalog.Clock;
        var before = await CoverageAsync(data); var business = await BusinessAsync(data); var request = 0; var observed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "M99AfterSalesFinalMfa-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (request == 0 && command.DatabaseNamespace.DatabaseName == data.Invoice.Order.Database.DatabaseNamespace.DatabaseName && command.CommandName == "update" &&
                    command.Command.GetValue("update", BsonNull.Value) == AfterSalesStockSourceRecords.CausalHeads && command.Command["updates"].AsBsonArray.Any(update =>
                        update["u"].AsBsonDocument.GetValue("$inc", new BsonDocument()).AsBsonDocument.GetValue("revision", 0).ToInt64() == 1)) request = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (request != 0 && command.RequestId == request && command.Reply.GetValue("nModified", 0).ToInt64() == 1 &&
                    Interlocked.CompareExchange(ref observed, 1, 0) == 0) clock.UtcNow = clock.UtcNow.AddMinutes(6);
            });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Invoice.Order.Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database);
        var orders = new OrderSubmissionService(database, unit, ids, clock); var preparations = new ShipmentPreparationService(database, unit, ids, clock, orders);
        var shipments = new ShipmentQueryService(database, unit, clock, orders, preparations); var documents = new ShipmentDocumentService(database, unit, ids, clock, shipments);
        var receipts = new ShipmentReceiptService(database, unit, ids, clock, shipments, documents);
        var isolated = new AfterSalesCaseService(database, unit, ids, clock, new InvoiceService(database, unit, ids, clock, receipts));
        var metadata = CatalogPriceTestData.Meta("m99-expiry-decision");
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => isolated.DecideAsync(actor, caseId, 1, review.Binding,
            new("reject", "Permintaan ditolak", []), metadata))).Code); Assert.Equal(1, observed);
        Assert.Equal(before, await CoverageAsync(data)); Assert.Equal(business, await BusinessAsync(data));
        Assert.False(await data.Invoice.Rows(AfterSalesDecisionRecords.Collection).Find(new BsonDocument("_id", caseId)).AnyAsync());
        foreach (var (table, field) in new[] { ("idempotencyRecords", "key"), ("auditEvents", "requestKey"), ("outboxEvents", "requestKey") })
            Assert.False(await data.Invoice.Rows(table).Find(new BsonDocument { { table == "idempotencyRecords" ? "operation" : "kind", AfterSalesDecisionRecords.Operation }, { field, metadata.IdempotencyKey } }).AnyAsync());
    }

    private static async Task<AfterSalesCaseService> ServiceAsync(PaymentDraftData data)
    {
        var catalog = data.Invoice.Order.Prices.Catalog; var buyer = data.Invoice.Order.Buyer; var reviewer = data.Invoice.Finance;
        await catalog.Base.AddGrantAsync(buyer, 99810, CatalogAuthoringTestData.Audience, [Permissions.CaseRead, Permissions.CaseRequest]);
        await catalog.Base.AddGrantAsync(reviewer, 99830, CatalogAuthoringTestData.Shop, [Permissions.CaseRead, Permissions.CaseManage, Permissions.StockSourceRead]);
        foreach (var user in new[] { buyer.UserId, reviewer.UserId }) if (!await data.Invoice.Rows("personRecords").Find(new BsonDocument("_id", PersonTestData.PersonId(user))).AnyAsync())
            await PersonTestData.SeedDistinctPeopleAsync(data.Invoice.Order.Database, catalog.Clock, user);
        return new(data.Invoice.Order.Database, new(catalog.Client), new(data.Invoice.Order.Database), catalog.Clock, data.Invoice.Service);
    }
    private static async Task<long> PoolAsync(PaymentDraftData data, long caseId)
    {
        var unit = new MongoUnitOfWork(data.Invoice.Order.Prices.Catalog.Client);
        return await unit.ExecuteAsync(async (tx, ct) => Assert.Single((await new AfterSalesStockSourceEvents(data.Invoice.Order.Database,
            new(data.Invoice.Order.Database), data.Invoice.Order.Prices.Catalog.Clock).ReadCausalAsync(tx, "request", caseId, ct)).PoolIds));
    }
    private static async Task<StockSourceObligationResult> ReadAsync(PaymentDraftData data, ActorContext actor, long poolId)
    {
        var database = data.Invoice.Order.Database; var clock = data.Invoice.Order.Prices.Catalog.Clock; var unit = new MongoUnitOfWork(data.Invoice.Order.Prices.Catalog.Client);
        var pool = await data.Invoice.Rows("inventoryPools").Find(new BsonDocument("_id", poolId)).SingleAsync();
        var expected = new StockSourceObligationExpectation(poolId, InventoryGraph.Location(pool["location"].AsBsonDocument), pool["revision"].AsInt64, pool["fence"].AsInt64);
        return await unit.ExecuteAsync((tx, ct) => new AfterSalesStockSourceObligationReader(database, unit, new(database), clock).ReadAndFenceAsync(tx, actor, expected, ct));
    }
    private static async Task<BsonDocument> CoverageAsync(PaymentDraftData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "inventoryPools", AfterSalesStockSourceRecords.Heads, AfterSalesStockSourceRecords.Transitions, AfterSalesStockSourceRecords.CausalHeads, AfterSalesStockSourceRecords.CausalTransitions })
            result.Add(name, new BsonArray(await data.Invoice.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return result;
    }
    private static async Task<BsonDocument> BusinessAsync(PaymentDraftData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "stockBalances", "lots", "movements", "reservations", "journalEntries", "paymentSubmissions", "paymentAllocations", "cashMovements" }) {
            var rows = await data.Invoice.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) { row.Remove("fence"); row.Remove("postingFence"); } result.Add(name, new BsonArray(rows));
        }
        return result;
    }
}
