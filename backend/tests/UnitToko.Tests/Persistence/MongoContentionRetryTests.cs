using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Persistence;

[Collection("Mongo")]
public sealed class MongoContentionRetryTests(MongoFixture fixture)
{
    [Fact]
    public async Task Diagnostic_only_labelled_error_preserves_terminal_cause_after_four_attempts_and_real_rollback()
    {
        var name = "ut_retry_cause_" + Guid.NewGuid().ToString("N"); var database = fixture.Client.GetDatabase(name);
        var fences = database.GetCollection<BsonDocument>("fences"); var effects = database.GetCollection<BsonDocument>("effects");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try {
            await fences.InsertOneAsync(new BsonDocument { { "_id", 1L }, { "fence", 0L } }, cancellationToken: deadline.Token);
            await effects.InsertOneAsync(new BsonDocument("_id", "setup"), cancellationToken: deadline.Token);
            var causes = new List<MongoException>(); var attempts = 0;
            var failure = await Assert.ThrowsAsync<PersistenceConflictException>(() => new MongoUnitOfWork(fixture.Client).ExecuteAsync<int>(async (session, token) => {
                var attempt = ++attempts;
                await fences.UpdateOneAsync(session, new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token);
                await effects.InsertOneAsync(session, new BsonDocument { { "_id", "partial-" + attempt }, { "attempt", attempt } }, cancellationToken: token);
                // Diagnostic-only synthetic errors after actual transactional writes.
                // This does not replace real contention or transaction-lifetime tests.
                var cause = new MongoException("Synthetic diagnostic cause at attempt " + attempt);
                cause.AddErrorLabel("TransientTransactionError"); causes.Add(cause); throw cause;
            }, deadline.Token));
            Assert.Equal(4, attempts); Assert.Equal(MongoUnitOfWork.MaximumAttempts, causes.Count);
            Assert.Equal("version_conflict", failure.Code); Assert.Equal("Data changed; refresh and try again", failure.Message);
            Assert.Same(causes[^1], failure.InnerException);
            Assert.True(causes[^1].HasErrorLabel("TransientTransactionError"));
            Assert.Equal(0L, (await fences.Find(new BsonDocument("_id", 1L)).SingleAsync(deadline.Token))["fence"].AsInt64);
            Assert.Equal(1L, await effects.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: deadline.Token));
            Assert.Equal("setup", (await effects.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(deadline.Token))["_id"].AsString);
        } finally { await fixture.Client.DropDatabaseAsync(name); }
    }

    [Fact]
    public async Task Competing_source_fence_held_beyond_short_retries_eventually_commits_one_effect()
    {
        var name = "ut_contention_" + Guid.NewGuid().ToString("N"); var database = fixture.Client.GetDatabase(name);
        var fences = database.GetCollection<BsonDocument>("fences"); var effects = database.GetCollection<BsonDocument>("effects");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try {
            await fences.InsertOneAsync(new BsonDocument { { "_id", 1L }, { "fence", 0L } }, cancellationToken: deadline.Token);
            await effects.InsertOneAsync(new BsonDocument("_id", "setup"), cancellationToken: deadline.Token);
            using var first = await fixture.Client.StartSessionAsync(cancellationToken: deadline.Token);
            first.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
            await fences.UpdateOneAsync(first, new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: deadline.Token);
            var conflictObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var attempts = 0;
            var contender = new MongoUnitOfWork(fixture.Client).ExecuteAsync(async (session, token) => {
                var attempt = Interlocked.Increment(ref attempts);
                await effects.InsertOneAsync(session, new BsonDocument { { "_id", "contender" }, { "attempt", attempt } }, cancellationToken: token);
                try { await fences.UpdateOneAsync(session, new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token); }
                catch (MongoException error) when (error.HasErrorLabel("TransientTransactionError")) { conflictObserved.TrySetResult(); throw; }
                return attempt;
            }, deadline.Token);
            try {
                await conflictObserved.Task.WaitAsync(deadline.Token);
                // This reproduces the remaining hold time of a legitimate overlapping source read.
                // The original 15/30/45ms retry schedule exhausted all attempts before release.
                await Task.Delay(TimeSpan.FromMilliseconds(350), deadline.Token);
                await first.CommitTransactionAsync(deadline.Token);
                var committedAttempt = await contender;
                Assert.InRange(attempts, 2, MongoUnitOfWork.MaximumAttempts); Assert.Equal(attempts, committedAttempt);
                var effect = await effects.Find(new BsonDocument("_id", "contender")).SingleAsync(deadline.Token);
                Assert.Equal(committedAttempt, effect["attempt"].AsInt32);
                Assert.Equal(2, await effects.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: deadline.Token));
                Assert.Equal(2L, (await fences.Find(new BsonDocument("_id", 1L)).SingleAsync(deadline.Token))["fence"].AsInt64);
            } finally {
                if (first.IsInTransaction) await first.AbortTransactionAsync(CancellationToken.None);
                if (!contender.IsCompleted) deadline.Cancel();
                try { await contender; } catch (Exception) when (deadline.IsCancellationRequested) { }
            }
        } finally { await fixture.Client.DropDatabaseAsync(name); }
    }

    [Fact]
    public async Task Cancellation_after_real_conflict_stops_retry_and_rolls_back_partial_effect()
    {
        var name = "ut_contention_cancel_" + Guid.NewGuid().ToString("N"); var database = fixture.Client.GetDatabase(name);
        var fences = database.GetCollection<BsonDocument>("fences"); var effects = database.GetCollection<BsonDocument>("effects");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); using var cancelled = new CancellationTokenSource();
        try {
            await fences.InsertOneAsync(new BsonDocument { { "_id", 1L }, { "fence", 0L } }, cancellationToken: deadline.Token);
            await effects.InsertOneAsync(new BsonDocument("_id", "setup"), cancellationToken: deadline.Token);
            using var first = await fixture.Client.StartSessionAsync(cancellationToken: deadline.Token); first.StartTransaction();
            await fences.UpdateOneAsync(first, new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: deadline.Token);
            var conflictObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var attempts = 0;
            var contender = new MongoUnitOfWork(fixture.Client).ExecuteAsync(async (session, token) => {
                Interlocked.Increment(ref attempts);
                await effects.InsertOneAsync(session, new BsonDocument("_id", "cancelled"), cancellationToken: token);
                try { await fences.UpdateOneAsync(session, new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token); }
                catch (MongoException error) when (error.HasErrorLabel("TransientTransactionError")) { conflictObserved.TrySetResult(); throw; }
                return 0;
            }, cancelled.Token);
            try {
                await conflictObserved.Task.WaitAsync(deadline.Token); cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await contender.WaitAsync(deadline.Token));
                Assert.Equal(1, attempts);
                Assert.Null(await effects.Find(new BsonDocument("_id", "cancelled")).SingleOrDefaultAsync(deadline.Token));
            } finally {
                cancelled.Cancel(); if (first.IsInTransaction) await first.AbortTransactionAsync(CancellationToken.None);
                try { await contender; } catch (OperationCanceledException) { }
            }
            Assert.Equal(0L, (await fences.Find(new BsonDocument("_id", 1L)).SingleAsync(deadline.Token))["fence"].AsInt64);
        } finally { await fixture.Client.DropDatabaseAsync(name); }
    }
}
