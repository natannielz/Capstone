using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Mongo;

// A transaction reads one snapshot, so a repeated read inside it returns the same answer until the transaction itself
// writes. The memo may reuse a result only in that window: same session, same transaction attempt, no write since.
[Collection("Mongo")]
public sealed class TransactionReadMemoTests(MongoFixture fixture)
{
    private IMongoCollection<BsonDocument> Rows => fixture.Client.GetDatabase(fixture.DatabaseName).GetCollection<BsonDocument>("memoRows");

    [Fact]
    public async Task A_repeated_read_in_one_transaction_is_reused_and_returned_as_a_copy()
    {
        await Rows.InsertOneAsync(new BsonDocument { { "_id", 1 }, { "value", "first" } });
        using var session = await fixture.Client.StartSessionAsync(); session.StartTransaction();
        var reads = 0;
        Task<BsonDocument> Read() { reads++; return Rows.Find(session, new BsonDocument("_id", 1)).SingleAsync(); }
        var first = await TransactionReadMemo.GetOrAddAsync(session, "row:1", Read, row => row.DeepClone().AsBsonDocument);
        first["value"] = "changed by the caller";
        var second = await TransactionReadMemo.GetOrAddAsync(session, "row:1", Read, row => row.DeepClone().AsBsonDocument);
        Assert.Equal(1, reads); Assert.Equal("first", second["value"].AsString);
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task Any_write_in_the_transaction_ends_reuse()
    {
        await Rows.InsertOneAsync(new BsonDocument { { "_id", 2 }, { "value", "before" } });
        using var session = await fixture.Client.StartSessionAsync(); session.StartTransaction();
        var reads = 0;
        Task<BsonDocument> Read() { reads++; return Rows.Find(session, new BsonDocument("_id", 2)).SingleAsync(); }
        await TransactionReadMemo.GetOrAddAsync(session, "row:2", Read, row => row.DeepClone().AsBsonDocument);
        await Rows.UpdateOneAsync(session, new BsonDocument("_id", 2), new BsonDocument("$set", new BsonDocument("value", "after")));
        var after = await TransactionReadMemo.GetOrAddAsync(session, "row:2", Read, row => row.DeepClone().AsBsonDocument);
        Assert.Equal(2, reads); Assert.Equal("after", after["value"].AsString);
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task A_new_transaction_attempt_and_a_read_outside_a_transaction_never_reuse()
    {
        using var session = await fixture.Client.StartSessionAsync();
        var reads = 0;
        Task<int> Read() { reads++; return Task.FromResult(reads); }
        session.StartTransaction();
        await TransactionReadMemo.GetOrAddAsync(session, "count", Read);
        await session.AbortTransactionAsync();
        session.StartTransaction();
        Assert.Equal(2, await TransactionReadMemo.GetOrAddAsync(session, "count", Read));
        await session.AbortTransactionAsync();
        Assert.Equal(3, await TransactionReadMemo.GetOrAddAsync(session, "count", Read));
        Assert.Equal(4, await TransactionReadMemo.GetOrAddAsync(session, "count", Read));
    }

    [Fact]
    public async Task A_client_whose_writes_are_not_tracked_never_reuses()
    {
        // A separate client without the write tracker: the memo cannot tell whether a write happened, so it never reuses.
        using var untracked = new MongoClient(MongoClientSettings.FromConnectionString(
            Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0"));
        using var session = await untracked.StartSessionAsync(); session.StartTransaction();
        await untracked.GetDatabase(fixture.DatabaseName).GetCollection<BsonDocument>("memoRows").Find(session, new BsonDocument()).AnyAsync();
        var reads = 0;
        Task<int> Read() { reads++; return Task.FromResult(reads); }
        await TransactionReadMemo.GetOrAddAsync(session, "count", Read);
        Assert.Equal(2, await TransactionReadMemo.GetOrAddAsync(session, "count", Read));
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task A_failed_read_is_not_remembered()
    {
        using var session = await fixture.Client.StartSessionAsync(); session.StartTransaction();
        await Rows.Find(session, new BsonDocument()).AnyAsync();
        var reads = 0;
        Task<int> Fail() { reads++; throw new InvalidOperationException("refused"); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => TransactionReadMemo.GetOrAddAsync(session, "refused", Fail));
        await Assert.ThrowsAsync<InvalidOperationException>(() => TransactionReadMemo.GetOrAddAsync(session, "refused", Fail));
        Assert.Equal(2, reads);
        await session.AbortTransactionAsync();
    }
}
