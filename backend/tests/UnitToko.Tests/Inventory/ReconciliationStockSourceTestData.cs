using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

/// <summary>Actual setup, independent policy approval and reconciliation commands in an isolated
/// Mongo database. People and their reviewed grants are synthetic local fixtures. No active pool,
/// stock release, seven-domain coverage or production operator approval is asserted.</summary>
internal sealed class ReconciliationStockSourceTestData(StoreTestData data, MongoFixture fixture) : IAsyncDisposable
{
    internal static readonly StockLocation Location = new(new(100), new(101), new(102), new(103));
    internal StoreTestData Data => data;
    internal IMongoDatabase Database => data.Database;
    internal MongoUnitOfWork Unit => new(fixture.Client);
    internal MutableAuthorizationClock Clock => data.Clock;
    internal StockSourceSetupService Service => new(Database, Unit, new(Database), Clock);
    internal ActorContext Maker { get; private set; } = null!;
    internal ActorContext Checker { get; private set; } = null!;
    internal ActorContext Reader { get; private set; } = null!;
    internal ActorContext Outsider { get; private set; } = null!;
    internal BsonDocument Setup { get; private set; } = null!;
    internal BsonDocument Approval { get; private set; } = null!;
    internal long PoolId => Approval["poolId"].AsInt64;
    internal static AuthorizationCommandMetadata Meta(string key) => new(key,
        "Synthetic local reconciliation producer verification; no stock or money action", "m91-reconciliation-tests");
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);

    internal static async Task<ReconciliationStockSourceTestData> CreateAsync(MongoFixture fixture)
    {
        var data = await StoreTestData.CreateAsync(fixture);
        var value = new ReconciliationStockSourceTestData(data, fixture);
        try {
            Assert.Equal(93, CollectionMigrations.CurrentVersion);
            Assert.True(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
            value.Maker = await data.AddActorAsync(910100, Location.ToScope(), Permissions.StockSourceRead,
                Permissions.StockSourcePropose, Permissions.StockSourcePolicyPropose,
                Permissions.StockSourceReconciliationRead, Permissions.StockSourceReconciliationPropose);
            value.Checker = await data.AddActorAsync(910200, Location.ToScope(), Permissions.StockSourceRead,
                Permissions.StockSourceApprove, Permissions.StockSourcePolicyApprove,
                Permissions.StockSourceReconciliationRead, Permissions.StockSourceReconciliationDecide);
            value.Reader = await data.AddActorAsync(910300, Location.ToScope(), Permissions.StockSourceRead,
                Permissions.StockSourceReconciliationRead);
            await data.Database.GetCollection<BsonDocument>("authorizationScopes").InsertOneAsync(AuthorizationBson.Node(
                new ScopeNode(104, ScopeNodeKind.Warehouse, 101, AuthorizationStatus.Active, 1)));
            value.Outsider = await data.AddActorAsync(910400, (Location with { WarehouseId = new(104) }).ToScope(),
                Permissions.StockSourceRead, Permissions.StockSourceReconciliationRead,
                Permissions.StockSourceReconciliationPropose, Permissions.StockSourceReconciliationDecide);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, value.Maker.UserId,
                value.Checker.UserId, value.Reader.UserId, value.Outsider.UserId);
            value.Setup = await value.Service.ProposeAsync(value.Maker,
                new(Location, "m91-reviewed-empty-local-pool", "Synthetic reviewed reconciliation pool",
                    StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60001, 0, null, null,
                    "Synthetic discrepancy operator", "Synthetic reviewed handoff"), Meta("m91-source-propose"));
            Assert.Equal(0L, await value.Rows(ReconciliationStockSourceRecords.Heads).CountDocumentsAsync(new BsonDocument()));
            value.Approval = await value.Service.DecideAsync(value.Checker, value.Setup["proposalId"].AsInt64, 1,
                StockSourceSetupDecision.Approve, "Synthetic independent source review", Meta("m91-source-approve"));
            var preview = await value.Service.PreviewOperatingPolicyAsync(value.Maker, value.PoolId);
            var expected = new StockSourcePolicyExpectation(preview["poolRevision"].AsInt64,
                preview["suspensionRevision"].AsInt64, preview["policyRevision"].AsInt64, preview["configurationHash"].AsString);
            var policy = await value.Service.ProposeOperatingPolicyAsync(value.Maker, value.PoolId, expected,
                new(StockMovementWriter.UnitToko, null, 0, "Synthetic complete empty-pool observation",
                    "Synthetic stopped-pool review procedure"), Meta("m91-policy-propose"));
            await value.Service.DecideOperatingPolicyAsync(value.Checker, policy["proposalId"].AsInt64, 1,
                StockSourceSetupDecision.Approve, "Synthetic independent policy review", Meta("m91-policy-approve"));
            Assert.False((await value.PoolAsync())["active"].AsBoolean);
            return value;
        } catch { await value.DisposeAsync(); throw; }
    }

    internal Task<BsonDocument> PoolAsync() => Rows("inventoryPools").Find(new BsonDocument("_id", PoolId)).SingleAsync();
    internal Task<BsonDocument> HeadAsync() => Rows(ReconciliationStockSourceRecords.Heads).Find(new BsonDocument("_id", PoolId)).SingleAsync();
    internal async Task<(StockSourceReconciliationInput Input, string Binding)> InputAsync(string reference)
    {
        var baseline = await Service.ReadReconciliationBaselineAsync(Maker, PoolId);
        Assert.Empty(baseline["balances"].AsBsonArray);
        return (new(PoolId, baseline["poolRevision"].AsInt64, baseline["poolFence"].AsInt64,
            baseline["policyId"].AsInt64, baseline["policyRevision"].AsInt64, baseline["configurationHash"].AsString,
            new(Clock.UtcNow, reference, ImmutableArray<StockSourceObservedBalance>.Empty)), baseline["binding"].AsString);
    }
    internal async Task<StockSourceObligationExpectation> ExpectedAsync()
    {
        var pool = await PoolAsync();
        Assert.False(pool["active"].AsBoolean);
        return new(PoolId, Location, pool["revision"].AsInt64, pool["fence"].AsInt64);
    }
    internal Task<StockSourceObligationResult> ReadAsync(ActorContext? actor = null, StockSourceObligationExpectation? expected = null) =>
        ReadCoreAsync(actor ?? Reader, expected);
    private async Task<StockSourceObligationResult> ReadCoreAsync(ActorContext actor, StockSourceObligationExpectation? expected)
    {
        var target = expected ?? await ExpectedAsync();
        var reader = new ReconciliationStockSourceObligationReader(Database, Unit, new(Database), Clock);
        return await Unit.ExecuteAsync((tx, ct) => reader.ReadAndFenceAsync(tx, actor, target, ct));
    }
    internal async Task<BsonDocument> EffectsAsync()
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "inventoryPools", ReconciliationStockSourceRecords.Heads,
            ReconciliationStockSourceRecords.Transitions, StockSourceReconciliationBson.Proposals,
            StockSourceReconciliationBson.Decisions, "auditEvents", "outboxEvents", "idempotencyRecords" })
            result[name] = new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    internal async Task<BsonDocument> StockAndMoneyAsync()
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "stockBalances", "movements", "lots", "reservations", "orders", "shipments",
            "procurementStockReceipts", "journalEntries", "cashMovements", "paymentSubmissions", "paymentAllocations" })
            result[name] = new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    public ValueTask DisposeAsync() => data.DisposeAsync();
}
