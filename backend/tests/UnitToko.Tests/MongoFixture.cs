using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Tests;

public sealed class MongoFixture : IAsyncLifetime
{
    public string DatabaseName { get; } = "unittoko_test_" + Guid.NewGuid().ToString("N");
    public MongoClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        const string host = "127.0.0.1:27028";
        var uri = Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ??
            $"mongodb://{host}/?replicaSet=rs0";
        var parsed = new MongoUrl(uri);
        var servers = parsed.Servers.ToArray();
        if (servers.Length != 1 || servers[0].Host != "127.0.0.1" ||
            servers[0].Port != 27028 || parsed.DatabaseName is not null)
            throw new InvalidOperationException("Integration tests only permit local 127.0.0.1:27028 without a database in the URI.");
        var direct = new MongoClient($"mongodb://{host}/?directConnection=true&serverSelectionTimeoutMS=2000");
        var admin = direct.GetDatabase("admin");
        try
        {
            await admin.RunCommandAsync<BsonDocument>(new BsonDocument("replSetInitiate",
                new BsonDocument { { "_id", "rs0" }, { "members", new BsonArray
                {
                    new BsonDocument { { "_id", 0 }, { "host", host } }
                } } }));
        }
        catch (MongoCommandException exception) when (exception.Code == 23) { }

        Client = new MongoClient(uri);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var hello = await Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                    new BsonDocument("hello", 1), cancellationToken: timeout.Token);
                if (hello.TryGetValue("isWritablePrimary", out var primary) && primary.ToBoolean() &&
                    hello.TryGetValue("setName", out var set) && set.AsString == "rs0")
                    return;
            }
            catch (Exception) when (!timeout.IsCancellationRequested) { }
            await Task.Delay(500, timeout.Token);
        }
        throw new InvalidOperationException("Local MongoDB replica set rs0 did not become primary.");
    }

    public async Task DisposeAsync()
    {
        if (Client is not null)
            await Client.DropDatabaseAsync(DatabaseName);
    }
}

[CollectionDefinition("Mongo")]
public sealed class MongoCollection : ICollectionFixture<MongoFixture> { }
