using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Procurement;

namespace UnitToko.Tests.Inventory;

/// <summary>Genuine retained proposals and isolated negative corruption only. No legacy
/// approval/head backfill, active pool, admitted order, complete drain or release is claimed.</summary>
[Collection("Mongo")]
public sealed class OrderStockSourceMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Genuine_retained_proposal_and_all_existing_owners_survive_empty_upgrade_and_replay_byte_exact()
    {
        await using var data = await OrderStockSourceTestData.CreateAsync(fixture, approve: false);
        await RestoreV89Async(data);
        await data.Rows("stockSourceProposals").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("proposedBy", 1), new CreateIndexOptions { Name = "operator_retained_proposal_lookup", Unique = false }));
        var predecessor = await HistoryAsync(data); var before = await SnapshotAsync(data);
        Assert.Equal(89, predecessor.Count); Assert.Equal(1L, await data.Rows("stockSourceProposals").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows("inventoryPools").CountDocumentsAsync(new BsonDocument()));
        var migrations = new CollectionMigrations(data.Data.Database, data.Data.Clock);
        Assert.False(await migrations.IsCurrentAsync()); await migrations.ApplyAsync(); Assert.True(await migrations.IsCurrentAsync());
        var applied = await HistoryAsync(data); Assert.Equal(94, applied.Count);
        Assert.Equal(predecessor, new BsonArray(applied.Take(89))); Assert.Equal("applied", applied[89]["state"].AsString);
        Assert.Equal(91L, applied[90]["_id"].ToInt64()); Assert.Equal("applied", applied[90]["state"].AsString);
        Assert.Equal(92L, applied[91]["_id"].ToInt64()); Assert.Equal("applied", applied[91]["state"].AsString);
        Assert.Equal(93L, applied[92]["_id"].ToInt64()); Assert.Equal("applied", applied[92]["state"].AsString);
        Assert.Equal(94L, applied[93]["_id"].ToInt64()); Assert.Equal("applied", applied[93]["state"].AsString);
        var after = await SnapshotAsync(data);
        Assert.Equal(before.ElementCount + OrderStockSourceSchemas.Collections.Length + ReconciliationStockSourceSchemas.Collections.Length + FulfillmentStockSourceSchemas.Collections.Length + ProcurementStockSourceSchemas.Collections.Length + PurchaseOrderInboundBindingSchemas.Collections.Length, after.ElementCount);
        foreach (var name in before.Names) Assert.Equal(before[name], after[name]);
        foreach (var name in OrderStockSourceSchemas.Collections.Concat(ReconciliationStockSourceSchemas.Collections).Concat(FulfillmentStockSourceSchemas.Collections).Concat(ProcurementStockSourceSchemas.Collections).Concat(PurchaseOrderInboundBindingSchemas.Collections))
        {
            Assert.Equal(0L, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
            Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        await OrderStockSourceStorage.RequireAsync(data.Data.Database, default);
        await UnitToko.Platform.Inventory.ReconciliationStockSourceStorage.RequireAsync(data.Data.Database, default);
        await FulfillmentStockSourceStorage.RequireAsync(data.Data.Database, default);
        await ProcurementStockSourceStorage.RequireAsync(data.Data.Database, default);
        await PurchaseOrderInboundBindingStorage.RequireAsync(data.Data.Database, default);
        await migrations.ApplyAsync();
        Assert.Equal(applied, await HistoryAsync(data)); Assert.Equal(after, await SnapshotAsync(data));
        // A compatible actual proposal is preserved; absence is never turned into an approved pool or producer revision.
        Assert.Equal(data.Proposed["proposalId"], (await data.Rows("stockSourceProposals").Find(new BsonDocument()).SingleAsync())["_id"]);
        Assert.Equal(0L, await data.Rows("stockSourceDecisions").CountDocumentsAsync(new BsonDocument()));
    }

    [Theory]
    [InlineData("owner-row")]
    [InlineData("spent-counter")]
    [InlineData("unsafe-index")]
    public async Task Prospective_owner_or_allocator_or_operator_constraint_refuses_before_any_other_target_DDL(string fault)
    {
        await using var data = await OrderStockSourceTestData.CreateAsync(fixture, approve: false);
        await RestoreV89Async(data);
        var name = OrderStockSourceRecords.Heads;
        await data.Data.Database.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument> {
            Validator = OrderStockSourceSchemas.Validator(name), ValidationLevel = DocumentValidationLevel.Strict,
            ValidationAction = DocumentValidationAction.Error });
        if (fault == "owner-row")
        {
            // Explicit negative corruption: structurally valid prospective bytes have no genuine setup approval/transition.
            // This is never used as an initialized source or a successful reader fixture.
            var corrupt = new BsonDocument { { "_id", 881001L }, { "schemaVersion", 1 }, { "kind", "ordering.stocksource.head" },
                { "location", UnitToko.Platform.Inventory.StockSourceSetupBson.Location(OrderStockSourceTestData.Location) },
                { "setupDecisionId", 881002L }, { "revision", 1L }, { "lastTransitionId", 881003L }, { "lastHash", new string('a', 64) },
                { "updatedAt", data.Data.Clock.UtcNow.UtcDateTime }, { "fence", 0L } };
            await data.Rows(name).InsertOneAsync(corrupt);
            Assert.True(await data.Rows(name).Find(OrderStockSourceSchemas.Validator(name)).AnyAsync());
        }
        if (fault == "spent-counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", name }, { "value", 1L } });
        if (fault == "unsafe-index") await data.Rows(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("setupDecisionId", 1), new CreateIndexOptions { Name = "operator_unreviewed_uniqueness", Unique = true }));
        await RequirePreDdlRefusalAsync(data);
        Assert.NotNull(await AccountContactStorage.DefinitionAsync(data.Data.Database, name, default));
        Assert.Null(await AccountContactStorage.DefinitionAsync(data.Data.Database, OrderStockSourceRecords.Transitions, default));
    }

    [Fact]
    public async Task Structurally_valid_orphan_order_child_refuses_before_creating_producer_owners_and_preserves_every_byte()
    {
        await using var data = await OrderStockSourceTestData.CreateAsync(fixture, approve: false);
        await RestoreV89Async(data);
        var orphan = new BsonDocument { { "_id", 882001L }, { "schemaVersion", 1 }, { "kind", "ordering.reservation" },
            { "revision", 1L }, { "fence", 0L }, { "orderId", 882002L }, { "suborderId", 882003L },
            { "allocation", new BsonDocument { { "skuId", 882004L }, { "authority", new BsonDocument { { "id", 882005L }, { "revision", 1L } } },
                { "poolId", 882006L }, { "poolRevision", 1L }, { "balanceId", 882007L }, { "balanceRevision", 1L },
                { "definitionId", 882008L }, { "lotId", 882009L }, { "packages", 1L },
                { "location", UnitToko.Platform.Inventory.StockSourceSetupBson.Location(OrderStockSourceTestData.Location) } } },
            { "remaining", 1L }, { "dispatched", 0L }, { "released", 0L } };
        await data.Rows("reservations").InsertOneAsync(orphan);
        Assert.True(await data.Rows("reservations").Find(OrderSubmissionSchemas.Validator("reservations")).AnyAsync());
        await RequirePreDdlRefusalAsync(data);
        foreach (var name in OrderStockSourceSchemas.Collections)
            Assert.Null(await AccountContactStorage.DefinitionAsync(data.Data.Database, name, default));
        Assert.Equal(orphan, await data.Rows("reservations").Find(new BsonDocument("_id", orphan["_id"])).SingleAsync());
    }

    private static async Task RequirePreDdlRefusalAsync(OrderStockSourceTestData data)
    {
        var predecessor = await HistoryAsync(data); var before = await SnapshotAsync(data);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Data.Database, data.Data.Clock).ApplyAsync());
        Assert.Equal("Ordering source migration requires exact safe retained history and empty new owners", error.Message);
        Assert.Equal(before, await SnapshotAsync(data));
        var failed = await HistoryAsync(data); Assert.Equal(90, failed.Count);
        Assert.Equal(predecessor, new BsonArray(failed.Take(89))); Assert.Equal("applying", failed[89]["state"].AsString);
        Assert.False(await new CollectionMigrations(data.Data.Database, data.Data.Clock).IsCurrentAsync());
    }

    private static async Task RestoreV89Async(OrderStockSourceTestData data)
    {
        // Destructive rehearsal is confined to this fixture's freshly created random database.
        Assert.Matches(@"\Aunittoko_test_[0-9a-f]{32}_[0-9a-f]{8}\z", data.Data.Database.DatabaseNamespace.DatabaseName);
        await ReconciliationStockSourceMigrationTests.RestoreEmptyV91Async(data.Data.Database);
        var history = await HistoryAsync(data);
        Assert.True(MigrationHistory.IsValid(history.Select(value => value.AsBsonDocument).ToArray(), 91));
        Assert.Equal(91, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"].AsString));
        // The later producer suffix can be removed only while its genuine owners and allocators
        // are exactly empty. Never erase a setup-created head or immutable transition to rehearse90.
        foreach (var name in ReconciliationStockSourceSchemas.Collections)
        {
            var definition = await AccountContactStorage.DefinitionAsync(data.Data.Database, name, default); Assert.NotNull(definition);
            Assert.Equal(ReconciliationStockSourceSchemas.Validator(name), definition["options"]["validator"].AsBsonDocument);
            Assert.True(AccountContactStorage.SafeDefinition(definition, ReconciliationStockSourceSchemas.Validator(name)));
            Assert.Equal("strict", definition["options"]["validationLevel"].AsString);
            Assert.Equal("error", definition["options"]["validationAction"].AsString);
            Assert.Equal(0L, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
            Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            Assert.True(OrderStockSourceStorage.SafeIndexes(indexes, ReconciliationStockSourceSchemas.Keys(name)));
            Assert.All(ReconciliationStockSourceSchemas.Keys(name), key => Assert.Contains(indexes, row => AccountContactStorage.Matches(row, key)));
            Assert.Equal(1 + ReconciliationStockSourceSchemas.Keys(name).Length, indexes.Count);
        }
        Assert.Equal(1L, (await data.Rows("schemaMigrations").DeleteOneAsync(history[90].AsBsonDocument)).DeletedCount);
        foreach (var name in ReconciliationStockSourceSchemas.Collections) await data.Data.Database.DropCollectionAsync(name);
        Assert.Equal(new BsonArray(history.Take(90)), await HistoryAsync(data));
        history = await HistoryAsync(data);
        Assert.True(MigrationHistory.IsValid(history.Select(value => value.AsBsonDocument).ToArray(), 90));
        Assert.Equal(90, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"].AsString));
        foreach (var name in OrderStockSourceSchemas.Collections)
        {
            var definition = await AccountContactStorage.DefinitionAsync(data.Data.Database, name, default); Assert.NotNull(definition);
            Assert.Equal(OrderStockSourceSchemas.Validator(name), definition["options"]["validator"].AsBsonDocument);
            Assert.True(AccountContactStorage.SafeDefinition(definition, OrderStockSourceSchemas.Validator(name)));
            Assert.Equal("strict", definition["options"]["validationLevel"].AsString);
            Assert.Equal("error", definition["options"]["validationAction"].AsString);
            Assert.Equal(0L, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
            Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            Assert.True(OrderStockSourceStorage.SafeIndexes(indexes, OrderStockSourceSchemas.Keys(name)));
            Assert.All(OrderStockSourceSchemas.Keys(name), key => Assert.Contains(indexes, row => AccountContactStorage.Matches(row, key)));
            Assert.Equal(1 + OrderStockSourceSchemas.Keys(name).Length, indexes.Count);
        }
        // Only the proven applied marker and the two proven exact EMPTY future owners are removed.
        Assert.Equal(1L, (await data.Rows("schemaMigrations").DeleteOneAsync(history[89].AsBsonDocument)).DeletedCount);
        foreach (var name in OrderStockSourceSchemas.Collections) await data.Data.Database.DropCollectionAsync(name);
        Assert.Equal(89, (await HistoryAsync(data)).Count);
    }

    private static async Task<BsonArray> HistoryAsync(OrderStockSourceTestData data) => new(
        await data.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());

    private static async Task<BsonDocument> SnapshotAsync(OrderStockSourceTestData data)
    {
        var result = new BsonDocument();
        var definitions = await (await data.Data.Database.ListCollectionsAsync()).ToListAsync();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection").OrderBy(row => row["name"].AsString, StringComparer.Ordinal))
        {
            var name = definition["name"].AsString;
            var indexes = (await (await data.Rows(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal);
            var state = new BsonDocument { { "definition", definition.DeepClone() }, { "indexes", new BsonArray(indexes) } };
            // Actual deployment history is asserted separately: exact89 predecessors and real90/91/92/93/94 markers.
            if (name != "schemaMigrations") state.Add("rows", new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            result.Add(name, state);
        }
        return result;
    }
}
