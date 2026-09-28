using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed partial class StockSourceRevisionMigrationTests(MongoFixture fixture)
{
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private static async Task<BsonArray> History(StoreTestData data) =>
        new(await Rows(data, "schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());

    [Fact]
    public async Task Registered_revision_migration_follows_actual_v15_and_installs_enforced_storage()
    {
        await using var data = await StoreTestData.CreateForStockSourceRevisionMigrationAsync(fixture);
        var before = await History(data); Assert.Equal(15, before.Count);
        var migrations = new CollectionMigrations(data.Database, data.Clock);
        Assert.False(await migrations.IsCurrentAsync());
        await migrations.ApplyAsync();
        Assert.True(await migrations.IsCurrentAsync());
        var after = await History(data); Assert.Equal(16, after.Count);
        Assert.Equal(before, new BsonArray(after.Take(15)));
        foreach (var name in StockSourceRevisionSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(data.Database, name, StockSourceRevisionSchemas.Validator(name), StockSourceRevisionSchemas.Key(name), default);
        await migrations.ApplyAsync();
        Assert.Equal(after, await History(data));
    }

    [Fact]
    public async Task Approved_revision_history_is_preserved_and_new_identity_counters_advance()
    {
        await using var data = await StoreTestData.CreateForStockSourceRevisionMigrationAsync(fixture);
        await InstallDraft(data);
        await Seed(data);
        var before = await BusinessRows(data);
        await Rows(data, "idCounters").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$in",
            new BsonArray(StockSourceRevisionSchemas.Collections.Append("auditEvents")))));
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(16, (await History(data)).Count);
        Assert.Equal(before, await BusinessRows(data));
        foreach (var name in StockSourceRevisionSchemas.Collections.Append("auditEvents"))
        {
            var maximum = (await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstAsync())["_id"].AsInt64;
            Assert.Equal(maximum + 1, (await new IdAllocator(data.Database).NextAsync(name)).Value);
        }
    }

    private static async Task InstallDraft(StoreTestData data)
    {
        foreach (var name in StockSourceRevisionSchemas.Collections)
        {
            await data.Database.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument>
                { Validator = StockSourceRevisionSchemas.Validator(name), ValidationAction = DocumentValidationAction.Error, ValidationLevel = DocumentValidationLevel.Strict });
            var key = StockSourceRevisionSchemas.Key(name);
            if (!key.Equals(new BsonDocument("_id", 1)))
                await Rows(data, name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key,
                    new CreateIndexOptions { Unique = true, Name = "ux_revision_draft" }));
        }
    }
    private sealed record SeedActors(ActorContext Maker, ActorContext Reviewer, long Pool);
    private async Task<SeedActors> Seed(StoreTestData data)
    {
        var location = new StockLocation(new(100), new(101), new(102), new(103));
        var input = new StockSourceSetupInput(location, "synthetic-pool", "Original",
            StockAuthorityMode.WebLedger, StockPoolControl.ExclusiveWebAllocation, 60000, 0, null, null, "owner", "handoff");
        var maker = await data.AddActorAsync(1000, location.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
        var reviewer = await data.AddActorAsync(2000, location.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId);
        var service = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        var proposal = await service.ProposeAsync(maker, input, new("original", "Synthetic original", "migration-test"));
        var original = await service.DecideAsync(reviewer, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "synthetic-original-review", new("original-approve", "Synthetic original approval", "migration-test"));
        var revised = await service.ProposeRevisionAsync(maker, original["poolId"].AsInt64, 1, input with { Label = "Revised" },
            new("revise", "Synthetic settings change", "migration-test"));
        await service.DecideRevisionAsync(reviewer, revised["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "synthetic-revision-review", new("revise-approve", "Synthetic revision approval", "migration-test"));
        return new(maker, reviewer, original["poolId"].AsInt64);
    }
    private static async Task<BsonDocument> BusinessRows(StoreTestData data)
    {
        var rows = new BsonDocument();
        foreach (var name in StockSourceSetupSchemas.Collections.Concat(StockSourceRevisionSchemas.Collections).Concat(["auditEvents", "idempotencyRecords"]))
            rows.Add(name, new BsonArray(await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return rows;
    }
}
