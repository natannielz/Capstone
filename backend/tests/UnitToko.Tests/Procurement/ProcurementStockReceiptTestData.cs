using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
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

[SupportedOSPlatform("windows")]
internal sealed class ProcurementStockReceiptTestData : IAsyncDisposable
{
    internal SupplierClaimTestData Claims = null!;
    internal ProcurementInspectionTestData Inspection => Claims.Inspection;
    internal StoreTestData Base => Claims.Base;
    internal IMongoDatabase Database => Base.Database;
    internal MongoUnitOfWork Unit => Claims.Unit;
    internal MutableAuthorizationClock Clock => Claims.Clock;
    internal ActorContext Maker = null!, Reader = null!, Outsider = null!, SourceMaker = null!, SourceChecker = null!;
    internal long PoolId, PolicyId;
    internal ProcurementInspectionLineInput InspectionLine = null!;
    internal long InspectionId => Claims.InspectionId;
    internal const string Operation = "procurement.stockreceipt.receive", Reason = "Synthetic physical receipt kept entirely held; valuation and release are pending";
    internal StockSourceSetupService Sources => new(Database, Unit, new(Database), Clock);
    internal ProcurementStockReceiptService Service => new(Database, Unit, new(Database), Clock, Inspection.Receiving.Procurement.Catalog,
        Inspection.Receiving.Orders.Service, Inspection.Receiving.Service, Sources);
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key, string reason = Reason) => new(key, reason, "held-stock-receipt-test");
    internal ProcurementStockReceiptInput Input(string reference = "SYNTHETIC-STOCK-001", int packages = 1) => new(ProcurementRequestTestData.Location,
        Inspection.Receiving.OrderId, 1, Inspection.Receiving.ApprovalId, InspectionId, 1, packages, PoolId, 1, reference,
        InspectionLine.LotCode ?? "SYNTHETIC-LOT-UNKNOWN-EXPIRY", InspectionLine.ExpiryDate, Reason);
    internal static async Task<ProcurementStockReceiptTestData> CreateAsync(MongoFixture fixture, ProcurementRoute route = ProcurementRoute.Ddo,
        Func<ProcurementInspectionInput, ProcurementInspectionInput>? inspectionInput = null) {
        var data = new ProcurementStockReceiptTestData();
        try {
            data.Claims = await SupplierClaimTestData.CreateAsync(fixture, route, inspectionInput: input => {
                var observed = inspectionInput is null ? input : inspectionInput(input);
                data.InspectionLine = observed.Lines[0];
                return observed;
            });
            var scope = ProcurementRequestTestData.Scope;
            data.Maker = await data.Base.AddActorAsync(720, scope, Permissions.ProcurementRead, Permissions.StockRead, Permissions.StockReceive);
            data.Reader = await data.Base.AddActorAsync(721, scope, Permissions.ProcurementRead, Permissions.StockRead);
            data.Outsider = await data.Base.AddActorAsync(722, new(GrantScopeKind.FulfillmentWarehouse, 100,
                ShopId: data.Inspection.Receiving.Procurement.ForeignShopId, WarehouseId: data.Inspection.Receiving.Procurement.ForeignWarehouseId, ChannelId: 103), Permissions.ProcurementRead, Permissions.StockRead, Permissions.StockReceive);
            data.SourceMaker = await data.Base.AddActorAsync(724, scope, Permissions.StockSourcePropose, Permissions.StockSourceRead, Permissions.StockSourcePolicyPropose, Permissions.StockSourceSuspend);
            data.SourceChecker = await data.Base.AddActorAsync(725, scope, Permissions.StockSourceApprove, Permissions.StockSourceRead, Permissions.StockSourcePolicyApprove);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Reader.UserId, data.Outsider.UserId, data.SourceMaker.UserId, data.SourceChecker.UserId);
            var source = new StockSourceSetupInput(new(new(100), new(101), new(102), new(103)), "synthetic-held-receiving", "Synthetic reviewed receiving pool",
                StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60001, 12, null, null, "Synthetic receiving operator", "Synthetic received-unit handoff");
            var proposal = await data.Sources.ProposeAsync(data.SourceMaker, source, Meta("held-source"));
            var approved = await data.Sources.DecideAsync(data.SourceChecker, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Independent synthetic source approval", Meta("held-source-approved"));
            data.PoolId = approved["poolId"].AsInt64;
            var preview = await data.Sources.PreviewOperatingPolicyAsync(data.SourceMaker, data.PoolId);
            var expected = new StockSourcePolicyExpectation(preview["poolRevision"].AsInt64, preview["suspensionRevision"].AsInt64, preview["policyRevision"].AsInt64, preview["configurationHash"].AsString);
            var policy = await data.Sources.ProposeOperatingPolicyAsync(data.SourceMaker, data.PoolId, expected,
                new(StockMovementWriter.UnitToko, null, 0, "Synthetic daily received-unit reconciliation", "Synthetic stop-and-review outage procedure"), Meta("held-policy"));
            var accepted = await data.Sources.DecideOperatingPolicyAsync(data.SourceChecker, policy["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
                "Independent synthetic operating-policy review", Meta("held-policy-approved"));
            data.PolicyId = accepted["policyId"].AsInt64;
            Assert.False((await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync())["active"].AsBoolean);
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> RecordAsync(string key, ProcurementStockReceiptInput? input = null) {
        input ??= Input(); var preview = await Service.PreviewAsync(Maker, input);
        return await Service.ReceiveAsync(Maker, input, preview["binding"].AsString, Meta(key, input.Reason));
    }
    internal async Task<BsonDocument> StateAsync() {
        var result = new BsonDocument();
        foreach (var table in new[] { "procurementStockReceipts", "lots", "stockBalances", "movements", "procurementQuarantineDispositionHeads", "procurementQuarantineDispositions" }) {
            var rows = await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); result[table] = new BsonArray(rows);
        }
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            result[table] = new BsonArray(await Rows(table).Find(new BsonDocument(field, Operation)).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    public async ValueTask DisposeAsync() { if (Claims is not null) await Claims.DisposeAsync(); }
}
