using System.Collections.Immutable;
using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Catalog;

internal sealed class CatalogAuthoringTestData : IAsyncDisposable
{
    internal static readonly GrantScope Shop = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
    internal static readonly GrantScope Warehouse = new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
    internal static readonly GrantScope SecondWarehouse = new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 104, ChannelId: 103);
    internal static readonly GrantScope OtherOwner = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 201, ChannelId: 203);
    internal static readonly GrantScope Audience = new(GrantScopeKind.BuyerOwned, 10, 11);
    internal static readonly AuthorizationReadMetadata Audit = new("catalog-authoring", "synthetic-catalog-read");
    internal static readonly AuthorizationReadMetadata RecoveryAudit = new("operation-recovery", "synthetic-catalog-recovery");
    internal StoreTestData Base { get; }
    internal IMongoDatabase Database => Base.Database;
    internal MutableAuthorizationClock Clock => Base.Clock;
    internal MongoClient Client { get; }
    internal CatalogAuthoringService Service { get; }
    internal CatalogDefinitionQueryService BuyerService { get; }
    internal ActorContext Maker { get; private set; } = null!;
    internal ActorContext WarehouseActor { get; private set; } = null!;
    internal ActorContext Other { get; private set; } = null!;
    internal ActorContext Buyer { get; private set; } = null!;
    internal MongoIdentitySessionStore Sessions { get; }
    internal Dictionary<long, SessionIssue> Issued { get; } = [];
    private readonly SessionPayloadProtector protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
    private static readonly SessionPolicy Policy = new("https://approved.example/", "fixture-connection", "fixture-policy");
    internal CatalogAuthoringTestData(StoreTestData data, MongoClient client)
    {
        Base = data; Client = client;
        Sessions = new(Database, new(client), Clock, Policy, protector);
        Service = NewService(); BuyerService = NewBuyer();
    }
    internal CatalogAuthoringService NewService(MongoClient? client = null)
    {
        var selected = client ?? Client; var db = selected.GetDatabase(Database.DatabaseNamespace.DatabaseName);
        return new(db, new(selected), new(db), Clock);
    }
    internal CatalogDefinitionQueryService NewBuyer(MongoClient? client = null)
    {
        var selected = client ?? Client; var db = selected.GetDatabase(Database.DatabaseNamespace.DatabaseName);
        return new(db, new(db, new(selected), Clock), Clock);
    }
    internal static async Task<CatalogAuthoringTestData> CreateAsync(MongoFixture fixture, bool beforePriceMigration = false)
    {
        var data = new CatalogAuthoringTestData(beforePriceMigration
            ? await StoreTestData.CreateForCatalogPriceMigrationAsync(fixture)
            : await StoreTestData.CreateAsync(fixture), fixture.Client);
        data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var node in new[] { new ScopeNode(104, ScopeNodeKind.Warehouse, 101, AuthorizationStatus.Active, 1),
            new(201, ScopeNodeKind.Shop, 100, AuthorizationStatus.Active, 1),
            new(202, ScopeNodeKind.Warehouse, 201, AuthorizationStatus.Active, 1),
            new(203, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1) })
            await data.Rows("authorizationScopes").InsertOneAsync(AuthorizationBson.Node(node));
        data.Maker = await data.ActorAsync(3000, Shop, Permissions.CatalogManage, Permissions.CatalogRead);
        data.WarehouseActor = await data.ActorAsync(4000, Warehouse, Permissions.CatalogManage);
        data.Other = await data.ActorAsync(5000, OtherOwner, Permissions.CatalogManage, Permissions.CatalogRead);
        data.Buyer = await data.ActorAsync(6000, Audience, Permissions.CatalogRead);
        return data;
    }
    internal async Task<ActorContext> ActorAsync(long id, GrantScope owner, params string[] permissions)
    {
        var actor = await Base.AddActorAsync(id, owner, permissions);
        await Rows("identities").UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "catalog-fixture@example.invalid",
            true, Policy.TrustedConnectionId, Clock.UtcNow, Clock.UtcNow, "otp", Policy.AssurancePolicyVersion);
        var issued = await new MongoUnitOfWork(Client).ExecuteAsync((tx, ct) => Sessions.CreateAsync(tx,
            new(id, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-catalog-provider", ct));
        Issued[id] = issued;
        return actor with { Session = issued.Session.Reference };
    }
    internal static AuthorizationCommandMetadata Metadata(string key, string reason = "Synthetic catalog review") =>
        new(key, reason, "synthetic-catalog-command");
    internal static DefinitionInput Definition(int contents = 12, string label = "Dus") =>
        new(label, "sealed", "piece", contents, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal), [], 1, 1);
    internal Task<CatalogMutationResult> CategoryAsync(ActorContext? actor = null, GrantScope? owner = null, string code = "ATK") =>
        Service.SaveCategoryAsync(actor ?? Maker, new(owner ?? Shop, null, 0, code, "Alat tulis"), Metadata("category-" + code));
    internal async Task<CatalogMutationResult> FamilyAsync(ActorContext? actor = null, GrantScope? owner = null, string code = "ATK")
    {
        var category = await CategoryAsync(actor, owner, code);
        return await Service.CreateFamilyAsync(actor ?? Maker, new(owner ?? Shop, category.Id, "Pensil", "Deskripsi", []), Metadata("family-" + code));
    }
    internal async Task<CatalogMutationResult> SkuAsync(int contents = 12, string code = "PENCIL-12")
    {
        var family = await FamilyAsync();
        return await Service.CreateSkuAsync(Maker, new(family.Id, family.Version, code, "pencil-base", Definition(contents)), Metadata("sku-" + code));
    }
    internal async Task AssortmentsAsync()
    {
        foreach (var item in new[] { new CatalogAssortment(501, Shop, AuthorizationStatus.Active, 1),
            new CatalogAssortment(502, Shop, AuthorizationStatus.Active, 1), new CatalogAssortment(503, OtherOwner, AuthorizationStatus.Active, 1) })
            await Rows("catalogAssortments").InsertOneAsync(CatalogVisibilityBson.Assortment(item));
        await Rows("catalogEntitlements").InsertOneAsync(CatalogVisibilityBson.Entitlement(new CatalogEntitlement(10001, Buyer.UserId, Buyer.UserId,
            501, Audience, AuthorizationStatus.Active, Clock.UtcNow, null, null, 1, 998, 999, 90001, 1, Clock.UtcNow)));
    }
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal Task<BsonDocument> RowAsync(string name, long id) => Rows(name).Find(new BsonDocument("_id", id)).SingleAsync();
    internal Task<long> CountAsync(string name) => Rows(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
    internal async Task<BsonDocument> StateAsync()
    {
        var state = new BsonDocument();
        foreach (var name in new[] { "catalogCategories", "productFamilies", "skus", "variantDefinitions", "catalogIdentifiers",
            "catalogIdentifierHistory", "catalogAssortments", "auditEvents", "idempotencyRecords", "outboxEvents",
            "identities", "identitySessions", "authorizationGrants", "memberships", "authorizationScopes", "authorizationReadEvents" })
            state[name] = new BsonArray(await Rows(name).Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
    public ValueTask DisposeAsync() => Base.DisposeAsync();
}
