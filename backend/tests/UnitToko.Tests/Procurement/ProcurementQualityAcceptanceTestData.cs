using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using B = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;

namespace UnitToko.Tests.Procurement;

// Genuine video inspection and immutable held receipt. Local personnel and quality-policy
// approvals are explicit synthetic inputs, not production policy acceptance or stock release.
[SupportedOSPlatform("windows")]
internal sealed class ProcurementQualityAcceptanceTestData(ProcurementStockReceiptTestData stock) : IAsyncDisposable
{
    internal const string Reason = "Synthetic physical quality acceptance only; every package stays held and unvalued";
    internal ProcurementStockReceiptTestData Stock { get; } = stock;
    internal ProcurementInspectionTestData Inspection => Stock.Inspection;
    internal StoreTestData Base => Stock.Base;
    internal IMongoDatabase Database => Stock.Database;
    internal MongoUnitOfWork Unit => Stock.Unit;
    internal MutableAuthorizationClock Clock => Stock.Clock;
    internal ActorContext Maker = null!, Checker = null!, Reader = null!, Outsider = null!;
    internal BsonDocument Receipt = null!, Policy = null!;
    internal string AcceptedCondition = null!;
    internal long ReceiptId => Receipt["receiptId"].AsInt64;
    internal long PolicyId => Policy["_id"].AsInt64;
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal ProcurementQualityAcceptanceService Service => new(Database, Unit, new(Database), Clock,
        Inspection.Receiving.Procurement.Catalog, Inspection.Receiving.Orders.Service, Inspection.Receiving.Service, Stock.Sources);
    internal static AuthorizationCommandMetadata Meta(string key, string reason = Reason) => new(key, reason, "synthetic-quality-acceptance-test");
    internal ProcurementQualityAcceptanceInput Input(int packages = 1) => new(ProcurementRequestTestData.Location, ReceiptId, 1, packages, PolicyId, 1,
        DateTimeOffset.FromUnixTimeMilliseconds(Clock.UtcNow.ToUnixTimeMilliseconds()), "SYNTHETIC-QUALITY-MAKER-001", Stock.InspectionLine.PutawayReference!,
        "SYNTHETIC-SEGREGATED-SUBSET-A", AcceptedCondition, Stock.Input().LotReference, Stock.Input().ExpiresOn,
        Stock.Input().ExpiresOn is null ? "Synthetic explicit non-expiring package determination under the approved procedure" : null, Reason);
    internal ProcurementQualityAcceptanceObservation Observation(ProcurementQualityAcceptanceInput? input = null) {
        input ??= Input(); return new(DateTimeOffset.FromUnixTimeMilliseconds(Clock.UtcNow.ToUnixTimeMilliseconds()), input.AcceptedPackages,
            "SYNTHETIC-QUALITY-CHECKER-001", input.PutawayReference, input.SegregationReference, input.AcceptedCondition,
            input.LotReference, input.ExpiresOn, input.NonExpiringReason);
    }
    internal static async Task<ProcurementQualityAcceptanceTestData> CreateAsync(MongoFixture fixture, bool nonExpiring = false,
        int expiryDays = 30, bool createPolicy = true, bool allowNonExpiring = true, int receiptPackages = 3)
    {
        var stock = await ProcurementStockReceiptTestData.CreateAsync(fixture, inspectionInput: input => input with {
            Lines = [input.Lines[0] with { ExpiredPackages = 0, ExpiryDate = nonExpiring ? null : input.InspectionDate.AddDays(expiryDays),
                Discrepancy = "Synthetic mixed damaged/opened observations overlap; no healthy quantity is inferred" }] });
        var data = new ProcurementQualityAcceptanceTestData(stock);
        try {
            data.Receipt = await stock.RecordAsync("quality-held-receipt", stock.Input(packages: receiptPackages));
            var source = await data.Rows(ProcurementStockReceiptBson.Receipts).Find(new BsonDocument("_id", data.ReceiptId)).SingleAsync();
            var package = PurchaseOrderService.FundingOrderView(source["source"].AsBsonDocument)["lines"].AsBsonArray.Single(row => row["skuId"] == stock.InspectionLine.RequestSkuId);
            data.AcceptedCondition = package["packageKind"] == "sealed" ? "sealed_undamaged" : "intact_as_defined";
            string[] reads = [Permissions.ProcurementRead, Permissions.StockRead, Permissions.ProcurementQualityAcceptanceRead];
            data.Maker = await data.Base.AddActorAsync(770, ProcurementRequestTestData.Scope, [.. reads, Permissions.ProcurementQualityAcceptancePropose]);
            data.Checker = await data.Base.AddActorAsync(771, ProcurementRequestTestData.Scope, [.. reads, Permissions.ProcurementQualityAcceptanceDecide]);
            data.Reader = await data.Base.AddActorAsync(772, ProcurementRequestTestData.Scope, reads);
            data.Outsider = await data.Base.AddActorAsync(773, new(GrantScopeKind.FulfillmentWarehouse, 100,
                ShopId: data.Inspection.Receiving.Procurement.ForeignShopId, WarehouseId: data.Inspection.Receiving.Procurement.ForeignWarehouseId, ChannelId: 103),
                [.. reads, Permissions.ProcurementQualityAcceptancePropose, Permissions.ProcurementQualityAcceptanceDecide]);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Checker.UserId, data.Reader.UserId, data.Outsider.UserId);
            if (createPolicy) await data.InstallPolicyAsync(allowNonExpiring);
            Assert.Equal(2, stock.InspectionLine.DamagedPackages); Assert.Equal(2, stock.InspectionLine.OpenedPackages);
            Assert.False(await data.Rows("procurementStockValuationAllocations").Find(new BsonDocument()).AnyAsync());
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    internal async Task InstallPolicyAsync(bool allowNonExpiring, TimeSpan? lifetime = null) {
        var approvedLifetime = lifetime ?? TimeSpan.FromDays(1);
        if (approvedLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        var people = await Unit.ExecuteAsync((tx, ct) => new PersonRegistryService(Database, Clock).ResolveAsync(tx,
            [Stock.SourceMaker.UserId, Stock.SourceChecker.UserId], ct));
        Policy = new() { { "_id", (await new IdAllocator(Database).NextAsync(B.Policies)).Value }, { "schemaVersion", 1 }, { "revision", 1L },
            { "active", true }, { "location", ProcurementRequestBson.Location(ProcurementRequestTestData.Location) },
            { "qualityProcedureReference", "SYNTHETIC-INDEPENDENT-SEALED-UNDAMAGED-OBSERVATION" },
            { "putawayProcedureReference", "SYNTHETIC-SEGREGATED-HELD-LOCATION-PROCEDURE" }, { "allowNonExpiring", allowNonExpiring },
            { "nonExpiringProcedureReference", allowNonExpiring ? "SYNTHETIC-EXPLICIT-NON-EXPIRING-DETERMINATION" : BsonNull.Value },
            { "maximumObservationAgeMilliseconds", 900000L }, { "approvalReference", "SYNTHETIC-LOCAL-QUALITY-POLICY-NOT-PRODUCTION-ACCEPTANCE" },
            { "proposedBy", Stock.SourceMaker.UserId }, { "approvedBy", Stock.SourceChecker.UserId },
            { "people", new BsonArray(people.Select(ProcurementRequestBson.Person)) }, { "approvedAt", Clock.UtcNow.UtcDateTime },
            { "expiresAt", Clock.UtcNow.Add(approvedLifetime).UtcDateTime }, { "fence", 0L } };
        await Rows(B.Policies).InsertOneAsync(Policy);
    }
    internal async Task<BsonDocument> ProposeAsync(string key, ProcurementQualityAcceptanceInput? input = null) {
        input ??= Input(); var preview = await Service.PreviewAsync(Maker, input);
        return await Service.ProposeAsync(Maker, input, preview["binding"].AsString, Meta(key, input.Reason));
    }
    internal async Task<BsonDocument> DecideAsync(long id, string key, ProcurementQualityAcceptanceInput? input = null,
        ProcurementQualityAcceptanceDecision decision = ProcurementQualityAcceptanceDecision.Approve) {
        var observation = decision == ProcurementQualityAcceptanceDecision.Approve ? Observation(input) : null;
        var preview = await Service.PreviewDecisionAsync(Checker, id, decision, observation);
        return await Service.DecideAsync(Checker, id, 1, decision, observation, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> StateAsync() {
        var result = new BsonDocument();
        foreach (var name in ProcurementQualityAcceptanceSchemas.Collections) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); result[name] = new BsonArray(rows);
        }
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            result[table] = new BsonArray(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.qualityacceptance\."))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    internal async Task<BsonDocument> PreservedAsync() {
        var result = await Stock.StateAsync();
        foreach (var table in new[] { "inventoryPools", "procurementInspectionSubmissions", "procurementStockValuationAllocations", "procurementStockValuationReceiptHeads",
            "journalEntries", "cashMovements", "procurementSupplierPayables", "procurementSupplierPayableApplications" }) {
            var rows = await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            // The pool's coordination fence is the sole source field quality commands may change.
            if (table == "inventoryPools") foreach (var row in rows) row.Remove("fence"); result[table] = new BsonArray(rows);
        }
        return result;
    }
    internal ProcurementQualityAcceptanceService NewService(MongoClient client) {
        var db = client.GetDatabase(Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(db);
        var catalog = new CatalogAuthoringService(db, unit, ids, Clock); var requests = new ProcurementRequestService(db, unit, ids, Clock, catalog);
        var orders = new PurchaseOrderService(db, unit, ids, Clock, catalog, requests, new(db, unit, ids, Clock, catalog));
        return new(db, unit, ids, Clock, catalog, orders, new(db, unit, ids, Clock, catalog, orders, requests), new(db, unit, ids, Clock));
    }
    public ValueTask DisposeAsync() => Stock.DisposeAsync();
}
