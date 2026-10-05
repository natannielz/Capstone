using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

/// <summary>Real Mongo/workflow with isolated synthetic reviewed people and accounts.
/// Every producer head comes from the actual independent setup command; no active pool,
/// production approval, commercial admission, positive drain or stock release is asserted.</summary>
internal sealed class OrderStockSourceTestData(StoreTestData data, MongoFixture fixture) : IAsyncDisposable
{
    internal static readonly StockLocation Location = new(new(100), new(101), new(102), new(103));
    internal StoreTestData Data => data;
    internal MongoUnitOfWork Unit => new(fixture.Client);
    internal StockSourceSetupService Service => new(data.Database, Unit, new(data.Database), data.Clock);
    internal ActorContext Maker { get; private set; } = null!;
    internal ActorContext Reviewer { get; private set; } = null!;
    internal ActorContext Operator { get; private set; } = null!;
    internal BsonDocument Proposed { get; private set; } = null!;
    internal BsonDocument? Approved { get; private set; }
    internal long PoolId => (Approved ?? throw new InvalidOperationException("No genuine approval"))["poolId"].AsInt64;
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Isolated Ordering producer verification", "m90-source-tests");
    internal static StockSourceSetupInput Input() => new(Location, "m90-reviewed-synthetic-pool", "Isolated reviewed source",
        StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60000, 0, null, null,
        "isolated-discrepancy-owner", "isolated-reviewed-handoff");
    internal IMongoCollection<BsonDocument> Rows(string name) => data.Database.GetCollection<BsonDocument>(name);
    internal OrderStockSourceObligationReader OrderReader => new(data.Database, Unit, new(data.Database), data.Clock);
    internal ReservationStockSourceObligationReader ReservationReader => new(data.Database, Unit, new(data.Database), data.Clock);
    internal static async Task<OrderStockSourceTestData> CreateAsync(MongoFixture fixture, bool approve = true)
    {
        var data = await StoreTestData.CreateAsync(fixture);
        var value = new OrderStockSourceTestData(data, fixture);
        try {
            Assert.Equal(91, CollectionMigrations.CurrentVersion);
            Assert.True(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
            value.Maker = await data.AddActorAsync(1000, Location.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
            value.Reviewer = await data.AddActorAsync(2000, Location.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead);
            value.Operator = await data.AddActorAsync(3000, Location.ToScope(), Permissions.StockSourceSuspend, Permissions.StockSourceRead);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, value.Maker.UserId, value.Reviewer.UserId);
            value.Proposed = await value.Service.ProposeAsync(value.Maker, Input(), Meta("m90-propose"));
            Assert.Equal(0, await value.Rows(OrderStockSourceRecords.Heads).CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(0, await value.Rows(OrderStockSourceRecords.Transitions).CountDocumentsAsync(new BsonDocument()));
            if (approve) value.Approved = await value.ApproveAsync();
            return value;
        } catch { await value.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> ApproveAsync()
    {
        var result = await Service.DecideAsync(Reviewer, Proposed["proposalId"].AsInt64, 1,
            StockSourceSetupDecision.Approve, "isolated-independent-review", Meta("m90-approve"));
        Approved = result; return result;
    }
    internal Task<BsonDocument> PoolAsync() => Rows("inventoryPools").Find(new BsonDocument("_id", PoolId)).SingleAsync();
    internal Task<BsonDocument> HeadAsync() => Rows(OrderStockSourceRecords.Heads).Find(new BsonDocument("_id", PoolId)).SingleAsync();
    internal async Task<StockSourceObligationExpectation> ExpectedAsync()
    {
        var pool = await PoolAsync();
        Assert.False(pool["active"].AsBoolean);
        return new(PoolId, InventoryGraph.Location(pool["location"].AsBsonDocument), pool["revision"].AsInt64, pool["fence"].AsInt64);
    }
    internal async Task<StockSourceObligationResult> ReadAsync(bool reservations = false, ActorContext? actor = null,
        StockSourceObligationExpectation? expected = null)
    {
        var target = expected ?? await ExpectedAsync();
        return await Unit.ExecuteAsync(async (tx, ct) =>
            reservations ? await ReservationReader.ReadAndFenceAsync(tx, actor ?? Reviewer, target, ct) :
                await OrderReader.ReadAndFenceAsync(tx, actor ?? Reviewer, target, ct));
    }
    internal async Task<BsonDocument> EffectsAsync()
    {
        var value = new BsonDocument();
        foreach (var name in new[] { "stockBalances", "movements", "lots", "journalEntries",
            "orders", "orderSuborders", "reservations", "orderDecisions", "shipments", "paymentSubmissions" })
            value.Add(name, new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return value;
    }
    internal async Task<BsonDocument> SourceStateAsync()
    {
        var value = new BsonDocument();
        foreach (var name in new[] { "stockSourceProposals", "stockSourceDecisions", "inventoryPoolRegistrations", "inventoryPools",
            "stockSourceAliasProposals", "stockSourceAliasDecisions", "stockSourceAliases",
            OrderStockSourceRecords.Heads, OrderStockSourceRecords.Transitions, "auditEvents", "idempotencyRecords" })
            value.Add(name, new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return value;
    }
    public ValueTask DisposeAsync() => data.DisposeAsync();
}
