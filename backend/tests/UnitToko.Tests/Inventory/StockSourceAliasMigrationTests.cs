using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
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
public sealed partial class StockSourceAliasMigrationTests(MongoFixture fixture)
{
    private static readonly StockLocation Owner = new(new(100), new(101), new(102), new(103));
    private static readonly StockLocation Requested = Owner with { ChannelId = new(104) };
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private static async Task<BsonArray> History(StoreTestData data) => new(await Rows(data, "schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private StockSourceSetupService Service(StoreTestData data) => new(data.Database, new(fixture.Client), new(data.Database), data.Clock);
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic mapping history", "alias-migration");
    private static async Task InstallDraft(StoreTestData data)
    {
        foreach (var name in StockSourceAliasSchemas.Collections)
        {
            await data.Database.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument>
            { Validator = StockSourceAliasSchemas.Validator(name), ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error });
            var key = StockSourceAliasSchemas.Key(name);
            if (!key.Equals(new BsonDocument("_id", 1))) await Rows(data, name).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = "ux_alias_draft" }));
        }
    }
    private sealed record Actors(ActorContext Maker, ActorContext Reviewer, long Pool, StockSourceSetupInput Input);
    private async Task<Actors> Seed(StoreTestData data)
    {
        await Rows(data, "authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)));
        var maker = await data.AddActorAsync(1000, Owner.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead);
        var reviewer = await data.AddActorAsync(2000, Owner.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead);
        await data.AddGrantAsync(maker, 1001, Requested.ToScope(), [Permissions.StockSourcePropose, Permissions.StockSourceRead]);
        await data.AddGrantAsync(reviewer, 2001, Requested.ToScope(), [Permissions.StockSourceApprove, Permissions.StockSourceRead]);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId);
        var input = new StockSourceSetupInput(Owner, "synthetic-pool", "Original pool", StockAuthorityMode.WebLedger,
            StockPoolControl.SharedWebLedger, 60001, 9007199254740993, null, null, "owner", "handoff");
        var service = Service(data);
        var source = await service.ProposeAsync(maker, input, Meta("source"));
        var approved = await service.DecideAsync(reviewer, source["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("source-approve"));
        var actors = new Actors(maker, reviewer, approved["poolId"].AsInt64, input);
        await AddAlias(data, actors, 0, true, 1);
        return actors;
    }
    private async Task<BsonDocument> AddAlias(StoreTestData data, Actors actors, long version, bool enabled, long poolVersion)
    {
        var service = Service(data);
        var proposed = await service.ProposeAliasAsync(actors.Maker, actors.Pool, poolVersion, Requested, version, enabled, Meta("alias-" + version));
        return await service.DecideAliasAsync(actors.Reviewer, proposed["proposalId"].AsInt64, 1,
            StockSourceSetupDecision.Approve, "synthetic-review", Meta("approve-" + version));
    }
    private static async Task<BsonDocument> BusinessRows(StoreTestData data)
    {
        var rows = new BsonDocument();
        foreach (var name in StockSourceSetupSchemas.Collections.Concat(StockSourceRevisionSchemas.Collections)
            .Concat(StockSourceAliasSchemas.Collections).Concat(["auditEvents", "idempotencyRecords", "lots", "stockBalances", "movements", "reservations", "journalEntries"]))
            rows.Add(name, new BsonArray(await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return rows;
    }
    [Fact]
    public async Task Registered_alias_migration_follows_actual_v16_and_preserves_applied_history()
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        var before = await History(data); Assert.Equal(16, before.Count);
        var migrations = new CollectionMigrations(data.Database, data.Clock);
        Assert.False(await migrations.IsCurrentAsync());
        await migrations.ApplyAsync();
        var after = await History(data); Assert.Equal(17, after.Count); Assert.Equal(before, new BsonArray(after.Take(16)));
        foreach (var name in StockSourceAliasSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(data.Database, name, StockSourceAliasSchemas.Validator(name), StockSourceAliasSchemas.Key(name), default);
        Assert.True(await migrations.IsCurrentAsync()); await migrations.ApplyAsync(); Assert.Equal(after, await History(data));
    }
    [Fact]
    public async Task Approved_alias_history_is_preserved_and_counters_advance_above_stored_ids()
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data); await AddAlias(data, actors, 1, false, 1);
        var before = await BusinessRows(data);
        await Rows(data, "idCounters").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$in",
            new BsonArray(StockSourceAliasSchemas.Collections.Append("auditEvents")))));
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(17, (await History(data)).Count); Assert.Equal(before, await BusinessRows(data));
        foreach (var name in StockSourceAliasSchemas.Collections.Append("auditEvents"))
        {
            var maximum = (await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstAsync())["_id"].AsInt64;
            Assert.Equal(maximum + 1, (await new IdAllocator(data.Database).NextAsync(name)).Value);
        }
    }
}
