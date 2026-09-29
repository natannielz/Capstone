using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Inventory;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceObligationGateTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task A_reader_that_ends_the_transaction_cannot_turn_the_gate_into_an_unfenced_write(bool restart)
    {
        await using var data = await Data();
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        var readers = data.Readers().ToArray();
        ((FixtureReader)readers[^1]).AfterRead = async () => { await tx.AbortTransactionAsync(); if (restart) tx.StartTransaction(); };
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => Gate(data, readers).RequireDrainedAsync(tx, Actor, Expected));
        Assert.Equal("inventory_transaction_required", error.Code);
        Assert.False(tx.IsInTransaction); await AssertRolledBack(data);
        Assert.Equal(0, (await Pool(data))["fence"].AsInt64);
    }

    [Fact]
    public async Task A_concurrent_obligation_insert_on_the_shared_pool_invalidates_the_earlier_empty_read()
    {
        await using var data = await Data();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = data.Readers().ToArray();
        ((FixtureReader)readers[^1]).AfterRead = async () =>
        {
            loaded.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };
        using var older = await fixture.Client.StartSessionAsync(); older.StartTransaction(); await Earlier(data, older);
        var pending = Gate(data, readers).RequireDrainedAsync(older, Actor, Expected);
        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var winner = await fixture.Client.StartSessionAsync(); winner.StartTransaction();
            var pool = await data.Rows("inventoryPools").Find(winner, new BsonDocument("_id", 400L)).SingleAsync();
            var change = await data.Rows("inventoryPools").UpdateOneAsync(winner, pool,
                Builders<BsonDocument>.Update.Inc("fence", 1L));
            Assert.Equal(1, change.ModifiedCount);
            await data.Rows("obligationTestWork").InsertOneAsync(winner, Work(StockSourceObligationDomain.Orders));
            await winner.CommitTransactionAsync();
        }
        finally { release.TrySetResult(); }
        var error = await Assert.ThrowsAnyAsync<MongoException>(() => pending);
        Assert.True(error.HasErrorLabel("TransientTransactionError"));
        Assert.False(older.IsInTransaction); await AssertRolledBack(data);
        Assert.Equal(1, (await Pool(data))["fence"].AsInt64);
        Assert.Equal(1, await data.Rows("obligationTestWork").CountDocumentsAsync(new BsonDocument()));
        var freshError = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Unit.ExecuteAsync((tx, ct) => Gate(data).RequireDrainedAsync(tx, Actor, Expected with { PoolFence = 1 }, ct)));
        Assert.Equal("stock_source_obligations_outstanding", freshError.Code);
    }

    [Fact]
    public async Task Cancellation_after_all_producer_reads_aborts_every_producer_fence()
    {
        await using var data = await Data(); using var cancellation = new CancellationTokenSource();
        var readers = data.Readers().ToArray();
        ((FixtureReader)readers[^1]).AfterRead = () => { cancellation.Cancel(); return Task.CompletedTask; };
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gate(data, readers).RequireDrainedAsync(tx, Actor, Expected, cancellation.Token));
        Assert.False(tx.IsInTransaction); await AssertRolledBack(data); Assert.Equal(0, (await Pool(data))["fence"].AsInt64);
    }
}
