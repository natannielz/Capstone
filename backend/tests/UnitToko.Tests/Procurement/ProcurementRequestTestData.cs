using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Organizations;

namespace UnitToko.Tests.Procurement;

// Real profile/catalog/request producers in one isolated database. No supplier, cost or receipt seeds.
internal sealed class ProcurementRequestTestData(OrganizationAddressTestData organizations) : IAsyncDisposable
{
    internal const string Requests = "procurementRequests", Decisions = "procurementRequestDecisions", Prefix = "procurement.request.";
    internal OrganizationAddressTestData Organizations { get; } = organizations;
    internal StoreTestData Base => Organizations.Base;
    internal IMongoDatabase Database => Base.Database;
    internal MutableAuthorizationClock Clock => Base.Clock;
    internal MongoUnitOfWork Unit => Organizations.Unit;
    internal CatalogAuthoringService Catalog => new(Database, Unit, new(Database), Clock);
    internal ProcurementRequestService Service => new(Database, Unit, new(Database), Clock, Catalog);
    internal ActorContext Maker = null!, Checker = null!, Reader = null!, Outsider = null!, Author = null!, ForeignAuthor = null!;
    internal CatalogMutationResult Sku = null!, SecondSku = null!, ForeignSku = null!;
    internal long ForeignShopId, ForeignWarehouseId;
    internal static readonly ProcurementLocation Location = new(100, 101, 102, 103);
    internal static readonly GrantScope Scope = new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
    internal static ProcurementRequestLineInput Line(CatalogMutationResult sku, int packages = 2) => new(sku.Id.Value, sku.Version,
        sku.CurrentDefinitionId!.Value.Value, sku.DefinitionVersion!.Value, packages, null);
    internal ProcurementRequestInput Input(ProcurementRoute route = ProcurementRoute.OmiRegular, ProcurementRequestKind kind = ProcurementRequestKind.StockNeed) =>
        new(Location, route, kind, DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime), kind == ProcurementRequestKind.IncomingNotice ? "SYNTHETIC-INCOMING-01" : null,
            "Synthetic independently reviewed procurement need", [Line(Sku)]);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic independently reviewed procurement need", "procurement-request-test");
    internal IMongoCollection<BsonDocument> Rows(string table) => Database.GetCollection<BsonDocument>(table);
    internal static async Task<ProcurementRequestTestData> CreateAsync(MongoFixture fixture, bool useSystemClock = false)
    {
        var source = await StoreTestData.CreateAsync(fixture);
        source.Clock.UseSystemClock = useSystemClock;
        try {
            source.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var data = new ProcurementRequestTestData(await OrganizationAddressTestData.PrepareAsync(source, fixture.Client));
            var channel = await data.Organizations.Organizations.ProposeAsync(data.Organizations.ProfileMaker,
                new(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Channel, 100, "RETAIL", "Synthetic retail channel", 103, 1, 0), Meta("procurement-channel"));
            await data.Organizations.Organizations.ApproveAsync(data.Organizations.ProfileChecker, channel.Id, 1, Meta("procurement-channel-approved"));
            async Task<long> CreateScope(ScopeNodeKind kind, long parent, string code, string name) {
                var proposal = await data.Organizations.Organizations.ProposeAsync(data.Organizations.ProfileMaker,
                    new(OrganizationChangeKind.Create, kind, parent, code, name), Meta("procurement-" + code));
                return (await data.Organizations.Organizations.ApproveAsync(data.Organizations.ProfileChecker, proposal.Id, 1, Meta("procurement-" + code + "-approved"))).ScopeId!.Value;
            }
            data.ForeignShopId = await CreateScope(ScopeNodeKind.Shop, 100, "OTHER", "Synthetic other shop");
            data.ForeignWarehouseId = await CreateScope(ScopeNodeKind.Warehouse, data.ForeignShopId, "OTHERWH", "Synthetic other warehouse");
            data.Maker = await source.AddActorAsync(610, Scope, Permissions.ProcurementRead, Permissions.ProcurementPrepare);
            data.Checker = await source.AddActorAsync(611, Scope, Permissions.ProcurementRead, Permissions.ProcurementApprove);
            data.Reader = await source.AddActorAsync(612, Scope, Permissions.ProcurementRead);
            data.Outsider = await source.AddActorAsync(613, new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: data.ForeignShopId, WarehouseId: data.ForeignWarehouseId, ChannelId: 103),
                Permissions.ProcurementRead, Permissions.ProcurementPrepare, Permissions.ProcurementApprove);
            data.Author = await source.AddActorAsync(614, CatalogAuthoringTestData.Shop, Permissions.CatalogRead, Permissions.CatalogManage);
            var foreignOwner = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: data.ForeignShopId, ChannelId: 103);
            data.ForeignAuthor = await source.AddActorAsync(615, foreignOwner, Permissions.CatalogManage);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Checker.UserId, data.Reader.UserId, data.Outsider.UserId);
            var category = await data.Catalog.SaveCategoryAsync(data.Author, new(CatalogAuthoringTestData.Shop, null, 0, "ATK", "Synthetic stationery"), Meta("procurement-category"));
            var family = await data.Catalog.CreateFamilyAsync(data.Author, new(CatalogAuthoringTestData.Shop, category.Id, "Pensil pengadaan", "Synthetic procurement packaging", []), Meta("procurement-family"));
            data.Sku = await data.Catalog.CreateSkuAsync(data.Author, new(family.Id, family.Version, "PROC-PENCIL-12", "procurement-pencil", CatalogAuthoringTestData.Definition()), Meta("procurement-sku-12"));
            data.SecondSku = await data.Catalog.CreateSkuAsync(data.Author, new(family.Id, family.Version, "PROC-PENCIL-1", "procurement-pencil", CatalogAuthoringTestData.Definition(1, "Satuan")), Meta("procurement-sku-1"));
            var otherCategory = await data.Catalog.SaveCategoryAsync(data.ForeignAuthor, new(foreignOwner, null, 0, "OTHERATK", "Synthetic foreign stationery"), Meta("procurement-other-category"));
            var otherFamily = await data.Catalog.CreateFamilyAsync(data.ForeignAuthor, new(foreignOwner, otherCategory.Id, "Foreign pencil", "Synthetic foreign packaging", []), Meta("procurement-other-family"));
            data.ForeignSku = await data.Catalog.CreateSkuAsync(data.ForeignAuthor, new(otherFamily.Id, otherFamily.Version, "FOREIGN-PENCIL-12", "foreign-pencil", CatalogAuthoringTestData.Definition()), Meta("procurement-foreign-sku"));
            return data;
        } catch { await source.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> SubmitAsync(string key, ProcurementRequestInput? input = null) {
        input ??= Input(); var preview = await Service.PreviewAsync(Maker, input);
        return await Service.SubmitAsync(Maker, input, preview["binding"].AsString, Meta(key) with { Reason = input.Reason });
    }
    internal async Task<BsonDocument> DecideAsync(BsonDocument request, string key, ProcurementRequestDecision decision = ProcurementRequestDecision.Approve) {
        var preview = await Service.PreviewDecisionAsync(Checker, request["requestId"].AsInt64, decision);
        return await Service.DecideAsync(Checker, request["requestId"].AsInt64, 1, decision, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> StateAsync() {
        var state = new BsonDocument();
        foreach (var table in new[] { Requests, Decisions }) {
            var rows = await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); state[table] = new BsonArray(rows);
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            state[table] = new BsonArray(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.request(?:[.:]|$)")))
                .Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
    internal async Task NoBusinessEffectsAsync() {
        foreach (var table in new[] { "suppliers", "purchaseOrders", "goodsReceipts", "supplierInvoices", "stockBalances", "movements", "reservations", "journalEntries", "invoices", "creditVersions", "paymentAllocations", "refundPayouts" })
            Assert.False(await Rows(table).Find(new BsonDocument()).AnyAsync());
    }
    public ValueTask DisposeAsync() => Base.DisposeAsync();
}
