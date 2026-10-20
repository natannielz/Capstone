using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Procurement;

// Genuine synthetic-local setup/request/independent decision commands. No raw active
// pool, source activation, available-lot release or production acceptance is asserted.
[Collection("Mongo")]
public sealed class ProcurementStockSourceCoverageTests(MongoFixture fixture)
{
    [Fact]
    public async Task Genuine_empty_source_pending_demand_independent_rejection_and_duplicate_keys_use_real_durable_membership() {
        await using var data = await ProcurementRequestTestData.CreateAsync(fixture);
        var actors = await ActorsAsync(data); var poolId = await SetupAsync(data, actors.Maker, actors.Checker);
        var physical = await PhysicalAsync(data); var initial = await ReadAsync(data, actors.Checker, poolId);
        Assert.True(initial.Complete); Assert.Equal(0L, initial.OpenCount); Assert.Equal(1L, initial.ProducerRevision);
        var input = data.Input(); var preview = await data.Service.PreviewAsync(data.Maker, input);
        var meta = ProcurementRequestTestData.Meta("coverage-double-request");
        var submitted = await Task.WhenAll(data.Service.SubmitAsync(data.Maker, input, preview["binding"].AsString, meta),
            data.Service.SubmitAsync(data.Maker, input, preview["binding"].AsString, meta));
        Assert.Equal(submitted[0], submitted[1]);
        var request = submitted[0]; var current = await ReadAsync(data, actors.Checker, poolId);
        Assert.True(current.Complete); Assert.Equal(1L, current.OpenCount); Assert.Equal(2L, current.ProducerRevision);
        var head = await data.Rows(ProcurementStockSourceCoverage.Heads).Find(new BsonDocument("_id", poolId)).SingleAsync();
        Assert.Equal(2L, head["revision"].AsInt64);
        var transition = await data.Rows(ProcurementStockSourceCoverage.Transitions).Find(new BsonDocument("sourceKind", "request")).SingleAsync();
        Assert.Equal(request["requestId"], transition["sourceId"]); Assert.Equal(poolId, transition["poolId"].AsInt64);
        Assert.Equal(checked(transition["beforePoolFence"].AsInt64 + 1), transition["afterPoolFence"].AsInt64);
        Assert.Equal(ProcurementStockSourceCoverage.Hash(transition), transition["transitionHash"].AsString);
        var beforeReplay = await CoverageAsync(data);
        Assert.Equal(request, await data.Service.SubmitAsync(data.Maker, input, preview["binding"].AsString, meta));
        Assert.Equal(beforeReplay, await CoverageAsync(data)); Assert.Equal(physical, await PhysicalAsync(data));
        var decision = await data.DecideAsync(request, "coverage-demand-rejected", ProcurementRequestDecision.Reject);
        var closed = await ReadAsync(data, actors.Checker, poolId);
        Assert.True(closed.Complete); Assert.Equal(0L, closed.OpenCount); Assert.Equal(3L, closed.ProducerRevision);
        var beforeDecisionReplay = await CoverageAsync(data);
        var decisionPreview = await data.Service.LookupAsync(data.Checker, ProcurementRequestBson.Prefix + "reject", "coverage-demand-rejected");
        Assert.Equal(decision, decisionPreview); Assert.Equal(beforeDecisionReplay, await CoverageAsync(data));
        Assert.Equal(physical, await PhysicalAsync(data));
        Assert.False((await data.Rows("inventoryPools").Find(new BsonDocument("_id", poolId)).SingleAsync())["active"].AsBoolean);
    }

