using System.Runtime.Versioning;
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

// Genuine approved PO, container receipt, private-video inspection, and optional private invoice.
// Personnel grants and local scanner approvals are explicit synthetic test inputs only.
[SupportedOSPlatform("windows")]
internal sealed class SupplierClaimTestData : IAsyncDisposable
{
    internal const string RequestOperation = "procurement.supplierclaim.request", ApproveOperation = "procurement.supplierclaim.approve", RejectOperation = "procurement.supplierclaim.reject";
    internal ProcurementInspectionTestData Inspection = null!;
    internal SupplierDocumentTestData? Documents;
    internal BsonDocument InspectionResult = null!, InvoiceMatch = null!;
    internal ActorContext Maker = null!, Checker = null!, Reader = null!, Outsider = null!;
    internal StoreTestData Base => Inspection.Base;
    internal IMongoDatabase Database => Inspection.Database;
    internal MongoUnitOfWork Unit => Inspection.Unit;
    internal MutableAuthorizationClock Clock => Inspection.Clock;
    internal long InspectionId => InspectionResult["inspectionId"].AsInt64;
    internal SupplierClaimService Service => new(Database, Unit, new(Database), Clock, Inspection.Receiving.Procurement.Catalog, Inspection.Receiving.Orders.Service, Inspection.Receiving.Service);
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal const string Reason = "Synthetic supplier claim; goods remain quarantined and no financial release is authorized";
    internal static AuthorizationCommandMetadata Meta(string key, string reason = Reason) => new(key, reason, "supplier-claim-test");
    internal SupplierClaimInput Input(string reference = "SYNTHETIC-CLAIM-001", int packages = 1, SupplierClaimBasis basis = SupplierClaimBasis.PhysicalReceived,
        SupplierClaimResolution resolution = SupplierClaimResolution.Replacement) => new(ProcurementRequestTestData.Location, Inspection.Receiving.OrderId, 1,
        Inspection.Receiving.ApprovalId, reference, AccountingCalendar.BusinessDate(Clock.UtcNow), AccountingCalendar.BusinessDate(Clock.UtcNow).AddDays(7),
        "Synthetic explicit seven-day supplier follow-up instruction", resolution, Reason,
        [new(basis, InspectionId, 1, packages, basis == SupplierClaimBasis.ShortageObserved ? "shortage" : Documents is null ? "damaged" : "other",
            Documents?.DocumentId, Documents is null ? null : 1, Documents is null ? null : InvoiceMatch["matchId"].AsInt64, Documents is null ? null : 1)]);
    internal static async Task<SupplierClaimTestData> CreateAsync(MongoFixture fixture, ProcurementRoute route = ProcurementRoute.Ddo, bool invoice = false, bool shortage = false,
        Func<ProcurementInspectionInput, ProcurementInspectionInput>? inspectionInput = null, bool boundStock = false) {
        var data = new SupplierClaimTestData();
        try {
            if (invoice) {
                if (inspectionInput is not null) throw new ArgumentException("Custom physical observations belong to the inspection-only fixture", nameof(inspectionInput));
                data.Documents = await SupplierDocumentTestData.CreateAsync(fixture, route, boundStock: boundStock); data.Inspection = data.Documents.Inspection;
                data.InspectionResult = data.Documents.InspectionResult; await data.Documents.SubmitOriginalAsync();
                data.InvoiceMatch = await data.Documents.MatchAsync("supplier-claim-invoice-basis");
            } else {
                data.Inspection = await ProcurementInspectionTestData.CreateAsync(fixture, route, createDraft: false, boundStock: boundStock);
                var input = data.Inspection.Input();
                input = input with { Lines = [input.Lines[0] with { DeclaredPackages = 4, DamagedPackages = 2 }] };
                if (shortage) input = input with { Lines = [input.Lines[0] with { DeclaredPackages = 5 }] };
                if (inspectionInput is not null) input = inspectionInput(input);
                data.Inspection.Draft = await data.Inspection.DraftAsync("supplier-claim-inspection", input);
                await data.Inspection.UploadAsync(); await data.Inspection.ValidateAsync();
                data.InspectionResult = await data.Inspection.SubmitAsync(await data.Inspection.PreviewSubmitAsync());
            }
            var scope = ProcurementRequestTestData.Scope;
            data.Maker = await data.Base.AddActorAsync(710, scope, Permissions.ProcurementRead, Permissions.SupplierClaim);
            data.Checker = await data.Base.AddActorAsync(711, scope, Permissions.ProcurementRead, Permissions.SupplierClaimApprove);
            data.Reader = await data.Base.AddActorAsync(712, scope, Permissions.ProcurementRead);
            data.Outsider = await data.Base.AddActorAsync(713, new(GrantScopeKind.FulfillmentWarehouse, 100,
                ShopId: data.Inspection.Receiving.Procurement.ForeignShopId, WarehouseId: data.Inspection.Receiving.Procurement.ForeignWarehouseId, ChannelId: 103),
                Permissions.ProcurementRead, Permissions.SupplierClaim, Permissions.SupplierClaimApprove);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Checker.UserId, data.Reader.UserId, data.Outsider.UserId);
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> RequestAsync(string key, SupplierClaimInput? input = null) {
        input ??= Input(); var preview = await Service.PreviewAsync(Maker, input);
        return await Service.RequestAsync(Maker, input, preview["binding"].AsString, Meta(key, input.Reason));
    }
    internal async Task<BsonDocument> DecideAsync(long claimId, string key, SupplierClaimDecision decision = SupplierClaimDecision.Approve) {
        var preview = await Service.PreviewDecisionAsync(Checker, claimId, decision);
        return await Service.DecideAsync(Checker, claimId, 1, new(decision, "Independent synthetic review; supplier action still pending"), preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> StateAsync() {
        var state = new BsonDocument(); foreach (var name in UnitToko.Platform.Mongo.Migrations.SupplierClaimSchemas.Collections) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); state[name] = new BsonArray(rows);
        }
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            state[table] = new BsonArray(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.supplierclaim\."))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
    public async ValueTask DisposeAsync() { if (Documents is not null) await Documents.DisposeAsync(); else if (Inspection is not null) await Inspection.DisposeAsync(); }
}
