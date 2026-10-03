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

// The admitted purchase order is produced by real request/supplier/order commands. Only the operator policy and people are synthetic local inputs.
internal sealed class ProcurementFundingTestData(PurchaseOrderTestData orders) : IAsyncDisposable
{
    internal const string Policies = "procurementFundingPolicies", Requests = "procurementFundingRequests", Decisions = "procurementFundingDecisions",
        Heads = "procurementFundingHeads", Allocations = "procurementFundingAllocations", Prefix = "procurement.funding.";
    internal const long PolicyId = 8001;
    internal PurchaseOrderTestData Orders { get; } = orders;
    internal ProcurementRequestTestData Procurement => Orders.Procurement;
    internal StoreTestData Base => Orders.Base;
    internal IMongoDatabase Database => Base.Database;
    internal MongoUnitOfWork Unit => Orders.Unit;
    internal MutableAuthorizationClock Clock => Base.Clock;
    internal ProcurementFundingService Service => new(Database, Unit, new(Database), Clock, Procurement.Catalog, Orders.Service);
    internal ActorContext Maker = null!, Checker = null!, Reader = null!, Outsider = null!;
    internal BsonDocument Order = null!, Proposal = null!;
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Explicit synthetic internal advance funding review", "procurement-funding-test");
    internal ProcurementFundingInput Input(decimal amount = 10000) {
        var date = AccountingCalendar.BusinessDate(Clock.UtcNow);
        return new(ProcurementRequestTestData.Location, Order["orderId"].AsInt64, 1, Order["decisionId"].AsInt64, PolicyId, 1, new Money(amount), date, date.AddDays(2));
    }
    internal static async Task<ProcurementFundingTestData> CreateAsync(MongoFixture fixture, bool positiveAdvance = true) {
        var orders = await PurchaseOrderTestData.CreateAsync(fixture);
        try {
            var input = orders.Input(6);
            if (positiveAdvance) input = input with { PaymentTerms = new("deposit", "Explicit synthetic deposit; balance on receipt", new Money(20000), "on_receipt", null) };
            var proposal = await orders.ProposeAsync("funding-fixture-po", input);
            var order = await orders.DecideAsync(proposal, "funding-fixture-po-approved");
            return await AttachAsync(orders, proposal, order);
        } catch { await orders.DisposeAsync(); throw; }
    }
    internal static async Task<ProcurementFundingTestData> AttachAsync(PurchaseOrderTestData orders, BsonDocument proposal, BsonDocument order) {
            var data = new ProcurementFundingTestData(orders) { Proposal = proposal, Order = order };
            data.Maker = await data.Base.AddActorAsync(640, ProcurementRequestTestData.Scope, Permissions.FundingRead, Permissions.FundingRequest);
            data.Checker = await data.Base.AddActorAsync(641, ProcurementRequestTestData.Scope, Permissions.FundingRead, Permissions.FundingApprove);
            data.Reader = await data.Base.AddActorAsync(642, ProcurementRequestTestData.Scope, Permissions.FundingRead);
            data.Outsider = await data.Base.AddActorAsync(643, new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: data.Procurement.ForeignShopId, WarehouseId: data.Procurement.ForeignWarehouseId, ChannelId: 103), Permissions.FundingRead, Permissions.FundingRequest, Permissions.FundingApprove);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Checker.UserId, data.Reader.UserId, data.Outsider.UserId);
            await data.Rows(Policies).InsertOneAsync(new BsonDocument { { "_id", PolicyId }, { "schemaVersion", 1 },
                { "location", new BsonDocument { { "organizationId", 100L }, { "shopId", 101L }, { "warehouseId", 102L }, { "channelId", 103L } } },
                { "revision", 1L }, { "fence", 0L }, { "eligibleRoutes", new BsonArray { "ddo" } }, { "allowInstallments", true }, { "reserveUndisbursed", true },
                { "requiresAdditionalBudgetSource", false }, { "requiresAdditionalLimitSource", false },
                { "operatingOwnerReference", "SYNTHETIC-LOCAL-FINANCE-OWNER" }, { "procedureReference", "SYNTHETIC-LOCAL-ADVANCE-PROCEDURE" },
                { "approvalReference", "SYNTHETIC-LOCAL-POLICY-APPROVAL" }, { "approvedAt", data.Clock.UtcNow.AddHours(-1).UtcDateTime },
                { "expiresAt", data.Clock.UtcNow.AddDays(30).UtcDateTime }, { "active", true } });
            return data;
    }
    internal async Task<BsonDocument> RequestAsync(string key, ProcurementFundingInput? input = null) {
        input ??= Input(); var preview = await Service.PreviewAsync(Maker, input);
        return await Service.RequestAsync(Maker, input, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> DecideAsync(BsonDocument request, string key, ProcurementFundingDecision decision = ProcurementFundingDecision.Approve) {
        var preview = await Service.PreviewDecisionAsync(Checker, request["requestId"].AsInt64, decision);
        return await Service.DecideAsync(Checker, request["requestId"].AsInt64, 1, decision, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> StateAsync() {
        var result = new BsonDocument();
        foreach (var name in new[] { Requests, Decisions, Heads, Allocations }) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); result[name] = new BsonArray(rows);
        }
        foreach (var (name, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            result[name] = new BsonArray(await Rows(name).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.funding(?:[.:]|$)"))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    public ValueTask DisposeAsync() => Orders.DisposeAsync();
}
