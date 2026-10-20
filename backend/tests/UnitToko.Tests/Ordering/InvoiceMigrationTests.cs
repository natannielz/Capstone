using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class InvoiceMigrationTests(MongoFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private async Task Run(Func<IMongoDatabase, Task> work) {
        var name = "ut_invoice_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { await LegacyOrderSubmissionTestSchema.ApplyV28Async(db, Clock); await work(db); } finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonArray> History(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public Task Upgrade_preserves_accepted_history_stronger_validation_and_original_partial_indexes() => Run(async db => {
        var history = await History(db); Assert.Equal(28, history.Count);
        var indexes = await (await db.GetCollection<BsonDocument>(InvoiceBson.Invoices).Indexes.ListAsync()).ToListAsync();
        var stronger = new BsonDocument("$and", new BsonArray { InvoiceSchemas.Validator(InvoiceBson.Allocations), new BsonDocument("acceptedBaseUnits", new BsonDocument("$lte", 999999L)) });
        await db.CreateCollectionAsync(InvoiceBson.Allocations, new CreateCollectionOptions<BsonDocument> { Validator = stronger });
        await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", InvoiceBson.Invoices }, { "value", 9007199254740993L } });
        await new CollectionMigrations(db, Clock).ApplyAsync(); var after = await History(db);
        Assert.Equal(history, new BsonArray(after.Take(28))); Assert.Equal(CollectionMigrations.CurrentVersion, after.Count); await InvoiceStorage.RequireAsync(db, default);
        var actual = await (await db.GetCollection<BsonDocument>(InvoiceBson.Invoices).Indexes.ListAsync()).ToListAsync();
        foreach (var index in indexes) Assert.Contains(actual, value => value.Equals(index));
        var definition = await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", InvoiceBson.Allocations) })).SingleAsync();
        Assert.Equal(stronger, definition["options"]["validator"]);
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(after, await History(db));
    });
    [Theory]
    [InlineData("invoice")] [InlineData("allocation")] [InlineData("receipt")] [InlineData("audit")] [InlineData("outbox")] [InlineData("journal")]
    [InlineData("balance")] [InlineData("number")] [InlineData("retention")] [InlineData("counter")] [InlineData("index")]
    public Task Every_unaccepted_source_or_effect_blocks_before_any_target_DDL(string fault) => Run(async db => {
        string? target = fault switch { "invoice" => InvoiceBson.Invoices, "allocation" => InvoiceBson.Allocations, "balance" => "invoiceBalances", _ => null };
        if (target is not null) await db.GetCollection<BsonDocument>(target).InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } });
        if (fault == "receipt") await db.GetCollection<BsonDocument>("idempotencyRecords").InsertOneAsync(new BsonDocument("operation", InvoiceBson.Operation), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "audit") await db.GetCollection<BsonDocument>("auditEvents").InsertOneAsync(new BsonDocument("kind", InvoiceBson.Operation), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "outbox") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument("sourceKey", InvoiceBson.Operation + ":1"), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "journal") await db.GetCollection<BsonDocument>("journalEntries").InsertOneAsync(new BsonDocument("source", new BsonDocument("namespace", "billing.invoice")), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "number") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "invoice-number.100.101.2026" }, { "value", 1L } });
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", InvoiceBson.Allocations }, { "value", long.MaxValue } });
        if (fault == "retention") await db.GetCollection<BsonDocument>(InvoiceBson.Allocations).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("issuedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "index") await db.GetCollection<BsonDocument>(InvoiceBson.Allocations).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(InvoiceSchemas.AllocationKey(), new CreateIndexOptions { Unique = false }));
        var history = await History(db); var definitions = (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, Clock).ApplyAsync());
        Assert.Equal(history, new BsonArray((await History(db)).Take(28)));
        Assert.Equal(definitions, (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString));
    });
    [Fact]
    public Task Interrupted_ddl_resumes_and_keeps_prior_history() => Run(async db => {
        var before = await History(db); using var cancellation = new CancellationTokenSource(); var changed = false;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(value => { if (value.CommandName == "collMod") { changed = true; cancellation.Cancel(); } });
        using var client = new MongoClient(settings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(client.GetDatabase(db.DatabaseNamespace.DatabaseName), Clock).ApplyAsync(cancellation.Token));
        Assert.True(changed); await new CollectionMigrations(db, Clock).ApplyAsync();
        Assert.Equal(before, new BsonArray((await History(db)).Take(28))); await InvoiceStorage.RequireAsync(db, default);
    });
}
