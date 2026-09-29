using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed partial class StockSourceSuspensionMigrationTests(MongoFixture fixture)
{
    private static readonly StockLocation Owner = new(new(100), new(101), new(102), new(103));
    private static readonly StockLocation Linked = Owner with { ChannelId = new(104) };
    private const string Collection = "stockSourceSuspensions";
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private StockSourceSetupService Service(StoreTestData data) => new(data.Database, new(fixture.Client), new(data.Database), data.Clock);
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic stop history", "suspension-migration");
    private static async Task<BsonArray> History(StoreTestData data) => new(await Rows(data, "schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task InstallDraft(StoreTestData data)
    {
        await data.Database.CreateCollectionAsync(Collection, new CreateCollectionOptions<BsonDocument>
        { Validator = StockSourceSuspensionSchemas.Validator(), ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error });
        await Rows(data, Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(StockSourceSuspensionSchemas.Key(),
            new CreateIndexOptions { Name = "reviewed_suspension_version", Unique = true }));
        foreach (var index in StockSourceSuspensionSchemas.LookupIndexes())
            await Rows(data, index.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(index.Key, new CreateIndexOptions { Name = index.Name }));
    }
    private sealed record Actors(ActorContext Maker, ActorContext Reviewer, ActorContext Operator, long Pool, StockSourceSetupInput Input);
    private async Task<Actors> Seed(StoreTestData data, bool suspended = true)
    {
        await Rows(data, "authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new UnitToko.Modules.Organizations.ScopeNode(104, UnitToko.Modules.Organizations.ScopeNodeKind.Channel, 100, UnitToko.Modules.Organizations.AuthorizationStatus.Active, 1)));
        var maker = await data.AddActorAsync(1000, Owner.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
        var reviewer = await data.AddActorAsync(2000, Owner.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead);
        var actor = await data.AddActorAsync(3000, Owner.ToScope(), Permissions.StockSourceSuspend);
        await data.AddGrantAsync(maker, 1001, Linked.ToScope(), [Permissions.StockSourcePropose, Permissions.StockSourceRead]);
        await data.AddGrantAsync(reviewer, 2001, Linked.ToScope(), [Permissions.StockSourceApprove, Permissions.StockSourceRead]);
        await data.AddGrantAsync(actor, 3001, Linked.ToScope(), [Permissions.StockSourceSuspend]);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId);
        var input = new StockSourceSetupInput(Owner, "synthetic-pool", "Synthetic pool", StockAuthorityMode.WebLedger,
            StockPoolControl.SharedWebLedger, 60001, 9007199254740993, null, null, "owner", "handoff");
        var service = Service(data);
        var proposal = await service.ProposeAsync(maker, input, Meta("source"));
        var result = await service.DecideAsync(reviewer, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("source-approved"));
        var pool = result["poolId"].AsInt64;
        var alias = await service.ProposeAliasAsync(maker, pool, 1, Linked, 0, true, Meta("mapping"));
        await service.DecideAliasAsync(reviewer, alias["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("mapped"));
        var actors = new Actors(maker, reviewer, actor, pool, input);
        if (suspended) await Stop(data, actors, "stop");
        return actors;
    }
    private async Task Stop(StoreTestData data, Actors actors, string key)
    {
        var service = Service(data); var preview = await service.PreviewSuspensionAsync(actors.Operator, actors.Pool);
        await service.SuspendAsync(actors.Operator, actors.Pool, new(preview["poolRevision"].AsInt64,
            preview["suspensionRevision"].AsInt64, preview["configurationHash"].AsString), Meta(key));
    }
    private static async Task<BsonDocument> Business(StoreTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in StockSourceSetupSchemas.Collections.Concat(StockSourceRevisionSchemas.Collections).Concat(StockSourceAliasSchemas.Collections)
            .Concat([Collection, "auditEvents", "idempotencyRecords", "stockBalances", "lots", "movements", "reservations", "journalEntries"]))
            result.Add(name, new BsonArray(await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return result;
    }
    [Fact]
    public async Task Registered_suspension_upgrade_runs_after_actual_v17_and_is_repeatable()
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        var before = await History(data); Assert.Equal(17, before.Count);
        var migration = new CollectionMigrations(data.Database, data.Clock);
        Assert.False(await migration.IsCurrentAsync()); await migration.ApplyAsync();
        var after = await History(data); Assert.Equal(CollectionMigrations.CurrentVersion, after.Count); Assert.Equal(before, new BsonArray(after.Take(17)));
        await InventoryRepository.RequireStorageAsync(data.Database, Collection, StockSourceSuspensionSchemas.Validator(), StockSourceSuspensionSchemas.Key(), default);
        foreach (var expected in StockSourceSuspensionSchemas.LookupIndexes())
            Assert.Contains(await (await Rows(data, expected.Collection).Indexes.ListAsync()).ToListAsync(), index => index["key"].Equals(expected.Key));
        Assert.True(await migration.IsCurrentAsync()); await migration.ApplyAsync(); Assert.Equal(after, await History(data));
    }
    [Fact]
    public async Task Actual_suspension_provenance_is_preserved_and_allocators_cover_stored_ids()
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data); var before = await Business(data);
        await Rows(data, "idCounters").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$in", new BsonArray { Collection, "auditEvents", "inventoryPools" })));
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(CollectionMigrations.CurrentVersion, (await History(data)).Count); Assert.Equal(before, await Business(data));
        foreach (var name in new[] { Collection, "auditEvents", "inventoryPools" })
        {
            var maximum = (await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstAsync())["_id"].AsInt64;
            Assert.Equal(maximum + 1, (await new IdAllocator(data.Database).NextAsync(name)).Value);
        }
    }
}