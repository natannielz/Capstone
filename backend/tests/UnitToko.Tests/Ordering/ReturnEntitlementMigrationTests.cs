using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Billing.ReturnEntitlementBson;
using A = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ReturnEntitlementMigrationTests(MongoFixture fixture)
{
    private static async Task<List<BsonDocument>> Definitions(IMongoDatabase database) =>
        (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task ProposeAsync(PaymentDraftData data) {
        var (service, caseId, returnId) = await ReturnResolutionTests.Setup(data); var actor = data.Invoice.Finance;
        await data.Invoice.Order.Prices.Catalog.Base.AddGrantAsync(actor, 99833, CatalogAuthoringTestData.Shop, [Permissions.CreditRequest]);
        var shown = await service.ReadResolutionAsync(actor, caseId, returnId, "credit");
        await service.RequestResolutionAsync(actor, caseId, returnId, 0, shown.Binding, new ReturnResolutionInput("credit", "Ajukan peninjauan kredit"),
            CatalogPriceTestData.Meta("entitlement-upgrade-proposal"));
    }
    private static async Task PredecessorAsync(PaymentDraftData data) {
        var database = data.Invoice.Order.Database;
        Assert.True(await data.Invoice.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 52 }, { "state", "applied" } }).AnyAsync());
        foreach (var name in ReturnEntitlementSchemas.Collections) {
            Assert.Equal(0, await data.Invoice.Count(name));
            await database.DropCollectionAsync(name);
        }
        // Only this isolated fixture is put back into its actual empty-target predecessor.
        Assert.Equal(1, (await data.Invoice.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 53))).DeletedCount);
    }
    private static Task UpgradeAsync(PaymentDraftData data) => new CollectionMigrations(data.Invoice.Order.Database, data.Invoice.Order.Prices.Catalog.Clock).ApplyAsync();
    private static async Task RefusedBeforeDdlAsync(PaymentDraftData data) {
        var before = await Definitions(data.Invoice.Order.Database);
        Assert.NotNull(await Record.ExceptionAsync(() => UpgradeAsync(data)));
        Assert.Equal(before, await Definitions(data.Invoice.Order.Database));
        Assert.False(await data.Invoice.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 53 }, { "state", "applied" } }).AnyAsync());
    }
    [WindowsStorageFact]
    public async Task Actual_inspected_credit_proposal_and_linked_payment_correction_upgrade_without_changing_original_money_or_sources() {
        await using var allocation = await PaymentAllocationTests.Data.CreateAsync(fixture); var data = allocation.Source.Read.Payment;
        var input = allocation.Input(0, 1000m); var shown = await allocation.Service.PreviewAsync(allocation.Actor, allocation.VerificationId, input);
        var original = await allocation.Service.AllocateAsync(allocation.Actor, allocation.VerificationId, shown.Binding, input, PaymentAllocationTests.Data.Meta("entitlement-upgrade-original-allocation"));
        var checker = await PaymentAllocationCorrectionTests.Checker(allocation); var corrected = allocation.Input(1, 400m);
        var correction = await allocation.Service.PreviewCorrectionAsync(checker, original["allocationId"].AsInt64, corrected);
        await allocation.Service.CorrectAsync(checker, original["allocationId"].AsInt64, correction.Binding, corrected, PaymentAllocationTests.Data.Meta("entitlement-upgrade-linked-correction"));
        await ProposeAsync(data); await PredecessorAsync(data);
        var tables = new[] { InvoiceBson.Invoices, InvoiceBson.Allocations, ShipmentReceiptRecords.Receipts, AfterSalesCaseRecords.Collection, AfterSalesDecisionRecords.Collection,
            ReturnQuarantineLedger.Collection, ReturnInspectionRecords.Collection, ReturnResolutionRecords.Collection, A.Events, C.Events, A.Receipts, A.Invoices, "journalEntries", BankReceiptBson.Receipts };
        var snapshots = new Dictionary<string, List<BsonDocument>>();
        foreach (var name in tables) { snapshots[name] = await data.Invoice.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); Assert.NotEmpty(snapshots[name]); }
        await UpgradeAsync(data);
        foreach (var name in tables) Assert.Equal(snapshots[name], await data.Invoice.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        foreach (var name in ReturnEntitlementSchemas.Collections) Assert.Equal(0, await data.Invoice.Count(name));
        await ReturnEntitlementStorage.RequireAsync(data.Invoice.Order.Database, default);
        var balance = await allocation.Service.ReadAsync(allocation.Source.Reader, allocation.VerificationId, [allocation.InvoiceId]);
        Assert.Equal(600m, balance.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(balance.ReceiptBalance["allocated"], balance.Invoices[0]["balance"]["paid"]);
        Assert.True(await data.Invoice.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 53 }, { "state", "applied" } }).AnyAsync());
    }
    [WindowsStorageTheory]
    [InlineData("recipient-receipt")] [InlineData("invoice-journal")] [InlineData("proposal-effects")]
    public async Task Missing_actual_original_source_or_proposal_effects_fail_before_new_entitlement_DDL(string fault) {
        await using var data = await PaymentDraftData.CreateAsync(fixture); await ProposeAsync(data); await PredecessorAsync(data);
        if (fault == "recipient-receipt") await data.Invoice.Rows(ShipmentReceiptRecords.Receipts).DeleteManyAsync(new BsonDocument());
        if (fault == "invoice-journal") {
            var invoice = await data.Invoice.Rows(InvoiceBson.Invoices).Find(new BsonDocument()).SingleAsync();
            await data.Invoice.Rows("journalEntries").DeleteOneAsync(new BsonDocument("_id", invoice["journalId"]));
        }
        if (fault == "proposal-effects") await data.Invoice.Rows("outboxEvents").DeleteManyAsync(new BsonDocument("kind", ReturnResolutionRecords.Operation));
        await RefusedBeforeDdlAsync(data);
    }
    [WindowsStorageFact]
    public async Task Surviving_financial_request_effects_cannot_initialize_an_empty_attribution_pool() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); await ProposeAsync(data); await PredecessorAsync(data);
        var orphan = await data.Invoice.Rows("outboxEvents").Find(new BsonDocument("kind", ReturnResolutionRecords.Operation)).SingleAsync();
        orphan["_id"] = ObjectId.GenerateNewId(); orphan["kind"] = B.Operation + ".orphan"; orphan["sourceKey"] = B.Operation + ":orphan";
        await data.Invoice.Rows("outboxEvents").InsertOneAsync(orphan);
        await RefusedBeforeDdlAsync(data);
        foreach (var name in ReturnEntitlementSchemas.Collections) Assert.False(await data.Invoice.Rows(name).Find(new BsonDocument()).AnyAsync());
    }
    [WindowsStorageTheory]
    [InlineData("compatible")] [InlineData("ttl")] [InlineData("partial")]
    public async Task Empty_resume_preserves_stronger_operator_constraints_and_compatible_indexes_and_refuses_unsafe_indexes(string condition) {
        await using var data = await PaymentDraftData.CreateAsync(fixture); await ProposeAsync(data); await PredecessorAsync(data); var database = data.Invoice.Order.Database;
        var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999L));
        await database.CreateCollectionAsync(B.Requests, new CreateCollectionOptions<BsonDocument> { Validator = guard });
        await data.Invoice.Rows(B.Requests).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("requestedAt", 1), condition == "ttl"
            ? new CreateIndexOptions { ExpireAfter = TimeSpan.FromMinutes(1), Name = "operator_request_index" }
            : condition == "partial" ? new CreateIndexOptions<BsonDocument> { PartialFilterExpression = new BsonDocument("action", "credit"), Name = "operator_request_index" }
            : new CreateIndexOptions { Name = "operator_request_index" }));
        if (condition != "compatible") { await RefusedBeforeDdlAsync(data); return; }
        await UpgradeAsync(data); var definitions = await Definitions(database); var target = Assert.Single(definitions, row => row["name"] == B.Requests);
        Assert.True(InventorySchemas.Contains(target["options"]["validator"].AsBsonDocument, guard));
        Assert.Equal("strict", target["options"]["validationLevel"]); Assert.Equal("error", target["options"]["validationAction"]);
        var indexes = await (await data.Invoice.Rows(B.Requests).Indexes.ListAsync()).ToListAsync();
        Assert.Single(indexes, index => index["name"] == "operator_request_index");
        foreach (var key in ReturnEntitlementSchemas.Keys(B.Requests)) Assert.True(Assert.Single(indexes, index => index["key"].Equals(key))["unique"].AsBoolean);
        await data.Invoice.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 53));
        await UpgradeAsync(data); Assert.Equal(definitions, await Definitions(database));
        await ReturnEntitlementStorage.RequireAsync(database, default);
    }
}
