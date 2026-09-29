using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed partial class StockSourceObligationGateTests(MongoFixture fixture)
{
    private static readonly ActorContext Actor = new(42, "https://synthetic.invalid/", "staff:42", 1,
        new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero), FactorAssurance.MultiFactor);
    private static readonly StockSourceObligationExpectation Expected = new(400,
        new(new(100), new(101), new(102), new(103)), 1, 0);
    private static readonly StockSourceObligationDomain[] Domains = Enum.GetValues<StockSourceObligationDomain>();
    private MongoUnitOfWork Unit => new(fixture.Client);

    private async Task<TestData> Data()
    {
        var data = new TestData(fixture.Client, fixture.DatabaseName + "_obl_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await data.Database.CreateCollectionAsync("inventoryPools", new CreateCollectionOptions<BsonDocument>
            {
                Validator = StockSourceSetupSchemas.Validator("inventoryPools"),
                ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error
            });
            var pool = InventoryGraphTests.Pool(); pool["active"] = false;
            await data.Rows("inventoryPools").InsertOneAsync(pool);
            foreach (var name in new[] { "obligationTestHeads", "obligationTestWork", "obligationTestCaller" })
                await data.Database.CreateCollectionAsync(name);
            foreach (var domain in Domains)
                await data.Rows("obligationTestHeads").InsertOneAsync(new BsonDocument
                {
                    { "_id", domain.ToString() }, { "revision", 9007199254740993L }, { "fence", 0L }, { "complete", true }
                });
            return data;
        }
        catch { await data.DisposeAsync(); throw; }
    }

    private static StockSourceObligationGate Gate(TestData data, IEnumerable<IStockSourceObligationReader>? readers = null) =>
        new(data.Database, readers ?? data.Readers());
    private static Task Earlier(TestData data, IClientSessionHandle tx) =>
        data.Rows("obligationTestCaller").InsertOneAsync(tx, new BsonDocument("_id", 1L));
    private static Task<BsonDocument> Pool(TestData data) => data.Rows("inventoryPools").Find(new BsonDocument()).SingleAsync();
    private static async Task AssertRolledBack(TestData data)
    {
        Assert.Equal(0, await data.Rows("obligationTestCaller").CountDocumentsAsync(new BsonDocument()));
        Assert.All(await data.Rows("obligationTestHeads").Find(new BsonDocument()).ToListAsync(), row => Assert.Equal(0, row["fence"].AsInt64));
    }

    [Fact]
    public async Task Complete_zero_work_fences_the_canonical_pool_without_activating_it()
    {
        await using var data = await Data();
        var result = await Unit.ExecuteAsync((tx, ct) => Gate(data).RequireDrainedAsync(tx, Actor, Expected, ct));
        Assert.Equal(Expected with { PoolFence = 1 }, result.Pool);
        Assert.Equal(7, result.Results.Length);
        Assert.All(result.Results, item =>
        {
            Assert.Equal(Expected, item.Pool); Assert.True(item.Complete);
            Assert.Equal(0, item.OpenCount); Assert.Equal(9007199254740993L, item.ProducerRevision);
        });
        Assert.Equal(7, result.Results.Select(row => row.Domain).Distinct().Count());
        var pool = await Pool(data); Assert.False(pool["active"].AsBoolean);
        Assert.Equal(1, pool["revision"].AsInt64); Assert.Equal(1, pool["fence"].AsInt64);
        Assert.All(await data.Rows("obligationTestHeads").Find(new BsonDocument()).ToListAsync(), row => Assert.Equal(1, row["fence"].AsInt64));
        Assert.Equal(0, await data.Rows("obligationTestCaller").CountDocumentsAsync(new BsonDocument()));
    }

    [Theory]
    [InlineData("Orders")] [InlineData("Reservations")]
    [InlineData("Fulfillment")] [InlineData("Procurement")]
    [InlineData("AfterSales")] [InlineData("SourceReconciliation")]
    [InlineData("InventoryOperations")]
    public async Task Missing_owning_domain_never_means_zero_work(string missing)
    {
        await using var data = await Data();
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Gate(data, data.Readers().Where(reader => reader.Domain.ToString() != missing)).RequireDrainedAsync(tx, Actor, Expected));
        Assert.Equal("stock_source_obligation_coverage_missing", error.Code);
        Assert.False(tx.IsInTransaction); await AssertRolledBack(data); Assert.Equal(0, (await Pool(data))["fence"].AsInt64);
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("unknown")] [InlineData("foreign-database")] [InlineData("foreign-client")] [InlineData("null")]
    public async Task Registry_rejects_ambiguous_or_foreign_readers(string fault)
    {
        await using var data = await Data();
        var readers = data.Readers().ToList();
        if (fault == "duplicate") readers.Add(readers[0]);
        if (fault == "unknown") readers.Add(new FixtureReader(data.Database, (StockSourceObligationDomain)99));
        if (fault == "foreign-database") readers[0] = new FixtureReader(fixture.Client.GetDatabase("unrelated_synthetic_database"), Domains[0]);
        if (fault == "foreign-client") readers[0] = new FixtureReader(new MongoClient(fixture.Client.Settings).GetDatabase(data.Database.DatabaseNamespace.DatabaseName), Domains[0]);
        if (fault == "null") readers[0] = null!;
        Assert.Throws<ArgumentException>(() => Gate(data, readers));
    }

    [Theory]
    [InlineData("active")] [InlineData("missing")] [InlineData("malformed")] [InlineData("scope")]
    [InlineData("revision")] [InlineData("fence")] [InlineData("exhausted")] [InlineData("storage-missing")] [InlineData("storage-weakened")]
    public async Task Actual_pool_or_storage_mismatch_aborts_prior_caller_work(string fault)
    {
        await using var data = await Data(); var expected = Expected; var pool = await Pool(data);
        if (fault == "active") pool["active"] = true;
        if (fault == "scope") pool["location"]["channelId"] = 104L;
        if (fault == "revision") pool["revision"] = 2L;
        if (fault == "fence") pool["fence"] = 1L;
        if (fault == "malformed") pool["unreviewed"] = true;
        if (fault == "exhausted") { pool["fence"] = long.MaxValue; expected = expected with { PoolFence = long.MaxValue }; }
        await data.Rows("inventoryPools").ReplaceOneAsync(new BsonDocument("_id", 400L), pool, new ReplaceOptions { BypassDocumentValidation = true });
        if (fault == "missing") await data.Rows("inventoryPools").DeleteOneAsync(new BsonDocument("_id", 400L));
        if (fault == "storage-missing") await data.Database.DropCollectionAsync("inventoryPools");
        if (fault == "storage-weakened") await data.Database.RunCommandAsync<BsonDocument>(
            new BsonDocument { { "collMod", "inventoryPools" }, { "validationLevel", "off" } });
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        await Assert.ThrowsAsync<DomainValidationException>(() => Gate(data).RequireDrainedAsync(tx, Actor, expected));
        Assert.False(tx.IsInTransaction); await AssertRolledBack(data);
        if (fault is not ("missing" or "storage-missing")) Assert.Equal(pool, await Pool(data));
    }

    [Theory]
    [InlineData("null")] [InlineData("domain")] [InlineData("pool")] [InlineData("revision")]
    [InlineData("fence")] [InlineData("location")] [InlineData("producer-revision")] [InlineData("negative")]
    [InlineData("incomplete")] [InlineData("large-count")]
    public async Task Untrusted_or_incomplete_producer_result_is_never_a_drain_certificate(string fault)
    {
        await using var data = await Data();
        var readers = data.Readers().ToArray();
        ((FixtureReader)readers[^1]).Transform = row => fault switch
        {
            "null" => null!,
            "domain" => row with { Domain = StockSourceObligationDomain.Orders },
            "pool" => row with { Pool = row.Pool with { PoolId = 401 } },
            "revision" => row with { Pool = row.Pool with { PoolRevision = 2 } },
            "fence" => row with { Pool = row.Pool with { PoolFence = 1 } },
            "location" => row with { Pool = row.Pool with { Location = row.Pool.Location with { ChannelId = new(104) } } },
            "producer-revision" => row with { ProducerRevision = 0 },
            "negative" => row with { OpenCount = -1 },
            "incomplete" => row with { Complete = false },
            "large-count" => row with { OpenCount = long.MaxValue },
            _ => row
        };
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => Gate(data, readers).RequireDrainedAsync(tx, Actor, Expected));
        Assert.Equal(fault == "large-count" ? "stock_source_obligations_outstanding" : "stock_source_obligation_result_unavailable", error.Code);
        Assert.False(tx.IsInTransaction); await AssertRolledBack(data); Assert.Equal(0, (await Pool(data))["fence"].AsInt64);
    }

    [Theory]
    [InlineData("Orders")] [InlineData("Reservations")]
    [InlineData("Fulfillment")] [InlineData("Procurement")]
    [InlineData("AfterSales")] [InlineData("SourceReconciliation")]
    [InlineData("InventoryOperations")]
    public async Task Work_on_another_previously_mapped_channel_still_blocks_the_physical_pool(string domain)
    {
        await using var data = await Data();
        await data.Rows("obligationTestWork").InsertOneAsync(Work(Enum.Parse<StockSourceObligationDomain>(domain)));
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => Gate(data).RequireDrainedAsync(tx, Actor, Expected));
        Assert.Equal("stock_source_obligations_outstanding", error.Code);
        Assert.False(tx.IsInTransaction); await AssertRolledBack(data);
        Assert.Equal(1, await data.Rows("obligationTestWork").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, (await Pool(data))["fence"].AsInt64);
    }

    [Theory]
    [InlineData("collection")] [InlineData("head")] [InlineData("incomplete")]
    public async Task Installed_reader_without_complete_actual_producer_state_does_not_supply_zero(string fault)
    {
        await using var data = await Data();
        if (fault == "collection") await data.Database.DropCollectionAsync("obligationTestHeads");
        if (fault == "head") await data.Rows("obligationTestHeads").DeleteOneAsync(new BsonDocument("_id", Domains[^1].ToString()));
        if (fault == "incomplete") await data.Rows("obligationTestHeads").UpdateOneAsync(
            new BsonDocument("_id", Domains[^1].ToString()), Builders<BsonDocument>.Update.Set("complete", false));
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        await Assert.ThrowsAsync<DomainValidationException>(() => Gate(data).RequireDrainedAsync(tx, Actor, Expected));
        Assert.False(tx.IsInTransaction); await AssertRolledBack(data);
        Assert.Equal(0, (await Pool(data))["fence"].AsInt64);
    }

    [Theory]
    [InlineData("no-transaction")] [InlineData("foreign-session")] [InlineData("cancelled")]
    public async Task Invalid_transaction_or_cancellation_cannot_complete_the_check(string fault)
    {
        await using var data = await Data();
        var client = fault == "foreign-session" ? new MongoClient(fixture.Client.Settings) : fixture.Client;
        using var tx = await client.StartSessionAsync();
        if (fault != "no-transaction") tx.StartTransaction();
        using var cancellation = new CancellationTokenSource();
        if (fault == "cancelled") cancellation.Cancel();
        if (fault == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gate(data).RequireDrainedAsync(tx, Actor, Expected, cancellation.Token));
        else await Assert.ThrowsAsync<DomainValidationException>(() => Gate(data).RequireDrainedAsync(tx, Actor, Expected));
        Assert.False(tx.IsInTransaction); await AssertRolledBack(data); Assert.Equal(0, (await Pool(data))["fence"].AsInt64);
    }

    [Fact]
    public async Task A_later_owning_command_failure_rolls_back_the_successful_drain_check()
    {
        await using var data = await Data();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Unit.ExecuteAsync<bool>(async (tx, ct) =>
        {
            await Earlier(data, tx); await Gate(data).RequireDrainedAsync(tx, Actor, Expected, ct);
            throw new InvalidOperationException("Synthetic later control acceptance failure");
        }));
        await AssertRolledBack(data); Assert.Equal(0, (await Pool(data))["fence"].AsInt64);
    }

    [Fact]
    public async Task A_prior_success_cannot_be_reused_at_an_old_pool_fence()
    {
        await using var data = await Data();
        await Unit.ExecuteAsync((tx, ct) => Gate(data).RequireDrainedAsync(tx, Actor, Expected, ct));
        await Assert.ThrowsAsync<DomainValidationException>(() => Unit.ExecuteAsync((tx, ct) => Gate(data).RequireDrainedAsync(tx, Actor, Expected, ct)));
        Assert.Equal(1, (await Pool(data))["fence"].AsInt64);
        Assert.All(await data.Rows("obligationTestHeads").Find(new BsonDocument()).ToListAsync(), row => Assert.Equal(1, row["fence"].AsInt64));
    }

    private static BsonDocument Work(StockSourceObligationDomain domain) => new()
    {
        { "_id", ObjectId.GenerateNewId() }, { "poolId", 400L }, { "domain", domain.ToString() },
        { "channelId", 104L }, { "mappingEnabled", false }, { "open", true }
    };

    private sealed class TestData(MongoClient client, string name) : IAsyncDisposable
    {
        internal IMongoDatabase Database { get; } = client.GetDatabase(name);
        internal IMongoCollection<BsonDocument> Rows(string collection) => Database.GetCollection<BsonDocument>(collection);
        internal IEnumerable<IStockSourceObligationReader> Readers() => Domains.Select(domain => new FixtureReader(Database, domain));
        public async ValueTask DisposeAsync() => await client.DropDatabaseAsync(name);
    }

    // Synthetic owning-module implementation: all operational readers remain unregistered in production.
    private sealed class FixtureReader(IMongoDatabase database, StockSourceObligationDomain domain) : IStockSourceObligationReader
    {
        public IMongoDatabase Database { get; } = database;
        public StockSourceObligationDomain Domain { get; } = domain;
        internal Func<StockSourceObligationResult, StockSourceObligationResult>? Transform { get; set; }
        internal Func<Task>? AfterRead { get; set; }
        public async Task<StockSourceObligationResult> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor,
            StockSourceObligationExpectation pool, CancellationToken ct)
        {
            var heads = Database.GetCollection<BsonDocument>("obligationTestHeads");
            var head = await heads.Find(tx, new BsonDocument("_id", Domain.ToString())).SingleOrDefaultAsync(ct);
            if (head is null) throw new DomainValidationException("test_producer_unavailable", "The synthetic producer is not installed");
            var count = await Database.GetCollection<BsonDocument>("obligationTestWork").CountDocumentsAsync(tx,
                new BsonDocument { { "poolId", pool.PoolId }, { "domain", Domain.ToString() }, { "open", true } }, cancellationToken: ct);
            var changed = await heads.UpdateOneAsync(tx, head, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct);
            if (changed.ModifiedCount != 1) throw new InvalidOperationException("Synthetic source fence changed");
            var result = new StockSourceObligationResult(Domain, pool, head["revision"].AsInt64, head["complete"].AsBoolean, count);
            if (AfterRead is not null) await AfterRead();
            return Transform?.Invoke(result) ?? (Transform is null ? result : null!);
        }
    }
}
