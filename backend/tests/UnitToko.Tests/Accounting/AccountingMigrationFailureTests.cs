using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Accounting;
[Collection("Mongo")]
public sealed class AccountingMigrationFailureTests(MongoFixture fixture)
{
    [Theory]
    [InlineData("partial")]
    [InlineData("nonunique")]
    [InlineData("ttl")]
    [InlineData("collation")]
    public async Task Incompatible_indexes_deny_without_mutating_book_validator(string kind)
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        var journals = data.DB.GetCollection<BsonDocument>("journalEntries");
        if (kind == "ttl")
            await journals.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("createdAt", 1), new CreateIndexOptions { Name = "unexpected_ttl", ExpireAfter = TimeSpan.FromDays(1) }));
        else
        {
            await journals.Indexes.DropOneAsync("uq_sourceEvent");
            var options = new CreateIndexOptions<BsonDocument>
            {
                Name = "uq_sourceEvent",
                Unique = kind != "nonunique"
            };
            if (kind == "partial")
                options.PartialFilterExpression = new BsonDocument("sourceEvent", new BsonDocument("$type", "string"));
            if (kind == "collation")
                options.Collation = new Collation("en");
            await journals.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("sourceEvent", 1), options));
        }

        await AccountingMigrationReviewTests.ResetMigrationAsync(data.DB);
        var before = await BooksDefinition(data.DB);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.DB, data.Access.Clock).ApplyAsync());
        Assert.Equal(before, await BooksDefinition(data.DB));
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("exhausted")]
    [InlineData("type")]
    public async Task Invalid_allocator_state_is_never_silently_repaired(string kind)
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        BsonValue invalid = kind == "negative" ? new BsonInt64(-1) : kind == "exhausted" ? new BsonInt64(long.MaxValue) : new BsonDouble(1);
        await data.DB.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "journalEntries" }, { "value", invalid } }, new InsertOneOptions { BypassDocumentValidation = true });
        await AccountingMigrationReviewTests.ResetMigrationAsync(data.DB);
        var before = await BooksDefinition(data.DB);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.DB, data.Access.Clock).ApplyAsync());
        Assert.Equal(before, await BooksDefinition(data.DB));
        Assert.Equal(invalid, (await data.DB.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", "journalEntries")).SingleAsync())["value"]);
    }

    [Fact]
    public async Task Interrupted_index_creation_resumes_after_partial_ddl_and_counter_progress()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        await data.CommandAsync("first");
        await data.DB.GetCollection<BsonDocument>("journalEntries").Indexes.DropOneAsync("ix_book_date_id");
        await data.DB.GetCollection<BsonDocument>("idCounters").UpdateOneAsync(new BsonDocument("_id", "journalEntries"), Builders<BsonDocument>.Update.Set("value", 0L));
        await AccountingMigrationReviewTests.ResetMigrationAsync(data.DB);
        var settings = fixture.Client.Settings.Clone();
        settings.ApplicationName = "accounting-migration-" + Guid.NewGuid().ToString("N");
        var db = new MongoClient(settings).GetDatabase(data.DB.DatabaseNamespace.DatabaseName);
        try
        {
            await fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument { { "configureFailPoint", "failCommand" }, { "mode", new BsonDocument("times", 1) }, { "data", new BsonDocument { { "appName", settings.ApplicationName }, { "failCommands", new BsonArray { "createIndexes" } }, { "errorCode", 11600 } } } });
            await Assert.ThrowsAsync<MongoNodeIsRecoveringException>(() => new CollectionMigrations(db, data.Access.Clock).ApplyAsync());
        }
        finally
        {
            await fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument { { "configureFailPoint", "failCommand" }, { "mode", "off" } });
        }

        var migrations = new CollectionMigrations(data.DB, data.Access.Clock);
        Assert.False(await migrations.IsCurrentAsync());
        // Simulate the elapsed lease after a failed node prevented best-effort release.
        data.Access.Clock.UtcNow += TimeSpan.FromMinutes(10);
        await migrations.ApplyAsync();
        Assert.True(await migrations.IsCurrentAsync());
        Assert.Equal(2L, (await new IdAllocator(data.DB).NextAsync("journalEntries")).Value);
        Assert.Equal(1, await data.Count("journalEntries"));
        await migrations.ApplyAsync();
    }

    private static async Task<BsonDocument> BooksDefinition(IMongoDatabase db) => await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "accountingBooks") })).SingleAsync();
}
