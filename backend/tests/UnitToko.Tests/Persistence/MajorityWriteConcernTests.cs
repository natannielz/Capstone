using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Persistence;

[Collection("Mongo")]
public sealed class MajorityWriteConcernTests(MongoFixture fixture)
{
    [Fact]
    public async Task Weak_client_default_cannot_weaken_counter_migration_or_outbox_state_writes()
    {
        var name = "ut_wc_" + fixture.DatabaseName[..8] + Guid.NewGuid().ToString("N");
        var observed = new ConcurrentBag<(string Name, BsonDocument Command)>();
        var settings = MongoClientSettings.FromConnectionString(
            "mongodb://127.0.0.1:27028/?replicaSet=rs0&w=1");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(
            item => observed.Add((item.CommandName, item.Command.DeepClone().AsBsonDocument)));
        var client = new MongoClient(settings);
        var db = client.GetDatabase(name);
        var clock = new SystemClock();
        try
        {
            await new CollectionMigrations(db, clock).ApplyAsync();
            await new IdAllocator(db).NextAsync("orders");
            var outbox = new OutboxStore(db, clock);
            await new MongoUnitOfWork(client).ExecuteAsync(async (session, token) =>
            {
                await outbox.EnqueueAsync(session, "source-1", "test", new BsonDocument("value", 1), token);
                return 0;
            });
            var claim = await outbox.ClaimAsync("worker", TimeSpan.FromMinutes(1));
            Assert.NotNull(claim);
            Assert.True(await outbox.CompleteAsync(claim!));

            AssertMajority(observed, "insert", "schemaMigrations");
            AssertMajority(observed, "update", "schemaMigrations");
            AssertMajority(observed, "findAndModify", "idCounters");
            AssertMajority(observed, "findAndModify", "outboxEvents");
            AssertMajority(observed, "update", "outboxEvents");
        }
        finally { await client.DropDatabaseAsync(name); }
    }

    private static void AssertMajority(IEnumerable<(string Name, BsonDocument Command)> events,
        string commandName, string collection)
    {
        var commands = events.Where(item => item.Name == commandName &&
            item.Command.TryGetValue(commandName == "findAndModify" ? "findAndModify" : commandName,
                out var target) && target.IsString && target.AsString == collection).ToArray();
        Assert.NotEmpty(commands);
        Assert.All(commands, item => Assert.Equal("majority",
            item.Command["writeConcern"].AsBsonDocument["w"].AsString));
    }
}
