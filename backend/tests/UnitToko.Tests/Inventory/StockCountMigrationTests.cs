using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class StockCountMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_draft_migration_enforces_shapes_without_seeding_stock()
    {
        var db = fixture.Client.GetDatabase("ut_count_schema_" + Guid.NewGuid().ToString("N"));
        try
        {
            await new CollectionMigrations(db, new SystemClock()).ApplyAsync();
            await StockCountTestMigration.ApplyAsync(db, new SystemClock());
            Assert.Equal(0, await db.GetCollection<BsonDocument>("stockCountObservations").CountDocumentsAsync(new BsonDocument()));
            await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>("stockCountObservations").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } }));
            await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>("auditEvents").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 }, { "kind", "inventory.stock-count.recorded" } }));
            await StockCountTestMigration.ApplyAsync(db, new SystemClock());
            foreach (var name in new[] { "lots", "stockBalances", "movements", "reservations" })
                Assert.Equal(0, await db.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
        }
        finally { await fixture.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName); }
    }

    [Fact]
    public async Task Malformed_preexisting_observation_blocks_all_target_ddl()
    {
        var db = fixture.Client.GetDatabase("ut_count_preflight_" + Guid.NewGuid().ToString("N"));
        try
        {
            await new CollectionMigrations(db, new SystemClock()).ApplyAsync();
            // Seed deliberate preexisting corruption after registered v14 installs strict validation.
            await db.GetCollection<BsonDocument>("stockCountObservations").InsertOneAsync(
                new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } }, new InsertOneOptions { BypassDocumentValidation = true });
            var before = await Definitions(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => StockCountTestMigration.ApplyAsync(db, new SystemClock()));
            Assert.Equal(before, await Definitions(db));
            Assert.Null(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", "stockCountObservations")).SingleOrDefaultAsync());
        }
        finally { await fixture.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName); }
    }
    [Fact]
    public async Task Populated_retry_preserves_stronger_validator_extra_index_and_highwater()
    {
        await using var data = await UnitToko.Tests.Catalog.CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync();
        await StockCountTestMigration.ApplyAsync(data.Database, data.Clock);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.WarehouseActor.UserId),
            Builders<BsonDocument>.Update.Set("permissions", new BsonArray { "stock.count" }));
        var service = new UnitToko.Platform.Inventory.StockCountService(data.Database, new(data.Client), new(data.Database), data.Clock);
        var saved = await service.RecordCountAsync(data.WarehouseActor,
            new(UnitToko.Tests.Catalog.CatalogAuthoringTestData.Warehouse, sku.Id.Value, 1, sku.CurrentDefinitionId!.Value.Value, 2),
            UnitToko.Tests.Catalog.CatalogAuthoringTestData.Metadata("migration-populated"));
        var sharedIndexes = await SharedIndexes(data.Database);
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "stockCountObservations") })).SingleAsync();
        var stronger = new BsonDocument("$and", new BsonArray { definition["options"]["validator"], new BsonDocument("packages", new BsonDocument("$lte", 999999)) });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "stockCountObservations" }, { "validator", stronger } });
        await data.Rows("stockCountObservations").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("packages", 1), new CreateIndexOptions { Name = "ix_count_extra" }));
        await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", "stockCountObservations"),
            Builders<BsonDocument>.Update.Set("value", 0L));
        await StockCountTestMigration.ApplyAsync(data.Database, data.Clock);
        await StockCountTestMigration.ApplyAsync(data.Database, data.Clock);
        Assert.Equal(saved, await service.RecoverAsync(data.WarehouseActor, "migration-populated"));
        Assert.True((await new IdAllocator(data.Database).NextAsync("stockCountObservations")).Value > saved.Id);
        var after = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "stockCountObservations") })).SingleAsync();
        Assert.Equal(stronger, after["options"]["validator"]);
        Assert.Equal(sharedIndexes, await SharedIndexes(data.Database));
        Assert.Contains(await (await data.Rows("stockCountObservations").Indexes.ListAsync()).ToListAsync(), row => row["name"] == "ix_count_extra");
    }

    [Theory]
    [InlineData("index")] [InlineData("counter")]
    public async Task Incompatible_index_or_counter_preflight_does_not_change_any_target(string defect)
    {
        var db = fixture.Client.GetDatabase("ut_count_incompatible_" + Guid.NewGuid().ToString("N"));
        try
        {
            await new CollectionMigrations(db, new SystemClock()).ApplyAsync();
            await StockCountTestMigration.ApplyAsync(db, new SystemClock());
            if (defect == "index")
            {
                await db.GetCollection<BsonDocument>("stockCountObservations").Indexes.DropOneAsync("ux_stock_count_request");
                await db.GetCollection<BsonDocument>("stockCountObservations").Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(new BsonDocument("requestKey", 1), new CreateIndexOptions { Name = "ux_stock_count_request", Unique = true }));
            }
            else
                await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "stockCountObservations" }, { "value", 1 } },
                    new InsertOneOptions { BypassDocumentValidation = true });
            var before = await Definitions(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => StockCountTestMigration.ApplyAsync(db, new SystemClock()));
            Assert.Equal(before, await Definitions(db));
        }
        finally { await fixture.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName); }
    }
    private static async Task<BsonDocument> SharedIndexes(IMongoDatabase database)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "auditEvents", "idempotencyRecords" })
        {
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync();
            result[name] = new BsonArray(indexes.OrderBy(row => row["name"].AsString, StringComparer.Ordinal));
        }
        return result;
    }
    private static async Task<BsonArray> Definitions(IMongoDatabase db) =>
        new((await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", new BsonDocument("$in", new BsonArray { "stockCountObservations", "auditEvents", "idempotencyRecords" })) })).ToListAsync()).OrderBy(row => row["name"].AsString));
}

internal static class StockCountTestMigration
{
    // Exercise draft migration DDL with a real isolated lease; never fabricate v13/v14 deployment history.
    internal static async Task ApplyAsync(IMongoDatabase db, IClock clock)
    {
        var history = db.GetCollection<BsonDocument>("stockCountTestMigrationLeases").WithWriteConcern(WriteConcern.WMajority);
        await history.DeleteManyAsync(new BsonDocument());
        var owner = Guid.NewGuid().ToString("N");
        await history.InsertOneAsync(new BsonDocument { { "_id", 1 }, { "state", "applying" }, { "owner", owner }, { "leaseUntil", clock.UtcNow.UtcDateTime.AddMinutes(10) } });
        var lease = new MigrationLease(history, clock, 1, 1, owner, TimeSpan.FromMinutes(10));
        await new StockCountMigration(db.WithWriteConcern(WriteConcern.WMajority)).ApplyAsync(lease, default);
        await lease.CompleteAsync(default);
    }
}
