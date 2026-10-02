using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Procurement;

// Actual request, supplier, catalog and profile producers; commercial documents/amounts are explicit synthetic test inputs.
internal sealed class PurchaseOrderTestData(SupplierTestData suppliers) : IAsyncDisposable
{
    internal const string Proposals = "purchaseOrderProposals", Decisions = "purchaseOrderDecisions", Orders = "purchaseOrders", Prefix = "procurement.purchase.order.";
    internal SupplierTestData Suppliers { get; } = suppliers;
    internal ProcurementRequestTestData Procurement => Suppliers.Procurement;
    internal StoreTestData Base => Suppliers.Base;
    internal IMongoDatabase Database => Base.Database;
    internal MongoUnitOfWork Unit => Suppliers.Unit;
    internal MutableAuthorizationClock Clock => Base.Clock;
    internal PurchaseOrderService Service => new(Database, Unit, new(Database), Clock, Procurement.Catalog, Procurement.Service, Suppliers.Service);
    internal ActorContext Maker = null!, Checker = null!;
    internal ActorContext Reader => Procurement.Reader;
    internal ActorContext Outsider => Procurement.Outsider;
    internal BsonDocument Request = null!, RequestDecision = null!, Supplier = null!;
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Explicit synthetic commercial purchase-order review", "purchase-order-test");
    internal PurchaseOrderLineInput Line(int packages = 3) => new(Procurement.Sku.Id.Value, packages, "supplier", "000001", "000012",
        Procurement.Sku.CurrentDefinitionId!.Value.Value, Procurement.Sku.DefinitionVersion!.Value, new Money(10000), new Money(3300), new Money(700));
    internal PurchaseOrderInput Input(int packages = 3) {
        var date = DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime);
        return new(ProcurementRequestTestData.Location, Request["requestId"].AsInt64, 1, RequestDecision["decisionId"].AsInt64,
            Supplier["supplierId"].AsInt64, Supplier["versionId"].AsInt64, Supplier["version"].AsInt64, date, "IDR",
            new("quote", "SYNTHETIC-QUOTE-0001", date, Clock.UtcNow.AddDays(1)), null,
            new("credit", "Synthetic approved credit terms, due in thirty days", new Money(0), "due_date", date.AddDays(30)), [Line(packages)]);
    }
    internal static async Task<PurchaseOrderTestData> CreateAsync(MongoFixture fixture) {
        var suppliers = await SupplierTestData.CreateAsync(fixture);
        try {
            var data = new PurchaseOrderTestData(suppliers);
            var supplier = await suppliers.ProposeAsync("po-fixture-supplier"); var observation = await suppliers.ObserveAsync(supplier, "po-fixture-supplier-observation");
            data.Supplier = await suppliers.DecideAsync(supplier, "po-fixture-supplier-approved", observation["recordId"].AsInt64);
            var need = data.Procurement.Input(ProcurementRoute.Ddo, ProcurementRequestKind.PurchaseRequest) with {
                Lines = [ProcurementRequestTestData.Line(data.Procurement.Sku, 6) with { Specification = "Synthetic approved logo specification" }] };
            data.Request = await data.Procurement.SubmitAsync("po-fixture-request", need);
            data.RequestDecision = await data.Procurement.DecideAsync(data.Request, "po-fixture-request-approved");
            data.Maker = await data.Base.AddActorAsync(630, ProcurementRequestTestData.Scope, Permissions.ProcurementRead, Permissions.ProcurementPrepare);
            data.Checker = await data.Base.AddActorAsync(631, ProcurementRequestTestData.Scope, Permissions.ProcurementRead, Permissions.ProcurementApprove);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Checker.UserId); return data;
        } catch { await suppliers.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> ProposeAsync(string key, PurchaseOrderInput? input = null) {
        input ??= Input(); var preview = await Service.PreviewAsync(Maker, input);
        return await Service.ProposeAsync(Maker, input, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> DecideAsync(BsonDocument proposal, string key, PurchaseOrderDecision decision = PurchaseOrderDecision.Approve) {
        var preview = await Service.PreviewDecisionAsync(Checker, proposal["proposalId"].AsInt64, decision);
        return await Service.DecideAsync(Checker, proposal["proposalId"].AsInt64, 1, decision, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> StateAsync() {
        var result = new BsonDocument();
        foreach (var name in new[] { Proposals, Decisions, Orders }.Concat(ProcurementRequestPurchaseOrderSchemas.Collections)) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); result[name] = new BsonArray(rows);
        }
        foreach (var (name, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            result[name] = new BsonArray(await Rows(name).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.purchase\.order(?:[.:]|$)"))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    internal async Task NoFinancialOrReceiptEffectsAsync() {
        foreach (var name in new[] { "goodsReceipts", "supplierInvoices", "stockBalances", "movements", "reservations", "journalEntries", "invoices", "paymentAllocations", "refundPayouts", "receivingAccounts", "disbursements", "bankReceipts" })
            Assert.False(await Rows(name).Find(new BsonDocument()).AnyAsync());
    }
    public ValueTask DisposeAsync() => Suppliers.DisposeAsync();
}
