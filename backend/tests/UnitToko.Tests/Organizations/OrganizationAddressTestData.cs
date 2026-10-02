using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Organizations;
using UnitToko.Tests.Authorization;
using B = UnitToko.Platform.Organizations.OrganizationAddressBson;

namespace UnitToko.Tests.Organizations;

// Actual organization/address commands with explicitly synthetic reviewed participants.
internal sealed class OrganizationAddressTestData(StoreTestData data, MongoClient client) : IAsyncDisposable
{
    internal StoreTestData Base { get; } = data;
    internal IMongoDatabase Database => Base.Database;
    internal MutableAuthorizationClock Clock => Base.Clock;
    internal MongoUnitOfWork Unit { get; } = new(client);
    internal ActorContext ProfileMaker = null!, ProfileChecker = null!, Maker = null!, Checker = null!, Reader = null!, WarehouseReader = null!, Orderer = null!;
    internal OrganizationMasterDataService Organizations => new(Database, Base.Store, new(Database), Clock);
    internal OrganizationAddressService Service => new(Database, Unit, new(Database), Clock, Organizations);
    internal static readonly OrganizationAddressTarget Branch = new(ScopeNodeKind.Branch, 11);
    internal static readonly OrganizationAddressTarget Buyer = new(ScopeNodeKind.BuyingOrganization, 10);
    internal static readonly OrganizationAddressTarget Warehouse = new(ScopeNodeKind.Warehouse, 102);
    internal static readonly GrantScope BranchScope = new(GrantScopeKind.BuyingBranch, 10, BranchId: 11);
    internal static readonly GrantScope WarehouseScope = new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
    internal static readonly ResourceScope BranchResource = new(BuyingOrganizationId: 10, BranchId: 11);
    internal static PostalAddress Postal => new("Jalan Tebet Raya 12", "Lantai 2", "Jakarta Selatan", "DKI Jakarta", "12810", "ID");
    internal OrganizationAddressInput Input(long version = 0, OrganizationAddressTarget? target = null, OrganizationAddressPurpose purpose = OrganizationAddressPurpose.Delivery) =>
        new(target ?? Branch, purpose, 2, 1, version, Postal);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic independently reviewed postal facts; no contact or bank authority", "organization-address-test");
    internal IMongoCollection<BsonDocument> Rows(string table) => Database.GetCollection<BsonDocument>(table);
    internal static async Task<OrganizationAddressTestData> CreateAsync(MongoFixture fixture) {
        var data = await StoreTestData.CreateAsync(fixture);
        try {
            return await PrepareAsync(data, fixture.Client);
        } catch { await data.DisposeAsync(); throw; }
    }
    // The caller retains ownership of its existing isolated database. Use the same real
    // organization and reviewed-person producers for integrated browser rehearsals.
    internal static async Task<OrganizationAddressTestData> PrepareAsync(StoreTestData source, MongoClient client) {
        var data = new OrganizationAddressTestData(source, client);
        var system = new GrantScope(GrantScopeKind.System);
        data.ProfileMaker = await data.Base.AddActorAsync(1, system, Permissions.TechnicalConfigure);
        data.ProfileChecker = await data.Base.AddActorAsync(2, system, Permissions.OrganizationApprove);
        foreach (var (id, kind, parent, code, name) in new (long, ScopeNodeKind, long?, string, string)[] {
            (10, ScopeNodeKind.BuyingOrganization, null, "BUYER", "Synthetic buying organization"),
            (11, ScopeNodeKind.Branch, 10, "TEBET", "Synthetic Tebet branch"),
            (100, ScopeNodeKind.FulfillmentOrganization, null, "TOKO", "Synthetic cooperative"),
            (101, ScopeNodeKind.Shop, 100, "SHOP", "Synthetic fulfillment shop"),
            (102, ScopeNodeKind.Warehouse, 101, "WAREHOUSE", "Synthetic dispatch warehouse") }) {
            var proposed = await data.Organizations.ProposeAsync(data.ProfileMaker,
                new(OrganizationChangeKind.UpdateProfile, kind, parent, code, name, id, 1, 0), Meta("postal-profile-" + id));
            await data.Organizations.ApproveAsync(data.ProfileChecker, proposed.Id, 1, Meta("postal-profile-" + id + "-approve"));
        }
        data.Maker = await data.Base.AddActorAsync(301, system, Permissions.TechnicalConfigure);
        data.Checker = await data.Base.AddActorAsync(302, system, Permissions.OrganizationApprove);
        data.Reader = await data.Base.AddActorAsync(303, BranchScope, Permissions.OrganizationAddressRead, Permissions.BranchPic);
        data.WarehouseReader = await data.Base.AddActorAsync(304, WarehouseScope, Permissions.OrganizationAddressRead);
        data.Orderer = await data.Base.AddActorAsync(305, BranchScope, Permissions.OrderSubmit, Permissions.BranchPic);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock,
            data.Maker.UserId, data.Checker.UserId, data.Reader.UserId, data.WarehouseReader.UserId, data.Orderer.UserId);
        return data;
    }
    internal async Task<BsonDocument> ProposeAsync(string key, OrganizationAddressInput? input = null) {
        input ??= Input(); var preview = await Service.PreviewAsync(Maker, input);
        return await Service.ProposeAsync(Maker, input, preview.Binding, Meta(key));
    }
    internal async Task<BsonDocument> DecideAsync(BsonDocument proposal, string key, OrganizationAddressDecision decision = OrganizationAddressDecision.Approve) {
        var preview = await Service.PreviewDecisionAsync(Checker, proposal["proposalId"].AsInt64, 1, decision);
        return await Service.DecideAsync(Checker, proposal["proposalId"].AsInt64, 1, decision, preview.Binding, Meta(key));
    }
    internal async Task<BsonDocument> StateAsync() {
        var result = new BsonDocument();
        foreach (var table in new[] { B.Heads, B.Proposals, B.Decisions, B.Versions }) {
            var rows = await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); result[table] = new BsonArray(rows);
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            result[table] = new BsonArray(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^organization\.address(?:[.:]|$)")))
                .Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    public ValueTask DisposeAsync() => Base.DisposeAsync();
}
