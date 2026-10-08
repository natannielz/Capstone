using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class TemporaryDelegationMigrationTests(MongoFixture fixture)
{
    private async Task<CatalogPriceTestData> PredecessorAsync() {
        var data = await CatalogPriceTestData.CreateAsync(fixture, install: false);
        try {
            var steps = new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock).RegisteredSteps().Where(step => step.Version <= 112).OrderBy(step => step.Version).ToArray();
            Assert.Equal(Enumerable.Range(1, 112), steps.Select(step => step.Version));
            foreach (var step in steps) {
                var lease = await new MigrationGate(data.Catalog.Database, data.Catalog.Clock, 113).AcquireAsync(step.Version, default);
                if (lease is null) continue; await step.ApplyAsync(lease, default); await lease.CompleteAsync(default);
            }
            return data;
        } catch {await data.DisposeAsync(); throw;}
    }
    [Fact]
    public async Task Genuine112_prefix_and_empty_partial_DDL_resume_without_grants_or_policy_defaults() {
        await using var data = await PredecessorAsync();
        var before = new BsonArray(await data.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var stronger = new BsonDocument("$and", new BsonArray {TemporaryDelegationSchemas.Validator(TemporaryDelegationRecords.Heads), new BsonDocument("sourceUserId", new BsonDocument("$gt", 0L))});
        await data.Catalog.Database.CreateCollectionAsync(TemporaryDelegationRecords.Heads, new CreateCollectionOptions<BsonDocument> {Validator = stronger, ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error});
        var lease = await new MigrationGate(data.Catalog.Database, data.Catalog.Clock, 113).AcquireAsync(113, default); Assert.NotNull(lease);
        var step = new TemporaryDelegationMigration(data.Catalog.Database);
        await step.ApplyAsync(lease, default); await step.ApplyAsync(lease, default); await lease.CompleteAsync(default);
        Assert.Equal(before, new BsonArray((await data.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Take(112)));
        await TemporaryDelegationStorage.RequireAsync(data.Catalog.Database, default);
        foreach (var table in TemporaryDelegationSchemas.Collections) {
            Assert.Equal(0, await data.Rows(table).CountDocumentsAsync(new BsonDocument()));
        }
        Assert.Equal(0, await data.Rows("authorizationGrants").CountDocumentsAsync(new BsonDocument("templateId", "temporary-delegation")));
        var definition = await (await data.Catalog.Database.ListCollectionsAsync(new ListCollectionsOptions {Filter = new BsonDocument("name", TemporaryDelegationRecords.Heads)})).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
    }
    [Theory]
    [InlineData("owner-row")]
    [InlineData("retention")]
    [InlineData("request-index")]
    [InlineData("counter")]
    [InlineData("handover-orphan")]
    public async Task Ambiguous_preexisting_owner_storage_blocks_all_mutation(string fault) {
        await using var data = await PredecessorAsync(); var events = fault == "handover-orphan" ? UnitToko.Platform.Billing.PaymentReviewHandoverRecords.Collection : TemporaryDelegationRecords.Events;
        await data.Catalog.Database.CreateCollectionAsync(events);
        if (fault is "owner-row" or "handover-orphan") await data.Rows(events).InsertOneAsync(new BsonDocument("_id", 1L));
        if (fault == "retention") await data.Rows(events).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions {ExpireAfter = TimeSpan.FromHours(1)}));
        if (fault == "request-index") await data.Rows(events).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { { "actorId", 1 }, { "requestKey", 1 } }, new CreateIndexOptions {Name = "ux_delegation_request", Unique = false}));
        if (fault == "counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", events }, { "value", long.MaxValue } });
        var lease = await new MigrationGate(data.Catalog.Database, data.Catalog.Clock, 113).AcquireAsync(113, default); Assert.NotNull(lease);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TemporaryDelegationMigration(data.Catalog.Database).ApplyAsync(lease, default));
        Assert.False(await (await data.Catalog.Database.ListCollectionNamesAsync(new ListCollectionNamesOptions {Filter = new BsonDocument("name", TemporaryDelegationRecords.Heads)})).AnyAsync());
    }
}
