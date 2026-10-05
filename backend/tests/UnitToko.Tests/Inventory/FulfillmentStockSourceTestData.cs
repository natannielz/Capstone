using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

/// <summary>Genuine independent setup and grants in an isolated synthetic database, with no
/// activation, fake head, reconstructed approval or production operator acceptance.</summary>
internal sealed class FulfillmentStockSourceTestData(StoreTestData data, MongoFixture fixture) : IAsyncDisposable
{
    internal static readonly StockLocation Location = new(new(100), new(101), new(102), new(103));
    internal StoreTestData Data => data;
    internal MongoUnitOfWork Unit => new(fixture.Client);
    internal StockSourceSetupService Service => new(data.Database, Unit, new(data.Database), data.Clock);
    internal ActorContext Maker { get; private set; } = null!;
    internal ActorContext Checker { get; private set; } = null!;
    internal BsonDocument Proposal { get; private set; } = null!;
    internal BsonDocument? Approval { get; private set; }
    internal long PoolId => (Approval ?? throw new InvalidOperationException("No genuine approval"))["poolId"].AsInt64;
    internal IMongoCollection<BsonDocument> Rows(string name) => data.Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic local Fulfillment source proof", "m92-fulfillment-tests");
    internal static async Task<FulfillmentStockSourceTestData> CreateAsync(MongoFixture fixture, bool approve = true)
    {
        var data = await StoreTestData.CreateAsync(fixture); var value = new FulfillmentStockSourceTestData(data, fixture);
        try {
            Assert.Equal(94, CollectionMigrations.CurrentVersion);
            Assert.True(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
            value.Maker = await data.AddActorAsync(920100, Location.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
            value.Checker = await data.AddActorAsync(920200, Location.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, value.Maker.UserId, value.Checker.UserId);
            value.Proposal = await value.Service.ProposeAsync(value.Maker, new(Location, "m92-isolated-custody-pool", "Synthetic custody source",
                StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60000, 0, null, null,
                "Synthetic discrepancy owner", "Synthetic source handoff"), Meta("m92-source-propose"));
            Assert.Equal(0L, await value.Rows(FulfillmentStockSourceRecords.Heads).CountDocumentsAsync(new BsonDocument()));
            if (approve) value.Approval = await value.Service.DecideAsync(value.Checker, value.Proposal["proposalId"].AsInt64, 1,
                StockSourceSetupDecision.Approve, "Synthetic independent custody source review", Meta("m92-source-approve"));
            return value;
        } catch { await value.DisposeAsync(); throw; }
    }
    internal Task<BsonDocument> PoolAsync() => Rows("inventoryPools").Find(new BsonDocument("_id", PoolId)).SingleAsync();
    internal Task<BsonDocument> HeadAsync() => Rows(FulfillmentStockSourceRecords.Heads).Find(new BsonDocument("_id", PoolId)).SingleAsync();
    internal async Task<StockSourceObligationResult> ReadAsync()
    {
        var pool = await PoolAsync(); var expected = new StockSourceObligationExpectation(PoolId, Location, pool["revision"].AsInt64, pool["fence"].AsInt64);
        return await Unit.ExecuteAsync((tx, ct) => new FulfillmentStockSourceObligationReader(data.Database, Unit, new(data.Database), data.Clock)
            .ReadAndFenceAsync(tx, Checker, expected, ct));
    }
    public ValueTask DisposeAsync() => data.DisposeAsync();
}
