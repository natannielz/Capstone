using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

internal sealed class StoreTestData : IAsyncDisposable
{
    private readonly MongoClient client;
    public IMongoDatabase Database { get; }
    public MutableAuthorizationClock Clock { get; } = new(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));
    public AuthorizationStore Store { get; }
    public AccessAdministrationService Administration { get; }
    private StoreTestData(MongoFixture fixture)
    {
        client = fixture.Client;
        Database = client.GetDatabase(fixture.DatabaseName + "_" + Guid.NewGuid().ToString("N")[..8]);
        Store = new(Database, new(client), Clock);
        Administration = new(Database, Store, new(Database), Clock);
    }
    public static Task<StoreTestData> CreateAsync(MongoFixture fixture) => CreateAsync(fixture, false);
    internal static Task<StoreTestData> CreateForLegacyCatalogAsync(MongoFixture fixture) => CreateAsync(fixture, true);
    internal static Task<StoreTestData> CreateForStockSourceSetupMigrationAsync(MongoFixture fixture) => CreateAsync(fixture, false, true);
    internal static Task<StoreTestData> CreateForStockSourceRevisionMigrationAsync(MongoFixture fixture) => CreateAsync(fixture, false, false, true);
    internal static Task<StoreTestData> CreateForStockSourceAliasMigrationAsync(MongoFixture fixture) => CreateAsync(fixture, false, false, false, true);
    internal static Task<StoreTestData> CreateForStockSourceSuspensionMigrationAsync(MongoFixture fixture) => CreateAsync(fixture, false, false, false, false, true);
    internal static Task<StoreTestData> CreateForStockSourcePolicyMigrationAsync(MongoFixture fixture) => CreateAsync(fixture, false, sourcePolicyMigration: true);
    internal static Task<StoreTestData> CreateForCatalogPriceMigrationAsync(MongoFixture fixture) => CreateAsync(fixture, false, catalogPriceMigration: true);
    internal static Task<StoreTestData> CreateForLegacyOrderReadsAsync(MongoFixture fixture) => CreateAsync(fixture, false, legacyOrderReads: true);
    private static async Task<StoreTestData> CreateAsync(MongoFixture fixture, bool legacyCatalog, bool sourceSetupMigration = false, bool sourceRevisionMigration = false, bool sourceAliasMigration = false, bool sourceSuspensionMigration = false, bool sourcePolicyMigration = false, bool catalogPriceMigration = false, bool legacyOrderReads = false)
    {
        var data = new StoreTestData(fixture);
        if (legacyOrderReads) await LegacyOrderSubmissionTestSchema.ApplyV21Async(data.Database, data.Clock);
        else if (catalogPriceMigration) await LegacyCatalogPriceTestSchema.ApplyV19Async(data.Database, data.Clock);
        else if (sourcePolicyMigration) await LegacyStockSourcePolicyTestSchema.ApplyV18Async(data.Database, data.Clock);
        else if (sourceSuspensionMigration) await LegacyStockSourceSuspensionTestSchema.ApplyV17Async(data.Database, data.Clock);
        else if (sourceAliasMigration) await LegacyStockSourceAliasTestSchema.ApplyV16Async(data.Database, data.Clock);
        else if (sourceRevisionMigration) await LegacyStockSourceRevisionTestSchema.ApplyV15Async(data.Database, data.Clock);
        else if (sourceSetupMigration) await LegacyStockSourceSetupTestSchema.ApplyV14Async(data.Database, data.Clock);
        else if (legacyCatalog) await LegacyCatalogTestSchema.ApplyV6Async(data.Database, data.Clock);
        else await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        foreach (var node in new[]
        {
            new ScopeNode(10, ScopeNodeKind.BuyingOrganization, null, AuthorizationStatus.Active, 1),
            new(11, ScopeNodeKind.Branch, 10, AuthorizationStatus.Active, 1),
            new(12, ScopeNodeKind.Branch, 10, AuthorizationStatus.Active, 1),
            new(20, ScopeNodeKind.BuyingOrganization, null, AuthorizationStatus.Active, 1),
            new(21, ScopeNodeKind.Branch, 20, AuthorizationStatus.Active, 1),
            new(100, ScopeNodeKind.FulfillmentOrganization, null, AuthorizationStatus.Active, 1),
            new(101, ScopeNodeKind.Shop, 100, AuthorizationStatus.Active, 1),
            new(102, ScopeNodeKind.Warehouse, 101, AuthorizationStatus.Active, 1),
            new(103, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)
        }) await data.Database.GetCollection<BsonDocument>("authorizationScopes").InsertOneAsync(AuthorizationBson.Node(node));
        return data;
    }
    public async Task<ActorContext> AddActorAsync(long id, GrantScope scope, params string[] permissions)
    {
        var actor = new ActorContext(id, "https://approved.example/", "subject-" + id, 1, Clock.UtcNow, FactorAssurance.MultiFactor);
        await Database.GetCollection<BsonDocument>("identities").InsertOneAsync(new BsonDocument
        {
            { "_id", id }, { "schemaVersion", 1 }, { "issuer", actor.ProviderIssuer }, { "subject", actor.ProviderSubject },
            { "status", "active" }, { "grantVersion", 1L }, { "fence", 0L }
        });
        await AddGrantAsync(actor, id, scope, permissions);
        return actor;
    }
    public async Task AddGrantAsync(ActorContext actor, long grantId, GrantScope scope, string[] permissions,
        string[]? delegable = null)
    {
        var membership = new Membership(grantId, actor.UserId, scope, AuthorizationStatus.Active, 999, Clock.UtcNow);
        await Database.GetCollection<BsonDocument>("memberships").InsertOneAsync(AuthorizationBson.Membership(membership));
        var grant = new PermissionGrant(grantId, actor.UserId, grantId, "buyer", 1,
            permissions.ToImmutableArray(), scope, AuthorizationStatus.Active, 998, 999, "Synthetic reviewed assignment", Clock.UtcNow,
            DelegablePermissions: (delegable ?? []).ToImmutableArray());
        await Database.GetCollection<BsonDocument>("authorizationGrants").InsertOneAsync(AuthorizationBson.Grant(grant));
    }
    public Task AddResourceAsync(long id, long? organizationId, long? branchId, long createdBy, long[]? assigned = null) =>
        Database.GetCollection<BsonDocument>("fixtureBusinessRecords").InsertOneAsync(new BsonDocument
        {
            { "_id", id }, { "schemaVersion", 1 }, { "version", 1L },
            { "buyingOrganizationId", organizationId.HasValue ? new BsonInt64(organizationId.Value) : BsonNull.Value },
            { "branchId", branchId.HasValue ? new BsonInt64(branchId.Value) : BsonNull.Value },
            { "createdBy", createdBy }, { "assignedUserIds", new BsonArray((assigned ?? []).Select(value => new BsonInt64(value))) }
        });
    public async ValueTask DisposeAsync() => await client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName);
}

internal sealed class MutableAuthorizationClock(DateTimeOffset now) : IClock
{
    private DateTimeOffset value = now;
    internal bool UseSystemClock { get; set; }
    public DateTimeOffset UtcNow { get => UseSystemClock ? DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) : value; set => this.value = value; }
}
