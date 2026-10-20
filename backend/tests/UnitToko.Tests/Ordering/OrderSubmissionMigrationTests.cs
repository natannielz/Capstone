using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderSubmissionMigrationTests(MongoFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private static Task<BsonArray> History(IMongoDatabase db) => ReadRows(db, "schemaMigrations");
    private static async Task<BsonArray> ReadRows(IMongoDatabase db, string name) => new(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private async Task Run(Func<IMongoDatabase, Task> work) {
        var name = "ut_order_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV21Async(db, Clock); await work(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public Task Forward_storage_preserves_predecessors_and_stronger_validator_and_is_idempotent() => Run(async db => {
        var before = await History(db); Assert.Equal(21, before.Count);
        var stronger = new BsonDocument("$and", new BsonArray { OrderSubmissionSchemas.Validator("orders"), new BsonDocument("branchId", 11L) });
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "orders" }, { "validator", stronger } });
        await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "orders" }, { "value", 9007199254740993L } });
        await db.GetCollection<BsonDocument>("purchaseOrders").InsertOneAsync(new BsonDocument { { "_id", 18L }, { "schemaVersion", 1 }, { "preserved", "unrelated" } });
        var unrelated = await ReadRows(db, "purchaseOrders");
        Assert.False(await new CollectionMigrations(db, Clock).IsCurrentAsync());
        await new CollectionMigrations(db, Clock).ApplyAsync(); var after = await History(db);
        Assert.Equal(CollectionMigrations.CurrentVersion, after.Count); Assert.Equal(before, new BsonArray(after.Take(21)));
        foreach (var name in OrderSubmissionSchemas.Collections) await InventoryRepository.RequireStorageAsync(db, name, OrderLineageSchemas.OrderValidator(name), OrderLineageSchemas.OrderKey(name), default);
        var definition = await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "orders") })).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
        Assert.Equal(unrelated, await ReadRows(db, "purchaseOrders"));
        Assert.Equal(9007199254740993L, (await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", "orders")).SingleAsync())["value"].AsInt64);
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(after, await History(db));
        Assert.True(await new CollectionMigrations(db, Clock).IsCurrentAsync());
        var invalid = await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>("orders").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } }));
        Assert.Equal(121, invalid.WriteError.Code);
    });
    [Theory]
    [InlineData("legacy-data")] [InlineData("reservation-data")] [InlineData("receipt")]
    [InlineData("audit")] [InlineData("outbox")] [InlineData("movement")] [InlineData("movement-event")]
    [InlineData("retention")] [InlineData("uniqueness")] [InlineData("counter")]
    [InlineData("partial-index")] [InlineData("wrong-required-index")]
    public Task Unaccepted_business_evidence_or_unsafe_storage_blocks_before_any_target_ddl(string fault) => Run(async db => {
        var orders = db.GetCollection<BsonDocument>("orders");
        if (fault == "legacy-data") await orders.InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } });
        if (fault == "reservation-data") await db.GetCollection<BsonDocument>("reservations").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } });
        // Deliberate corrupt orphan evidence: no prior authorized order writer existed.
        if (fault == "receipt") await db.GetCollection<BsonDocument>("idempotencyRecords").InsertOneAsync(new BsonDocument { { "operation", "ordering.order.submit" } }, new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "audit") await db.GetCollection<BsonDocument>("auditEvents").InsertOneAsync(new BsonDocument { { "operation", "ordering.order.cancel" } }, new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "outbox") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument { { "kind", "ordering.order.submitted" } }, new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "movement") await db.GetCollection<BsonDocument>("movements").InsertOneAsync(new BsonDocument { { "source", new BsonDocument("namespace", "ordering.order") } }, new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "movement-event") await db.GetCollection<BsonDocument>("movements").InsertOneAsync(new BsonDocument { { "sourceEvent", "ordering.decision:5:release:6" } }, new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "retention") await orders.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("expiresAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromHours(1) }));
        if (fault == "uniqueness") await orders.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("branchId", 1), new CreateIndexOptions { Unique = true }));
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "orders" }, { "value", long.MaxValue } });
        if (fault == "partial-index") await orders.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("createdBy", 1), new CreateIndexOptions<BsonDocument> { PartialFilterExpression = new BsonDocument("active", true) }));
        if (fault == "wrong-required-index") await orders.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("branchId", 1), new CreateIndexOptions { Name = "ix_order_request" }));
        var before = await History(db); var definitions = await (await db.ListCollectionsAsync()).ToListAsync();
        var business = new Dictionary<string, BsonArray>(); foreach (var name in new[] { "orders", "reservations", "auditEvents", "idempotencyRecords", "outboxEvents", "movements" }) business[name] = await ReadRows(db, name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, Clock).ApplyAsync());
        Assert.Equal(before, new BsonArray((await History(db)).Take(21)));
        Assert.Equal(definitions.OrderBy(row => row["name"].AsString), (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString));
        foreach (var pair in business) Assert.Equal(pair.Value, await ReadRows(db, pair.Key));
        Assert.False(await new CollectionMigrations(db, Clock).IsCurrentAsync());
    });
    [Fact]
    public Task Interrupted_real_ddl_resumes_without_rewriting_predecessor_history() => Run(async db => {
        var before = await History(db); using var cancellation = new CancellationTokenSource();
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0"); var changed = false;
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(item => { if (item.CommandName == "collMod") { changed = true; cancellation.Cancel(); } });
        var observed = new MongoClient(settings).GetDatabase(db.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(observed, Clock).ApplyAsync(cancellation.Token)); Assert.True(changed);
        Assert.False(await new CollectionMigrations(db, Clock).IsCurrentAsync());
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(before, new BsonArray((await History(db)).Take(21)));
        Assert.True(await new CollectionMigrations(db, Clock).IsCurrentAsync());
    });
    [Fact]
    public Task Existing_deployment_lease_prevents_a_competing_upgrade() => Run(async db => {
        var lease = await new MigrationGate(db, Clock, 22).AcquireAsync(22, default); Assert.NotNull(lease);
        try {
            await Assert.ThrowsAsync<PersistenceConflictException>(() => new CollectionMigrations(db, Clock).ApplyAsync());
            Assert.False(await (await db.ListCollectionNamesAsync(new ListCollectionNamesOptions { Filter = new BsonDocument("name", "orderDecisions") })).AnyAsync());
        } finally { await lease.ReleaseAsync(); }
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.True(await new CollectionMigrations(db, Clock).IsCurrentAsync());
    });
}
