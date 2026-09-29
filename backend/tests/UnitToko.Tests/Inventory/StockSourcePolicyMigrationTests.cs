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
public sealed partial class StockSourcePolicyMigrationTests(MongoFixture fixture)
{
    private static readonly StockLocation Owner = new(new(100), new(101), new(102), new(103));
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private StockSourceSetupService Service(StoreTestData data) => new(data.Database, new(fixture.Client), new(data.Database), data.Clock);
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic policy migration", "policy-migration");
    private static async Task<BsonArray> History(StoreTestData data) => new(await Rows(data, "schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task InstallDraft(StoreTestData data)
    {
        foreach (var name in StockSourcePolicySchemas.Collections)
        {
            await data.Database.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument>
            { Validator = StockSourcePolicySchemas.Validator(name), ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error });
            if (name != StockSourcePolicyBson.Proposals)
                await Rows(data, name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(StockSourcePolicySchemas.Key(name),
                    new CreateIndexOptions { Unique = true, Name = "reviewed_policy_unique" }));
        }
        foreach (var index in StockSourcePolicySchemas.LookupIndexes())
            await Rows(data, index.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(index.Key, new CreateIndexOptions { Name = index.Name }));
    }
    private sealed record Actors(ActorContext Maker, ActorContext Reviewer, long Pool, long Proposal, StockSourceSetupInput Source);
    private async Task<Actors> Seed(StoreTestData data, bool external = false, StockSourceSetupDecision decision = StockSourceSetupDecision.Approve)
    {
        var maker = await data.AddActorAsync(1100, Owner.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourceRead,
            Permissions.StockSourceSuspend, Permissions.StockSourcePolicyPropose);
        var reviewer = await data.AddActorAsync(2200, Owner.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourceRead, Permissions.StockSourcePolicyApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId);
        var source = new StockSourceSetupInput(Owner, "migration-pool", "Synthetic source", external ? StockAuthorityMode.ExternalLedger : StockAuthorityMode.WebLedger,
            external ? StockPoolControl.ExternalHold : StockPoolControl.SharedWebLedger, 60001, 9007199254740993,
            external ? "synthetic-external" : null, external ? "Synthetic protocol definition" : null, "Synthetic owner", "Synthetic handoff definition");
        var service = Service(data);
        var setup = await service.ProposeAsync(maker, source, Meta("source"));
        var setupDecision = await service.DecideAsync(reviewer, setup["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic setup", Meta("source-approved"));
        var pool = setupDecision["poolId"].AsInt64;
        var preview = await service.PreviewOperatingPolicyAsync(maker, pool);
        var policy = new StockSourceOperatingPolicyInput(external ? StockMovementWriter.ExternalSystem : StockMovementWriter.UnitToko,
            external ? "Tenant-A/02" : null, external ? 120000 : 0, "Synthetic reconciliation", "Synthetic outage");
        var proposed = await service.ProposeOperatingPolicyAsync(maker, pool, new(preview["poolRevision"].AsInt64,
            preview["suspensionRevision"].AsInt64, preview["policyRevision"].AsInt64, preview["configurationHash"].AsString), policy, Meta("policy"));
        await service.DecideOperatingPolicyAsync(decision == StockSourceSetupDecision.Withdraw ? maker : reviewer, proposed["proposalId"].AsInt64,
            1, decision, "Synthetic policy review", Meta("policy-decided"));
        return new(maker, reviewer, pool, proposed["proposalId"].AsInt64, source);
    }
    private static async Task<BsonDocument> Business(StoreTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in StockSourceSetupSchemas.Collections.Concat(StockSourceRevisionSchemas.Collections).Concat(StockSourceAliasSchemas.Collections)
            .Concat(StockSourcePolicySchemas.Collections).Concat([StockSourceSuspensionSchemas.Collection, "auditEvents", "idempotencyRecords", "stockBalances", "lots", "movements", "reservations", "journalEntries", "accountingBooks", "accountingChartVersions", "periods", "accountingPeriodHistory", "accountingAuditEvents"]))
            result[name] = new BsonArray(await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }

    [Fact]
    public async Task Registered_policy_upgrade_follows_actual_v18_and_is_repeatable()
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        var before = await History(data); Assert.Equal(18, before.Count);
        var migration = new CollectionMigrations(data.Database, data.Clock);
        Assert.False(await migration.IsCurrentAsync()); await migration.ApplyAsync();
        var after = await History(data); Assert.Equal(19, after.Count); Assert.Equal(before, new BsonArray(after.Take(18)));
        foreach (var name in StockSourcePolicySchemas.Collections)
            await InventoryRepository.RequireStorageAsync(data.Database, name, StockSourcePolicySchemas.Validator(name), StockSourcePolicySchemas.Key(name), default);
        foreach (var expected in StockSourcePolicySchemas.LookupIndexes())
            Assert.Contains(await (await Rows(data, expected.Collection).Indexes.ListAsync()).ToListAsync(), index => index["key"].Equals(expected.Key));
        Assert.True(await migration.IsCurrentAsync()); await migration.ApplyAsync(); Assert.Equal(after, await History(data));
    }

    [Theory]
    [InlineData(false, StockSourceSetupDecision.Approve)] [InlineData(true, StockSourceSetupDecision.Approve)]
    [InlineData(false, StockSourceSetupDecision.Reject)] [InlineData(false, StockSourceSetupDecision.Withdraw)]
    public async Task Actual_policy_history_survives_upgrade_and_exact_allocator_repair(bool external, StockSourceSetupDecision decision)
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data, external, decision); var before = await Business(data);
        var counters = StockSourcePolicySchemas.Collections.Concat(["auditEvents", "inventoryPools"]).ToArray();
        await Rows(data, "idCounters").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(counters))));
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(19, (await History(data)).Count); Assert.Equal(before, await Business(data));
        foreach (var name in counters)
        {
            var highest = await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstOrDefaultAsync();
            if (highest is not null) Assert.Equal(highest["_id"].AsInt64 + 1, (await new IdAllocator(data.Database).NextAsync(name)).Value);
        }
        Assert.NotNull(await Service(data).ReadOperatingPolicyProposalAsync(actors.Maker, actors.Pool, actors.Proposal));
    }
}