using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
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
    internal ActorContext SourceMaker = null!, SourceChecker = null!;
    internal long PoolId, PolicyId;
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
        ProcurementRoute route = ProcurementRoute.Ddo, bool onReceipt = false, decimal requiredAdvance = 0, bool boundStock = false)
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
            if (requiredAdvance > 0) input = input with { PaymentTerms = new("deposit", "Synthetic independently approved deposit; balance on receipt", new Money(requiredAdvance), "on_receipt", null) };
            if (boundStock) {
                await data.Base.AddGrantAsync(orders.Maker, 7280, ProcurementRequestTestData.Scope, [Permissions.StockRead]);
                await data.Base.AddGrantAsync(orders.Checker, 7281, ProcurementRequestTestData.Scope, [Permissions.StockRead]);
                data.SourceMaker = await data.Base.AddActorAsync(724, ProcurementRequestTestData.Scope,
                    Permissions.StockSourcePropose, Permissions.StockSourceRead, Permissions.StockSourcePolicyPropose, Permissions.StockSourceSuspend);
                data.SourceChecker = await data.Base.AddActorAsync(725, ProcurementRequestTestData.Scope,
                    Permissions.StockSourceApprove, Permissions.StockSourceRead, Permissions.StockSourcePolicyApprove);
                await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.SourceMaker.UserId, data.SourceChecker.UserId);
                var sources = new StockSourceSetupService(data.Database, data.Unit, new(data.Database), data.Clock);
                var source = new StockSourceSetupInput(new(new(100), new(101), new(102), new(103)), "synthetic-held-receiving", "Synthetic reviewed receiving pool",
                    StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60001, 12, null, null, "Synthetic receiving operator", "Synthetic received-unit handoff");
                var proposal = await sources.ProposeAsync(data.SourceMaker, source, Meta("held-source"));
                var approved = await sources.DecideAsync(data.SourceChecker, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
                    "Independent synthetic source approval", Meta("held-source-approved"));
                data.PoolId = approved["poolId"].AsInt64;
                var policyPreview = await sources.PreviewOperatingPolicyAsync(data.SourceMaker, data.PoolId);
                var expected = new StockSourcePolicyExpectation(policyPreview["poolRevision"].AsInt64, policyPreview["suspensionRevision"].AsInt64,
                    policyPreview["policyRevision"].AsInt64, policyPreview["configurationHash"].AsString);
                var policy = await sources.ProposeOperatingPolicyAsync(data.SourceMaker, data.PoolId, expected,
                    new(StockMovementWriter.UnitToko, null, 0, "Synthetic daily received-unit reconciliation", "Synthetic stop-and-review outage procedure"), Meta("held-policy"));
                var accepted = await sources.DecideOperatingPolicyAsync(data.SourceChecker, policy["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
                    "Independent synthetic operating-policy review", Meta("held-policy-approved"));
                data.PolicyId = accepted["policyId"].AsInt64;
                var selection = new PurchaseOrderInboundPoolSelection([.. input.Lines.Select(line => new PurchaseOrderInboundAllocation(line.RequestSkuId, line.Packages, data.PoolId, 1))]);
                var inbound = await orders.Service.PreviewInboundAsync(orders.Maker, input, selection);
                data.Proposal = await orders.Service.ProposeInboundAsync(orders.Maker, input, selection, inbound["binding"].AsString, PurchaseOrderTestData.Meta("receiving-fixture-po"));
            } else data.Proposal = await orders.ProposeAsync("receiving-fixture-po", input);
            data.Order = await orders.DecideAsync(data.Proposal, "receiving-fixture-po-approved");
            data.Maker = await data.Base.AddActorAsync(680, ProcurementRequestTestData.Scope, Permissions.ProcurementRead, Permissions.ProcurementPrepare);
            data.Receiver = await data.Base.AddActorAsync(681, ProcurementRequestTestData.Scope, Permissions.ProcurementRead, Permissions.ProcurementContainerReceive);
            if (boundStock) {
                await data.Base.AddGrantAsync(data.Maker, 7282, ProcurementRequestTestData.Scope, [Permissions.StockRead]);
                await data.Base.AddGrantAsync(data.Receiver, 7283, ProcurementRequestTestData.Scope, [Permissions.StockRead]);
            }
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
