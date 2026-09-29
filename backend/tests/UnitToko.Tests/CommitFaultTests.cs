using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests;

// failCommand is scoped to a unique client appName. The Mongo collection serializes
// these tests because the server owns one failCommand configuration at a time.
[Collection("Mongo")]
public sealed class CommitFaultTests(MongoFixture fixture)
{
    [Fact]
    public async Task Ambiguous_commit_retries_commit_without_repeating_business_callback()
    {
        await WithFaultDatabase(async (client, database, appName, commits) =>
        {
            var clock = new SystemClock();
            var store = new IdempotencyStore(database, new MongoUnitOfWork(client), clock);
            var outbox = new OutboxStore(database, clock);
            var calls = 0;
            var scope = new IdempotencyScope("123", "fault.order", "recoverable", new string('a', 64));
            await EnableCommitAmbiguity(appName, new BsonDocument("times", 2));
            var result = await store.ExecuteAsync(scope, async (session, token) =>
            {
                calls++;
                await database.GetCollection<BsonDocument>("fixtureBusinessRecords").InsertOneAsync(session,
                    new BsonDocument { { "_id", 101L }, { "schemaVersion", 1 } }, cancellationToken: token);
                await outbox.EnqueueAsync(session, "order:101", "order.created", new BsonDocument("id", 101L), token);
                return new BsonDocument("id", "101");
            });
            Assert.Equal("101", result["id"].AsString);
            Assert.Equal(1, calls);
            Assert.True(commits() >= 3, "The server must actually fail commit replies before a successful retry.");
            await AssertSingleEffect(database);
        });
    }

    [Fact]
    public async Task Exhausted_commit_replies_return_uncertainty_then_same_key_recovers_committed_result()
    {
        await WithFaultDatabase(async (client, database, appName, commits) =>
        {
            var clock = new SystemClock();
            var store = new IdempotencyStore(database, new MongoUnitOfWork(client), clock);
            var outbox = new OutboxStore(database, clock);
            var scope = new IdempotencyScope("123", "fault.order", "uncertain", new string('b', 64));
            var calls = 0;
            async Task<BsonDocument> Work(IClientSessionHandle session, CancellationToken token)
            {
                calls++;
                await database.GetCollection<BsonDocument>("fixtureBusinessRecords").InsertOneAsync(session,
                    new BsonDocument { { "_id", 101L }, { "schemaVersion", 1 } }, cancellationToken: token);
                await outbox.EnqueueAsync(session, "order:101", "order.created", new BsonDocument("id", 101L), token);
                return new BsonDocument("id", "101");
            }

            await EnableCommitAmbiguity(appName, "alwaysOn");
            var error = await Assert.ThrowsAsync<UncertainCommitException>(() => store.ExecuteAsync(scope, Work));
            Assert.Contains("same idempotency key", error.Message);
            Assert.True(commits() >= MongoUnitOfWork.MaximumAttempts);
            // writeConcernError changes the reply after the server executes the command.
            // An independent client proves it committed despite the caller's uncertainty.
            await AssertSingleEffect(fixture.Client.GetDatabase(database.DatabaseNamespace.DatabaseName));
            await DisableFault();
            var replay = await store.ExecuteAsync(scope, Work);
            Assert.Equal("101", replay["id"].AsString);
            Assert.Equal(1, calls);
            await AssertSingleEffect(database);
        });
    }

