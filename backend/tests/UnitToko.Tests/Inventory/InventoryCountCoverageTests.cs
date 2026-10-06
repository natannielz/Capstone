using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using Data = UnitToko.Tests.Inventory.InventoryCountStockSourceTests.Data;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class InventoryCountCoverageTests(MongoFixture fixture)
{
    [Fact]
    public async Task Neutral_unbound_original_custom_receipt_survives_matching_setup_and_remains_unresolved_without_inferred_pool()
    {
        await using var data = await NeutralAsync(fixture);
        var input = data.Input(2); var metadata = Data.Meta("coverage-neutral-original");
        var saved = await data.Counts.RecordCountAsync(data.Counter, input, metadata); Assert.Null(saved.PoolBinding);
        var row = await data.Rows(StockCountBson.Collection).Find(new BsonDocument("_id", saved.Id)).SingleAsync();
        var audit = await data.Rows("auditEvents").Find(new BsonDocument("_id", saved.AuditId)).SingleAsync();
        var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", StockCountBson.Operation }, { "key", metadata.IdempotencyKey } }).SingleAsync();
        Assert.Equal(1, row["schemaVersion"].AsInt32); Assert.False(row.Contains("poolBinding"));
        var member = await data.Rows(InventoryCountCoverage.Members).Find(new BsonDocument("_id", saved.Id)).SingleAsync();
        Assert.True(member["poolId"].IsBsonNull); Assert.Equal(InventoryCountCoverage.Member(row, "live"), member);
        var physical = await data.PhysicalAsync(); await ConfigureAsync(data, "coverage-neutral-source");
        var obligations = await data.ReadSourceAsync(); Assert.False(obligations.Complete); Assert.Equal(1L, obligations.OpenCount);
        Assert.Equal(saved, await data.Counts.RecordCountAsync(data.Counter, input, metadata));
        Assert.Equal(saved, await data.Counts.RecoverAsync(data.Counter, metadata.IdempotencyKey));
        Assert.Equal(row, await data.Rows(StockCountBson.Collection).Find(new BsonDocument("_id", saved.Id)).SingleAsync());
        Assert.Equal(audit, await data.Rows("auditEvents").Find(new BsonDocument("_id", saved.AuditId)).SingleAsync());
        Assert.Equal(receipt, await data.Rows("idempotencyRecords").Find(new BsonDocument("_id", receipt["_id"])).SingleAsync());
        Assert.Equal(member, await data.Rows(InventoryCountCoverage.Members).Find(new BsonDocument("_id", saved.Id)).SingleAsync());
        Assert.Equal("inventory_count_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            data.Counts.RecordCountAsync(data.Counter, input, Data.Meta("coverage-fresh-ambiguous")))).Code);
        Assert.Equal(1L, await data.Rows(StockCountBson.Collection).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(physical, await data.PhysicalAsync()); Assert.False((await data.PoolAsync())["active"].AsBoolean);
    }
    [Fact]
    public async Task Missing_or_reassigned_membership_refuses_complete_admission_and_fresh_writes_without_repairing_original_history()
    {
        await using var data = await Data.CreateAsync(fixture);
        var choice = Assert.Single((await data.Counts.SourcesAsync(data.Counter, Data.Warehouse, 0, 25)).Items);
        var input = data.Input() with { PoolSelection = choice.Pool.Selection };
        var saved = await data.Counts.RecordCountAsync(data.Counter, input, Data.Meta("coverage-bound-original"));
        var member = await data.Rows(InventoryCountCoverage.Members).Find(new BsonDocument("_id", saved.Id)).SingleAsync();
        Assert.Equal(1L, (await data.Rows(InventoryCountCoverage.Members).DeleteOneAsync(member)).DeletedCount);
        try {
            var before = await data.StateAsync(); var physical = await data.PhysicalAsync();
            Assert.Equal("inventory_count_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => data.ReadSourceAsync())).Code);
            Assert.Equal("inventory_count_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() =>
                data.Counts.RecordCountAsync(data.Counter, input, Data.Meta("coverage-not-appended")))).Code);
            Assert.Equal(saved, await data.Counts.RecoverAsync(data.Counter, "coverage-bound-original"));
            Assert.Equal(before, await data.StateAsync()); Assert.Equal(physical, await data.PhysicalAsync());
        } finally { await data.Rows(InventoryCountCoverage.Members).InsertOneAsync(member); }
        var reassigned = member.DeepClone().AsBsonDocument; reassigned["poolId"] = data.PoolId + 1;
        reassigned["memberHash"] = InventoryCountStockSourceRecords.Hash(reassigned, "memberHash");
        await data.Rows(InventoryCountCoverage.Members).ReplaceOneAsync(new BsonDocument("_id", saved.Id), reassigned);
        try {
            Assert.Equal("inventory_count_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => data.ReadSourceAsync())).Code);
        } finally { await data.Rows(InventoryCountCoverage.Members).ReplaceOneAsync(new BsonDocument("_id", saved.Id), member); }
        var complete = await data.ReadSourceAsync(); Assert.True(complete.Complete); Assert.Equal(1L, complete.OpenCount);
    }
    [Fact]
    public async Task Fresh_unbound_admission_and_real_source_approval_share_the_organization_fence_and_cannot_hide_open_work()
    {
        await using var data = await NeutralAsync(fixture); var physical = await data.PhysicalAsync();
        var proposed = await ProposeAsync(data, "coverage-racing-source");
        async Task Record() {
            try { await data.Counts.RecordCountAsync(data.Counter, data.Input(), Data.Meta("coverage-racing-unbound")); }
            catch (DomainValidationException failure) when (failure.Code == "inventory_count_source_obligation_unavailable") { }
            catch (PersistenceConflictException failure) when (failure.Code == "version_conflict") { }
        }
        var approval = data.Sources.DecideAsync(data.Reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Independent synthetic coverage review", Data.Meta("coverage-racing-source-approved"));
        await Task.WhenAll(approval, Record()); data.PoolId = (await approval)["poolId"].AsInt64;
        var observations = await data.Rows(StockCountBson.Collection).CountDocumentsAsync(new BsonDocument());
        var obligations = await data.ReadSourceAsync(); Assert.Equal(observations, obligations.OpenCount);
        Assert.Equal(observations == 0, obligations.Complete);
        Assert.Equal(observations, await data.Rows(InventoryCountCoverage.Members).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(physical, await data.PhysicalAsync()); Assert.False((await data.PoolAsync())["active"].AsBoolean);
    }
    private static async Task<Data> NeutralAsync(MongoFixture fixture)
    {
        var catalog = await CatalogAuthoringTestData.CreateAsync(fixture); var data = new Data(catalog);
        try {
            var sku = await catalog.SkuAsync(12, "COVERAGE-12", "COV"); data.SkuId = sku.Id.Value; data.DefinitionId = sku.CurrentDefinitionId!.Value.Value;
            data.Proposer = await catalog.ActorAsync(1016100, Data.Warehouse, Permissions.StockSourcePropose, Permissions.StockSourceRead);
            data.Reviewer = await catalog.ActorAsync(1016200, Data.Warehouse, Permissions.StockSourceApprove, Permissions.StockSourceRead);
            data.Counter = await catalog.ActorAsync(1016300, Data.Warehouse, Permissions.StockCount, Permissions.StockRead);
            await PersonTestData.SeedDistinctPeopleAsync(catalog.Database, catalog.Clock, data.Proposer.UserId, data.Reviewer.UserId, data.Counter.UserId);
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    private static Task<BsonDocument> ProposeAsync(Data data, string key) => data.Sources.ProposeAsync(data.Proposer,
        new(StockSourceSetupService.StockCountLocation(Data.Warehouse), "coverage-explicit-source", "Synthetic inactive coverage source",
            StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60000, 0, null, null, "Synthetic discrepancy owner", "Synthetic handoff"), Data.Meta(key));
    private static async Task ConfigureAsync(Data data, string key)
    {
        var proposal = await ProposeAsync(data, key);
        var approved = await data.Sources.DecideAsync(data.Reviewer, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Independent synthetic coverage review", Data.Meta(key + "-approved")); data.PoolId = approved["poolId"].AsInt64;
    }
}
