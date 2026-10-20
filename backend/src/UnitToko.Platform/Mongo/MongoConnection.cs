using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo;

public sealed class MongoConnection
{
    public MongoConnection(string uri, string database)
    {
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        // Repeated reads inside one transaction attempt may be reused until that transaction writes (TransactionReadMemo).
        settings.ClusterConfigurator = TransactionReadMemo.Track;
        Client = new MongoClient(settings);
        Database = Client.GetDatabase(database);
    }

    public MongoClient Client { get; }
    public IMongoDatabase Database { get; }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        var hello = await Database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: cancellationToken);
        return hello.TryGetValue("isWritablePrimary", out var primary) && primary.ToBoolean() &&
               hello.TryGetValue("setName", out var set) && set.IsString;
    }
}
