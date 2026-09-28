using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Persistence;

[Collection("Mongo")]
public sealed class DisposedTransactionCleanupTests(MongoFixture fixture)
{
    [Fact]
    public async Task Already_disposed_caller_handle_does_not_replace_the_original_failure()
    {
        var rows = fixture.Client.GetDatabase(fixture.DatabaseName).GetCollection<BsonDocument>("cleanup_original");
        await rows.InsertOneAsync(new BsonDocument { { "_id", 1L }, { "value", 0 } });
        var original = new InvalidDataException("synthetic inner boundary failure");
        var caught = await Assert.ThrowsAsync<InvalidDataException>(() => new MongoUnitOfWork(fixture.Client).ExecuteAsync<int>(async (tx, ct) =>
        {
            await rows.UpdateOneAsync(tx, new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Inc("value", 1), cancellationToken: ct);
            // Reproduce the fail-closed inner-boundary postcondition: it invalidated its caller handle.
            tx.Dispose(); throw original;
        }));
        Assert.Same(original, caught);
        Assert.Equal(0, (await rows.Find(new BsonDocument("_id", 1L)).SingleAsync())["value"].AsInt32);
    }

    [Fact]
    public async Task Disposed_failed_attempt_preserves_transient_retry_and_commits_one_effect()
    {
        var rows = fixture.Client.GetDatabase(fixture.DatabaseName).GetCollection<BsonDocument>("cleanup_retry");
        await rows.InsertOneAsync(new BsonDocument { { "_id", 1L }, { "value", 0 } });
        var original = new MongoException("synthetic transient inner failure"); original.AddErrorLabel("TransientTransactionError");
        var attempts = 0;
        var result = await new MongoUnitOfWork(fixture.Client).ExecuteAsync(async (tx, ct) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            await rows.UpdateOneAsync(tx, new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Inc("value", 1), cancellationToken: ct);
            if (attempt == 1) { tx.Dispose(); throw original; }
            return attempt;
        });
        Assert.Equal(2, result); Assert.Equal(2, attempts);
        Assert.Equal(1, (await rows.Find(new BsonDocument("_id", 1L)).SingleAsync())["value"].AsInt32);
    }
}
