using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Procurement;

// Genuine approved request/supplier/PO producers. External documents and observed
// container facts are explicitly synthetic; no funding or sellable stock is seeded.
internal sealed class ProcurementReceivingTestData(PurchaseOrderTestData orders) : IAsyncDisposable
{
    internal const string Notices = "procurementArrivalNotices", Heads = "procurementContainerHeads", Intakes = "procurementContainerIntakes";
    internal const string NoticeOperation = "procurement.receiving.notice", IntakeOperation = "procurement.receiving.intake";
    internal PurchaseOrderTestData Orders { get; } = orders;
    internal ProcurementRequestTestData Procurement => Orders.Procurement;
    internal StoreTestData Base => Orders.Base;
    internal IMongoDatabase Database => Orders.Database;
    internal MongoUnitOfWork Unit => Orders.Unit;
    internal MutableAuthorizationClock Clock => Orders.Clock;
    internal ProcurementRoute Route;
    internal ActorContext Maker = null!, Receiver = null!, Reader = null!, Outsider = null!;
    internal BsonDocument Proposal = null!, Order = null!;
    internal long OrderId => Order["orderId"].AsInt64;
    internal long ApprovalId => Order["decisionId"].AsInt64;
    internal ProcurementReceivingService Service => new(Database, Unit, new(Database), Clock, Procurement.Catalog, Orders.Service, Procurement.Service);
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key,
        "Synthetic observed supplier document and container custody; no stock or financial posting", "procurement-receiving-test");
    internal ProcurementArrivalNoticeInput NoticeInput(string reference = "SYNTHETIC-SUPPLIER-DELIVERY-001", int containers = 5) => new(
        ProcurementRequestTestData.Location, OrderId, 1, ApprovalId, AccountingCalendar.BusinessDate(Clock.UtcNow), reference,
        Route == ProcurementRoute.Ddo ? "SYNTHETIC-DDO-REQUEST-001" : null,
        Route == ProcurementRoute.Ddo ? "SYNTHETIC-DDO-TELEPHONE-001" : null,
        containers, "Synthetic actual supplier acknowledgement; item inspection and putaway remain separate");
    internal ProcurementContainerIntakeInput IntakeInput(string reference = "SYNTHETIC-CONTAINER-INTAKE-001") => new(
        AccountingCalendar.BusinessDate(Clock.UtcNow), reference, 3, 1, 1, "mixed",
        "One unannounced excess container held pending separate item inspection");

    internal static async Task<ProcurementReceivingTestData> CreateAsync(MongoFixture fixture,
        ProcurementRoute route = ProcurementRoute.Ddo, bool onReceipt = false)
    {
        var orders = await PurchaseOrderTestData.CreateAsync(fixture);
        try {
            var data = new ProcurementReceivingTestData(orders) { Route = route };
            if (route != ProcurementRoute.Ddo) {
                var need = data.Procurement.Input(route, route == ProcurementRoute.OmiRegular ? ProcurementRequestKind.StockNeed : ProcurementRequestKind.PurchaseRequest) with {
                    Lines = [ProcurementRequestTestData.Line(data.Procurement.Sku, 6) with { Specification = "Synthetic approved logo specification" }] };
                orders.Request = await data.Procurement.SubmitAsync("receiving-route-request", need);
                orders.RequestDecision = await data.Procurement.DecideAsync(orders.Request, "receiving-route-request-approved");
            }
            var input = orders.Input();
            if (route == ProcurementRoute.OmiRegular) input = input with { ExternalOrderReference = "SYNTHETIC-OMI-ORDER-001" };
            if (onReceipt) input = input with { PaymentTerms = new("on_receipt", "Synthetic approved on-receipt terms", new Money(0), "on_receipt", null) };
            data.Proposal = await orders.ProposeAsync("receiving-fixture-po", input);
            data.Order = await orders.DecideAsync(data.Proposal, "receiving-fixture-po-approved");
            data.Maker = await data.Base.AddActorAsync(680, ProcurementRequestTestData.Scope, Permissions.ProcurementRead, Permissions.ProcurementPrepare);
            data.Receiver = await data.Base.AddActorAsync(681, ProcurementRequestTestData.Scope, Permissions.ProcurementRead, Permissions.ProcurementContainerReceive);
            data.Reader = await data.Base.AddActorAsync(682, ProcurementRequestTestData.Scope, Permissions.ProcurementRead);
            data.Outsider = await data.Base.AddActorAsync(683, new(GrantScopeKind.FulfillmentWarehouse, 100,
                ShopId: data.Procurement.ForeignShopId, WarehouseId: data.Procurement.ForeignWarehouseId, ChannelId: 103),
                Permissions.ProcurementRead, Permissions.ProcurementPrepare, Permissions.ProcurementContainerReceive);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Receiver.UserId, data.Reader.UserId, data.Outsider.UserId);
            return data;
        } catch { await orders.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> NoticeAsync(string key = "receiving-notice", ProcurementArrivalNoticeInput? input = null)
    {
        input ??= NoticeInput(); var preview = await Service.PreviewNoticeAsync(Maker, input);
        return await Service.RecordNoticeAsync(Maker, input, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> IntakeAsync(long noticeId, long revision = 0, string key = "receiving-intake", ProcurementContainerIntakeInput? input = null)
    {
        input ??= IntakeInput(); var preview = await Service.PreviewIntakeAsync(Receiver, noticeId, revision, input);
        return await Service.ReceiveAsync(Receiver, noticeId, revision, input, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> StateAsync()
    {
        var state = new BsonDocument();
        foreach (var name in new[] { Notices, Heads, Intakes }) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence");
            state[name] = new BsonArray(rows);
        }
        foreach (var (name, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            state[name] = new BsonArray(await Rows(name).Find(new BsonDocument(field, new BsonDocument("$in", new BsonArray { NoticeOperation, IntakeOperation })))
                .Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
    public ValueTask DisposeAsync() => Orders.DisposeAsync();
}