    [Fact]
    public async Task Missing_real_transition_and_foreign_scope_refuse_without_zero_claim_or_repair() {
        await using var data = await ProcurementRequestTestData.CreateAsync(fixture);
        var actors = await ActorsAsync(data); var poolId = await SetupAsync(data, actors.Maker, actors.Checker);
        await data.SubmitAsync("coverage-missing-member");
        var foreignScope = new GrantScope(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: data.ForeignShopId,
            WarehouseId: data.ForeignWarehouseId, ChannelId: 103);
        var foreign = await data.Base.AddActorAsync(100013, foreignScope, Permissions.StockSourceRead);
        var before = await CoverageAsync(data); var physical = await PhysicalAsync(data);
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => ReadAsync(data, foreign, poolId))).Code);
        Assert.Equal(before, await CoverageAsync(data));
        var row = await data.Rows(ProcurementStockSourceCoverage.Transitions).Find(new BsonDocument("sourceKind", "request")).SingleAsync();
        Assert.Equal(1L, (await data.Rows(ProcurementStockSourceCoverage.Transitions).DeleteOneAsync(row)).DeletedCount);
        var missing = await CoverageAsync(data);
        try {
            Assert.Equal("procurement_stock_source_coverage_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(
                () => ReadAsync(data, actors.Checker, poolId))).Code);
            Assert.Equal(missing, await CoverageAsync(data)); Assert.Equal(physical, await PhysicalAsync(data));
        } finally { await data.Rows(ProcurementStockSourceCoverage.Transitions).InsertOneAsync(row); }
        var valid = await ReadAsync(data, actors.Checker, poolId); Assert.True(valid.Complete); Assert.Equal(1L, valid.OpenCount);
    }

    [Fact]
    public async Task Genuine_old_unbound_demand_remains_preserved_and_blocks_new_source_completeness() {
        await using var data = await ProcurementRequestTestData.CreateAsync(fixture);
        var request = await data.SubmitAsync("coverage-before-source-exists");
        Assert.Empty(await data.Rows(ProcurementStockSourceCoverage.Transitions).Find(new BsonDocument()).ToListAsync());
        var old = await data.StateAsync(); var physical = await PhysicalAsync(data);
        var actors = await ActorsAsync(data); var poolId = await SetupAsync(data, actors.Maker, actors.Checker);
        Assert.Equal(old, await data.StateAsync()); Assert.Equal(physical, await PhysicalAsync(data));
        var state = await CoverageAsync(data);
        Assert.Equal("procurement_stock_source_coverage_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(
            () => ReadAsync(data, actors.Checker, poolId))).Code);
        Assert.Equal(state, await CoverageAsync(data)); Assert.Equal(old, await data.StateAsync());
        Assert.Equal(request["requestId"], (await data.Rows(ProcurementRequestBson.Requests).Find(new BsonDocument()).SingleAsync())["_id"]);
    }

    [WindowsStorageFact(Skip = Pending.FixtureRewrite)]
    [SupportedOSPlatform("windows")]
    public async Task Real_unbound_PO_and_private_inspection_cannot_authorize_a_new_held_stock_leg() {
        await using var data = await ProcurementStockReceiptTestData.CreateAsync(fixture);
        var before = await data.StateAsync();
        var pool = await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
        var head = await data.Rows(ProcurementStockSourceCoverage.Heads).Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
        Assert.False(await data.Rows(PurchaseOrderInboundBindingBson.Selections).Find(new BsonDocument()).AnyAsync());
        Assert.Equal("procurement_stock_receipt_inbound_target_required", (await Assert.ThrowsAsync<PersistenceConflictException>(
            () => data.Service.PreviewAsync(data.Maker, data.Input()))).Code);
        Assert.Equal(before, await data.StateAsync());
        Assert.Equal(pool, await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync());
        Assert.Equal(head, await data.Rows(ProcurementStockSourceCoverage.Heads).Find(new BsonDocument("_id", data.PoolId)).SingleAsync());
        Assert.False(await data.Rows(ProcurementStockReceiptBson.Receipts).Find(new BsonDocument()).AnyAsync());
    }

    private static async Task<(ActorContext Maker, ActorContext Checker)> ActorsAsync(ProcurementRequestTestData data) {
        var maker = await data.Base.AddActorAsync(100011, ProcurementRequestTestData.Scope, Permissions.StockSourcePropose, Permissions.StockSourceRead);
        var checker = await data.Base.AddActorAsync(100012, ProcurementRequestTestData.Scope, Permissions.StockSourceApprove, Permissions.StockSourceRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, checker.UserId); return (maker, checker);
    }
    internal static async Task<long> SetupAsync(ProcurementRequestTestData data, ActorContext maker, ActorContext checker) {
        var sources = new StockSourceSetupService(data.Database, data.Unit, new(data.Database), data.Clock);
        var input = new StockSourceSetupInput(new(new(100), new(101), new(102), new(103)), "m100-synthetic-coverage-pool",
            "Synthetic Procurement coverage", StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60000, 0, null, null,
            "synthetic-discrepancy-owner", "synthetic-reviewed-handoff");
        var proposed = await sources.ProposeAsync(maker, input, ProcurementRequestTestData.Meta("coverage-source-propose"));
        return (await sources.DecideAsync(checker, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "synthetic-independent-review", ProcurementRequestTestData.Meta("coverage-source-approve")))["poolId"].AsInt64;
    }
    private static async Task<StockSourceObligationResult> ReadAsync(ProcurementRequestTestData data, ActorContext actor, long poolId) {
        var pool = await data.Rows("inventoryPools").Find(new BsonDocument("_id", poolId)).SingleAsync();
        var reader = new ProcurementStockSourceObligationReader(data.Database, data.Unit, new(data.Database), data.Clock);
        return await data.Unit.ExecuteAsync((tx, ct) => reader.ReadAndFenceAsync(tx, actor,
            new(poolId, InventoryGraph.Location(pool["location"].AsBsonDocument), pool["revision"].AsInt64, pool["fence"].AsInt64), ct));
    }
    private static async Task<BsonDocument> CoverageAsync(ProcurementRequestTestData data) {
        var result = new BsonDocument();
        foreach (var name in new[] { ProcurementStockSourceCoverage.Heads, ProcurementStockSourceCoverage.Transitions,
            ProcurementStockSourceRecords.Heads, ProcurementStockSourceRecords.Transitions, "inventoryPools" })
            result[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    private static async Task<BsonDocument> PhysicalAsync(ProcurementRequestTestData data) {
        var result = new BsonDocument();
        foreach (var name in new[] { "stockBalances", "movements", "lots", "reservations", "journalEntries", "bankReceipts", "paymentSubmissions", "paymentAllocations" })
            result[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
}
