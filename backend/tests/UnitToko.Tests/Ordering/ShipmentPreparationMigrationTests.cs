using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentPreparationMigrationTests(MongoFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private async Task Run(Func<IMongoDatabase, Task> work) {
        var name = "ut_preparation_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV22Async(db, Clock); await work(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonArray> History(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    [Fact]
    public Task Forward_upgrade_preserves_history_and_stronger_rules_and_installs_real_unique_contracts() => Run(async db => {
        var before = await History(db); Assert.Equal(22, before.Count);
        var stronger = new BsonDocument("$and", new BsonArray { ShipmentPreparationSchemas.Validator(ShipmentPreparationBson.Heads), new BsonDocument("revision", new BsonDocument("$lte", 100L)) });
        await db.CreateCollectionAsync(ShipmentPreparationBson.Heads, new CreateCollectionOptions<BsonDocument> { Validator = stronger });
        await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", ShipmentPreparationBson.Heads }, { "value", 9007199254740993L } });
        await new CollectionMigrations(db, Clock).ApplyAsync(); var after = await History(db);
        Assert.Equal(23, after.Count); Assert.Equal(before, new BsonArray(after.Take(22)));
        foreach (var name in ShipmentPreparationSchemas.Collections) await InventoryRepository.RequireStorageAsync(db, name, ShipmentPreparationSchemas.Validator(name), ShipmentPreparationSchemas.Key(name), default);
        var definition = await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", ShipmentPreparationBson.Heads) })).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
        Assert.Equal(9007199254740993L, (await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", ShipmentPreparationBson.Heads)).SingleAsync())["value"].AsInt64);
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(after, await History(db));
        var invalid = await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(ShipmentPreparationBson.Heads).InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } }));
        Assert.Equal(121, invalid.WriteError.Code);
    });
    [Theory] [InlineData("data")] [InlineData("receipt")] [InlineData("outbox")] [InlineData("retention")] [InlineData("counter")] [InlineData("wrong-index")]
    public Task Unaccepted_preparation_or_unsafe_storage_blocks_before_target_ddl(string fault) => Run(async db => {
        if (fault == "data") await db.GetCollection<BsonDocument>(ShipmentPreparationBson.Versions).InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } });
        if (fault == "receipt") await db.GetCollection<BsonDocument>("idempotencyRecords").InsertOneAsync(new BsonDocument("operation", ShipmentPreparationBson.Operation), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "outbox") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument("sourceKey", "fulfillment.preparation.saved:7"), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "retention") await db.GetCollection<BsonDocument>(ShipmentPreparationBson.Versions).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", ShipmentPreparationBson.Heads }, { "value", long.MaxValue } });
        if (fault == "wrong-index") await db.GetCollection<BsonDocument>(ShipmentPreparationBson.Heads).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("suborderId", 1), new CreateIndexOptions { Unique = false }));
        var before = await History(db); var definitions = await (await db.ListCollectionsAsync()).ToListAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, Clock).ApplyAsync());
        Assert.Equal(before, new BsonArray((await History(db)).Take(22)));
        Assert.Equal(definitions.OrderBy(row => row["name"].AsString), (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString));
        Assert.False(await new CollectionMigrations(db, Clock).IsCurrentAsync());
    });
    [Fact]
    public Task Interrupted_actual_collection_install_resumes_under_deployment_lease() => Run(async db => {
        var before = await History(db); using var cancellation = new CancellationTokenSource(); var changed = false;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(value => {
            if (value.CommandName == "create") { changed = true; cancellation.Cancel(); }
        });
        using var client = new MongoClient(settings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(client.GetDatabase(db.DatabaseNamespace.DatabaseName), Clock).ApplyAsync(cancellation.Token));
        Assert.True(changed); Assert.False(await new CollectionMigrations(db, Clock).IsCurrentAsync());
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(before, new BsonArray((await History(db)).Take(22)));
        Assert.True(await new CollectionMigrations(db, Clock).IsCurrentAsync());
    });
}
