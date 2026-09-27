using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Persistence;

[Collection("Mongo")]
public sealed class AbortCleanupTests(MongoFixture fixture)
{
    [Fact]
    public async Task Blocked_abort_does_not_hide_original_error_or_commit_partial_write()
    {
        var appName = "unittoko-abort-" + Guid.NewGuid().ToString("N");
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ApplicationName = appName;
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        var client = new MongoClient(settings);
        var name = "ut_abort_" + Guid.NewGuid().ToString("N");
        var database = client.GetDatabase(name);
        var admin = fixture.Client.GetDatabase("admin");
        try
        {
            await database.GetCollection<BsonDocument>("orders").InsertOneAsync(new BsonDocument("_id", "setup"));
            var enabled = await admin.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "configureFailPoint", "failCommand" }, { "mode", new BsonDocument("times", 1) },
                { "data", new BsonDocument
                    {
                        { "appName", appName }, { "failCommands", new BsonArray { "abortTransaction" } },
                        { "blockConnection", true }, { "blockTimeMS", 10_000 }, { "errorCode", 11600 }
                    }
                }
            });
            var clock = Stopwatch.StartNew();
            var failure = await Assert.ThrowsAsync<DomainValidationException>(() =>
                new MongoUnitOfWork(client).ExecuteAsync<int>(async (session, token) =>
                {
                    await database.GetCollection<BsonDocument>("orders").InsertOneAsync(session,
                        new BsonDocument("_id", "partial"), cancellationToken: token);
                    throw new DomainValidationException("injected", "Known business rejection");
                }));
            clock.Stop();
            Assert.Equal("injected", failure.Code);
            await admin.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "waitForFailPoint", "failCommand" },
                { "timesEntered", enabled.GetValue("count", 0).ToInt32() + 1 },
                { "maxTimeMS", 1000 }
            });
            Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(4),
                $"Abort failpoint did not exercise the cleanup timeout: {clock.Elapsed}");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(9),
                $"Abort cleanup took {clock.Elapsed}; the original failure was masked or delayed");
            var independent = fixture.Client.GetDatabase(name).GetCollection<BsonDocument>("orders");
            Assert.Null(await independent.Find(new BsonDocument("_id", "partial")).FirstOrDefaultAsync());
            Assert.Equal(1, await independent.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        }
        finally
        {
            try { await admin.RunCommandAsync<BsonDocument>(new BsonDocument
                { { "configureFailPoint", "failCommand" }, { "mode", "off" } }); }
            finally { await client.DropDatabaseAsync(name); }
        }
    }
}
