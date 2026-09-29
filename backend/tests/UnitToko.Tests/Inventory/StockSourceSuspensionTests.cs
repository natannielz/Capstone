using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed partial class StockSourceSuspensionTests(MongoFixture fixture)
{
    private static readonly StockLocation Owner = new(new(100), new(101), new(102), new(103));
    private static readonly StockLocation Linked = Owner with { ChannelId = new(104) };
    private const string SuspendPermission = "stock.source.suspend";
    private static StockSourceSetupInput Input() => new(Owner, "synthetic-pool", "Synthetic pool",
        StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60001, 9007199254740993,
        null, null, "synthetic-owner", "synthetic-handoff");
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic source suspension", "suspension-tests");
    private StockSourceSetupService Service(StoreTestData data) => new(data.Database, new(fixture.Client), new(data.Database), data.Clock);
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private static Task<long> Count(StoreTestData data, string name) => Rows(data, name).CountDocumentsAsync(new BsonDocument());
    private static Task<BsonDocument> Pool(StoreTestData data, long pool) => Rows(data, "inventoryPools").Find(new BsonDocument("_id", pool)).SingleAsync();
    private static StockSourceSuspensionExpectation Expected(BsonDocument preview) =>
        new(preview["poolRevision"].AsInt64, preview["suspensionRevision"].AsInt64, preview["configurationHash"].AsString);
    private async Task<(ActorContext Maker, ActorContext Reviewer, ActorContext Operator, long Pool)> Setup(StoreTestData data, bool linked = false)
    {
        // Registered migration 18 supplies enforced suspension storage and lookup indexes.
        var maker = await data.AddActorAsync(1000, Owner.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
        var reviewer = await data.AddActorAsync(2000, Owner.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead);
        var operatorActor = await data.AddActorAsync(3000, Owner.ToScope(), SuspendPermission, Permissions.StockSourceRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId);
        var service = Service(data);
        var proposed = await service.ProposeAsync(maker, Input(), Meta("source-propose"));
        var approved = await service.DecideAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("source-approve"));
        var pool = approved["poolId"].AsInt64;
        if (linked)
        {
            await Rows(data, "authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)));
            await data.AddGrantAsync(maker, 1001, Linked.ToScope(), [Permissions.StockSourcePropose, Permissions.StockSourceRead]);
            await data.AddGrantAsync(reviewer, 2001, Linked.ToScope(), [Permissions.StockSourceApprove, Permissions.StockSourceRead]);
            await data.AddGrantAsync(operatorActor, 3001, Linked.ToScope(), [SuspendPermission, Permissions.StockSourceRead]);
            var alias = await service.ProposeAliasAsync(maker, pool, 1, Linked, 0, true, Meta("mapping-propose"));
            await service.DecideAliasAsync(reviewer, alias["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-mapping-review", Meta("mapping-approve"));
        }
        return (maker, reviewer, operatorActor, pool);
    }

    [Fact]
    public void Suspension_is_an_explicit_fresh_operational_capability_without_automatic_role_rights()
    {
        var permission = Permissions.Find(SuspendPermission);
        Assert.NotNull(permission);
        Assert.Equal(TimeSpan.FromMinutes(5), permission.Assurance.MaximumAge);
        Assert.Equal(new[] { GrantScopeKind.FulfillmentShop, GrantScopeKind.FulfillmentWarehouse }, permission.AllowedScopes.ToArray());
        foreach (var role in RoleTemplates.Templates.Values) Assert.DoesNotContain(SuspendPermission, role.Permissions);
        Assert.False(ApprovalPolicy.RequiresEvidence(SuspendPermission));
        Assert.False(PersonApprovalPolicy.RequiresEvidence(SuspendPermission));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Suspension_atomically_stops_one_pool_without_changing_config_or_stock_and_replays_once(bool active)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var (_, _, actor, pool) = await Setup(data, true); var service = Service(data);
        // A synthetic active flag exercises suspension; it is deliberately not handoff/activation evidence.
        await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument("_id", pool), Builders<BsonDocument>.Update.Set("active", active));
        await InventoryGraphTests.InstallBalances(data);
        await Rows(data, "lots").InsertOneAsync(InventoryGraphTests.Lot());
        var stock = InventoryBson.Balance(new InventoryBalanceDocument(7000, pool,
            new(new(Owner, new(200), new(201), new(202)), 9007199254740993, 2, 3, 4, 1)));
        await Rows(data, "stockBalances").InsertOneAsync(stock);
        var before = await Pool(data, pool); var audits = await Count(data, "auditEvents"); var receipts = await Count(data, "idempotencyRecords");
        var preview = await service.PreviewSuspensionAsync(actor, pool);
        Assert.Equal(2, preview["affectedLocations"].AsBsonArray.Count);
        Assert.DoesNotContain("input", preview.Names);
        var expected = Expected(preview);
        var result = await service.SuspendAsync(actor, pool, expected, Meta("suspend"));
        Assert.Equal("suspended", result["status"].AsString);
        Assert.Equal(result, await service.SuspendAsync(actor, pool, expected, Meta("suspend")));
        var after = await Pool(data, pool);
        Assert.False(after["active"].AsBoolean);
        Assert.Equal(before["revision"], after["revision"]);
        Assert.Equal(before["fence"].AsInt64 + 1, after["fence"].AsInt64);
        Assert.Equal(stock, await Rows(data, "stockBalances").Find(new BsonDocument("_id", 7000L)).SingleAsync());
        Assert.Equal(1L, await Count(data, StockSourceSuspensionSchemas.Collection));
        Assert.Equal(audits + 1, await Count(data, "auditEvents"));
        Assert.Equal(receipts + 1, await Count(data, "idempotencyRecords"));
        Assert.Equal(1L, await Count(data, "stockSourceAliases"));
        Assert.Equal(0L, await Count(data, "movements")); Assert.Equal(0L, await Count(data, "journalEntries"));
        var latest = await service.ReadSuspensionAsync(actor, pool);
        Assert.Equal(result["suspensionId"], latest!["_id"]);
        Assert.Equal(active, latest["configuration"]["source"]["active"].AsBoolean);
        await Assert.ThrowsAsync<PersistenceConflictException>(async () => await service.SuspendAsync(actor, pool,
            Expected(await service.PreviewSuspensionAsync(actor, pool)), Meta("second-stop")));
    }
}
