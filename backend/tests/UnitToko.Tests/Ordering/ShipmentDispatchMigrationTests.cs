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
public sealed class ShipmentDispatchMigrationTests(MongoFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private async Task Run(Func<IMongoDatabase, Task> work) {
        var name = "ut_dispatch_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV23Async(db, Clock); await work(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonArray> History(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    [Fact]
    public Task Upgrade_preserves_prior_history_number_uniqueness_stronger_validator_and_existing_order_data() => Run(async db => {
        var before = await History(db); Assert.Equal(23, before.Count);
        var rows = db.GetCollection<BsonDocument>("shipments"); var indexes = await (await rows.Indexes.ListAsync()).ToListAsync();
        var numberIndex = Assert.Single(indexes, index => index["key"].Equals(ShipmentDispatchSchemas.NumberKey()));
        var stronger = new BsonDocument("$and", new BsonArray { ShipmentDispatchSchemas.Validator(), new BsonDocument("orderSequence", new BsonDocument("$lte", 1000L)) });
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "shipments" }, { "validator", stronger } });
        await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "shipments" }, { "value", 9007199254740993L } });
        // An unrelated existing accepted-domain record must not be rewritten by this migration.
        var order = new BsonDocument { { "_id", 99L }, { "marker", "preserve-only-no-dispatch" } };
        await db.GetCollection<BsonDocument>("orders").InsertOneAsync(order, new InsertOneOptions { BypassDocumentValidation = true });
        await new CollectionMigrations(db, Clock).ApplyAsync(); var after = await History(db);
        Assert.Equal(CollectionMigrations.CurrentVersion, after.Count); Assert.Equal(before, new BsonArray(after.Take(23)));
        await ShipmentDispatchStorage.RequireAsync(db, default);
        var definition = await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "shipments") })).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
        Assert.Contains(numberIndex, await (await rows.Indexes.ListAsync()).ToListAsync());
        Assert.Equal(order, await db.GetCollection<BsonDocument>("orders").Find(new BsonDocument("_id", 99L)).SingleAsync());
        Assert.Equal(9007199254740993L, (await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", "shipments")).SingleAsync())["value"].AsInt64);
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(after, await History(db));
        Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => rows.InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } }))).WriteError.Code);
    });
    [Theory]
    [InlineData("data")] [InlineData("receipt")] [InlineData("audit")] [InlineData("movement")] [InlineData("outbox")]
    [InlineData("retention")] [InlineData("counter")] [InlineData("number-counter")] [InlineData("wrong-index")]
    public Task Unsafe_data_or_orphan_effects_block_before_changing_the_shipment_collection(string fault) => Run(async db => {
        if (fault == "data") await db.GetCollection<BsonDocument>("shipments").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } });
        if (fault == "receipt") await db.GetCollection<BsonDocument>("idempotencyRecords").InsertOneAsync(new BsonDocument("operation", ShipmentDispatchBson.Operation), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "audit") await db.GetCollection<BsonDocument>("auditEvents").InsertOneAsync(new BsonDocument("kind", "fulfillment.shipment.dispatched"), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "movement") await db.GetCollection<BsonDocument>("movements").InsertOneAsync(new BsonDocument("sourceEvent", "fulfillment.shipment:7:dispatch:400"), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "outbox") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument("sourceKey", "fulfillment.shipment.dispatched:7"), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "retention") await db.GetCollection<BsonDocument>("shipments").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "shipments" }, { "value", long.MaxValue } });
        if (fault == "number-counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "shipment-number.100.103.2026" }, { "value", 1L } });
        if (fault == "wrong-index") await db.GetCollection<BsonDocument>("shipments").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(ShipmentDispatchSchemas.Key(), new CreateIndexOptions { Unique = false }));
        var before = await History(db); var definitions = await (await db.ListCollectionsAsync()).ToListAsync();
        var indexes = await (await db.GetCollection<BsonDocument>("shipments").Indexes.ListAsync()).ToListAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, Clock).ApplyAsync());
        Assert.Equal(before, new BsonArray((await History(db)).Take(23)));
        Assert.Equal(definitions.OrderBy(row => row["name"].AsString), (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString));
        Assert.Equal(indexes, await (await db.GetCollection<BsonDocument>("shipments").Indexes.ListAsync()).ToListAsync());
        Assert.False(await new CollectionMigrations(db, Clock).IsCurrentAsync());
    });
    [Fact]
    public Task Interrupted_collection_install_resumes_without_replaying_prior_migrations() => Run(async db => {
        var before = await History(db); using var cancellation = new CancellationTokenSource(); var changed = false;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(value => {
            if (value.CommandName == "collMod") { changed = true; cancellation.Cancel(); }
        });
        using var client = new MongoClient(settings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(client.GetDatabase(db.DatabaseNamespace.DatabaseName), Clock).ApplyAsync(cancellation.Token));
        Assert.True(changed); Assert.False(await new CollectionMigrations(db, Clock).IsCurrentAsync());
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(before, new BsonArray((await History(db)).Take(23)));
        Assert.True(await new CollectionMigrations(db, Clock).IsCurrentAsync());
    });
}