    [Fact]
    public async Task Cancellation_after_an_uncertain_commit_reply_preserves_same_key_recovery()
    {
        using var cancellation = new CancellationTokenSource();
        await WithFaultDatabase(async (client, database, appName, _) =>
        {
            var clock = new SystemClock();
            var store = new IdempotencyStore(database, new MongoUnitOfWork(client), clock);
            var outbox = new OutboxStore(database, clock);
            var scope = new IdempotencyScope("123", "fault.order", "cancelled", new string('c', 64));
            var calls = 0;
            async Task<BsonDocument> Work(IClientSessionHandle session, CancellationToken token)
            {
                calls++;
                await database.GetCollection<BsonDocument>("fixtureBusinessRecords").InsertOneAsync(session,
                    new BsonDocument { { "_id", 101L }, { "schemaVersion", 1 } }, cancellationToken: token);
                await outbox.EnqueueAsync(session, "order:101", "order.created", new BsonDocument("id", 101L), token);
                return new BsonDocument("id", "101");
            }
            await EnableCommitAmbiguity(appName, "alwaysOn");
            var error = await Assert.ThrowsAsync<UncertainCommitException>(() =>
                store.ExecuteAsync(scope, Work, cancellation.Token));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Contains("same idempotency key", error.Message);
            await DisableFault();
            var replay = await store.ExecuteAsync(scope, Work);
            Assert.Equal("101", replay["id"].AsString);
            Assert.Equal(1, calls);
            await AssertSingleEffect(database);
        }, () => cancellation.Cancel());
    }

    [Fact]
    public async Task Transient_read_failure_aborts_partial_writes_and_replays_whole_transaction_once()
    {
        await WithFaultDatabase(async (client, database, appName, _) =>
        {
            var calls = 0;
            var orders = database.GetCollection<BsonDocument>("fixtureBusinessRecords");
            var outbox = new OutboxStore(database, new SystemClock());
            await fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "configureFailPoint", "failCommand" }, { "mode", new BsonDocument("times", 1) },
                { "data", new BsonDocument
                    {
                        { "appName", appName }, { "failCommands", new BsonArray { "find" } },
                        { "errorCode", 112 }, { "errorLabels", new BsonArray { "TransientTransactionError" } }
                    }
                }
            });
            await new MongoUnitOfWork(client).ExecuteAsync(async (session, token) =>
            {
                calls++;
                await orders.InsertOneAsync(session, new BsonDocument
                    { { "_id", 101L }, { "schemaVersion", 1 } }, cancellationToken: token);
                await orders.Find(session, new BsonDocument("_id", 101L)).FirstAsync(token);
                await outbox.EnqueueAsync(session, "order:101", "order.created", new BsonDocument("id", 101L), token);
                return 0;
            });
            Assert.Equal(2, calls);
            Assert.Equal(1, await orders.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            Assert.Equal(1, await database.GetCollection<BsonDocument>("outboxEvents")
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        });
    }

    private async Task WithFaultDatabase(Func<MongoClient, IMongoDatabase, string, Func<int>, Task> test,
        Action? afterCommitReply = null)
    {
        var appName = "unittoko-fault-" + Guid.NewGuid().ToString("N");
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ApplicationName = appName;
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        var commitCommands = 0;
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.CommandName == "commitTransaction") Interlocked.Increment(ref commitCommands);
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (command.CommandName == "commitTransaction") afterCommitReply?.Invoke();
            });
        };
        var client = new MongoClient(settings);
        var database = client.GetDatabase(fixture.DatabaseName + "_fault");
        try
        {
            await new CollectionMigrations(database, new SystemClock()).ApplyAsync();
            await test(client, database, appName, () => Volatile.Read(ref commitCommands));
        }
        finally
        {
            try { await DisableFault(); }
            finally { await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName); }
        }
    }

    private Task<BsonDocument> EnableCommitAmbiguity(string appName, BsonValue mode) =>
        fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "configureFailPoint", "failCommand" }, { "mode", mode },
            { "data", new BsonDocument
                {
                    { "appName", appName }, { "failCommands", new BsonArray { "commitTransaction" } },
                    { "writeConcernError", new BsonDocument { { "code", 64 }, { "errmsg", "injected uncertain commit reply" } } },
                    { "errorLabels", new BsonArray { "UnknownTransactionCommitResult" } }
                }
            }
        });

    private Task<BsonDocument> DisableFault() => fixture.Client.GetDatabase("admin")
        .RunCommandAsync<BsonDocument>(new BsonDocument { { "configureFailPoint", "failCommand" }, { "mode", "off" } });

    private static async Task AssertSingleEffect(IMongoDatabase database)
    {
        foreach (var name in new[] { "fixtureBusinessRecords", "idempotencyRecords", "outboxEvents" })
            Assert.Equal(1, await database.GetCollection<BsonDocument>(name)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }
}
