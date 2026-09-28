using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Persistence;

[Collection("Mongo")]
public sealed class MongoPersistenceTests(MongoFixture fixture)
{
    private readonly IMongoDatabase database = fixture.Client.GetDatabase(fixture.DatabaseName);
    private readonly MongoUnitOfWork unitOfWork = new(fixture.Client);
    private readonly MutableClock clock = new(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Transaction_failure_rolls_back_business_and_outbox()
    {
        await new CollectionMigrations(database, clock).ApplyAsync();
        var business = database.GetCollection<BsonDocument>("orders");
        var outbox = new OutboxStore(database, clock);
        var id = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteAsync<int>(async (session, token) =>
        {
            await business.InsertOneAsync(session, new BsonDocument { { "_id", id }, { "schemaVersion", 1 } }, cancellationToken: token);
            await outbox.EnqueueAsync(session, id, "order.created", new BsonDocument("order", id), token);
            throw new InvalidOperationException("Downstream accounting failed");
        }));
        Assert.Null(await business.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync());
        Assert.Null(await database.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument("sourceKey", id)).FirstOrDefaultAsync());
    }

    [Fact]
    public async Task Lost_response_replays_result_once_and_different_body_conflicts()
    {
        await new CollectionMigrations(database, clock).ApplyAsync();
        var store = new IdempotencyStore(database, unitOfWork, clock);
        var orders = database.GetCollection<BsonDocument>("orders");
        var key = Guid.NewGuid().ToString("N");
        var hash = new string('a', 64);
        var actor = new ActorContext(9_007_199_254_740_993L, "issuer", "subject", 1,
            clock.UtcNow, FactorAssurance.MultiFactor);
        var scope = IdempotencyScope.ForActor(actor, "order.submit", key, hash);
        var calls = 0;
        async Task<BsonDocument> Work(IClientSessionHandle session, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            await orders.InsertOneAsync(session, new BsonDocument
            { { "_id", key }, { "schemaVersion", 1 }, { "version", 1L } }, cancellationToken: token);
            return new BsonDocument("id", key);
        }
        var first = await store.ExecuteAsync(scope, Work);
        var replay = await store.ExecuteAsync(scope, Work);
        Assert.Equal(first, replay);
        var storedRequest = await database.GetCollection<BsonDocument>("idempotencyRecords")
            .Find(new BsonDocument("key", key)).FirstAsync();
        Assert.True(storedRequest["actorId"].IsInt64);
        Assert.Equal(9_007_199_254_740_993L, storedRequest["actorId"].AsInt64);
        Assert.Equal(1, calls);
        Assert.Equal(1, await orders.CountDocumentsAsync(new BsonDocument("_id", key)));
        var conflict = await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            store.ExecuteAsync(scope with { BodyHash = new string('b', 64) }, Work));
        Assert.Equal("idempotency_key_reused", conflict.Code);
    }

    [Fact]
    public async Task Concurrent_same_key_has_one_effect()
    {
        await new CollectionMigrations(database, clock).ApplyAsync();
        var store = new IdempotencyStore(database, unitOfWork, clock);
        var orders = database.GetCollection<BsonDocument>("orders");
        var key = Guid.NewGuid().ToString("N");
        var scope = new IdempotencyScope("123", "order.submit", key, new string('c', 64));
        async Task<BsonDocument> Work(IClientSessionHandle session, CancellationToken token)
        {
            await orders.InsertOneAsync(session, new BsonDocument
            { { "_id", key }, { "schemaVersion", 1 } }, cancellationToken: token);
            return new BsonDocument("id", key);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => store.ExecuteAsync(scope, Work)));
        Assert.Equal(results[0], results[1]);
        Assert.Equal(1, await orders.CountDocumentsAsync(new BsonDocument("_id", key)));
    }

    [Fact]
    public async Task Version_mismatch_is_409_style_and_fresh_version_succeeds()
    {
        await new CollectionMigrations(database, clock).ApplyAsync();
        var orders = database.GetCollection<BsonDocument>("orders");
        var key = Guid.NewGuid().ToString("N");
        await orders.InsertOneAsync(new BsonDocument { { "_id", key }, { "schemaVersion", 1 }, { "version", 1L } });
        async Task<int> Change(long expected) => await unitOfWork.ExecuteAsync(async (session, token) =>
        {
            await MongoUnitOfWork.RequireVersionAsync(orders, session,
                Builders<BsonDocument>.Filter.Eq("_id", key), expected,
                Builders<BsonDocument>.Update.Set("status", "changed"), token);
            return 1;
        });
        var conflict = await Assert.ThrowsAsync<PersistenceConflictException>(() => Change(0));
        Assert.Equal("version_conflict", conflict.Code);
        Assert.Equal(1L, (await orders.Find(new BsonDocument("_id", key)).FirstAsync())["version"].AsInt64);
        await Change(1);
        Assert.Equal(2L, (await orders.Find(new BsonDocument("_id", key)).FirstAsync())["version"].AsInt64);
    }

    [Fact]
    public async Task Lease_expiry_allows_new_consumer_and_stale_ack_cannot_complete()
    {
        await new CollectionMigrations(database, clock).ApplyAsync();
        var outbox = new OutboxStore(database, clock);
        var key = Guid.NewGuid().ToString("N");
        await unitOfWork.ExecuteAsync(async (session, token) =>
        {
            await outbox.EnqueueAsync(session, key, "test", new BsonDocument("value", 1), token);
            return 0;
        });
        var first = await outbox.ClaimAsync("worker-a", new OutboxClaimPolicy(["test"], 3, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
        Assert.NotNull(first);
        Assert.Null(await outbox.ClaimAsync("worker-b", new OutboxClaimPolicy(["test"], 3, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.False(await outbox.CompleteAsync(first!));
        Assert.False(await outbox.RetryAsync(first!, TimeSpan.Zero));
        var restarted = new OutboxStore(database, clock);
        var second = await restarted.ClaimAsync("worker-b", new OutboxClaimPolicy(["test"], 3, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
        Assert.NotNull(second);
        Assert.False(await outbox.CompleteAsync(first!));
        Assert.True(await restarted.CompleteAsync(second!));
        Assert.False(await restarted.CompleteAsync(second!));
        Assert.Null(await restarted.ClaimAsync("worker-c", new OutboxClaimPolicy(["test"], 3, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))));
    }

    [Fact]
    public async Task BSON_and_JSON_numbers_are_lossless_and_bounded()
    {
        await new CollectionMigrations(database, clock).ApplyAsync();
        var id = new LongId(9_007_199_254_740_993L);
        Assert.Equal("\"9007199254740993\"", JsonSerializer.Serialize(id));
        Assert.Equal(id, JsonSerializer.Deserialize<LongId>(JsonSerializer.Serialize(id)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LongId>("9007199254740993"));
        Assert.Throws<DomainValidationException>(() => BsonNumbers.Id(default));
        Assert.Throws<DomainValidationException>(() => JsonSerializer.Serialize(default(LongId)));
        var signed = SignedMoney.Parse("-999999999999999.99");
        Assert.Equal("\"-999999999999999.99\"", JsonSerializer.Serialize(signed));
        Assert.Throws<DomainValidationException>(() => SignedMoney.Parse("-1000000000000000.00"));
        Assert.Throws<DomainValidationException>(() => new Money(1.000m));
        Assert.Throws<DomainValidationException>(() => new SignedMoney(-1.000m));
        Assert.Throws<DomainValidationException>(() => Money.Parse("1.000"));
        Assert.Throws<DomainValidationException>(() => SignedMoney.Parse("-1.001"));
        var amount = Money.Parse("999999999999999.99");
        Assert.Equal("\"999999999999999.99\"", JsonSerializer.Serialize(amount));
        var document = new BsonDocument { { "_id", Guid.NewGuid().ToString("N") },
            { "schemaVersion", 1 }, { "id", BsonNumbers.Id(id) }, { "amount", BsonNumbers.Money(amount) } };
        var collection = database.GetCollection<BsonDocument>("auditEvents");
        await collection.InsertOneAsync(document);
        var saved = await collection.Find(new BsonDocument("_id", document["_id"])).FirstAsync();
        Assert.True(saved["id"].IsInt64);
        Assert.True(saved["amount"].IsDecimal128);
        Assert.Equal(id, BsonNumbers.ReadId(saved["id"]));
        Assert.Equal(amount, BsonNumbers.ReadMoney(saved["amount"]));
        await Assert.ThrowsAsync<MongoWriteException>(() => collection.InsertOneAsync(new BsonDocument
        { { "_id", Guid.NewGuid().ToString("N") }, { "schemaVersion", 1 },
          { "documentTotal", new BsonDecimal128(Decimal128.Parse("1000000000000000.00")) } }));
        await Assert.ThrowsAsync<MongoWriteException>(() => collection.InsertOneAsync(new BsonDocument
        { { "_id", Guid.NewGuid().ToString("N") }, { "schemaVersion", 1 }, { "id", 9007199254740993.0 } }));
        await Assert.ThrowsAsync<MongoWriteException>(() => collection.InsertOneAsync(new BsonDocument
        { { "_id", Guid.NewGuid().ToString("N") }, { "schemaVersion", 1 }, { "quantity", 1_000_001 } }));
        Assert.Throws<DomainValidationException>(() => BsonNumbers.ReadMoney(new BsonDecimal128(Decimal128.Parse("1e100"))));
        foreach (var (field, value) in new[]
        {
            ("unitPrice", "1.000"), ("lineTotal", "1.001"), ("documentTotal", "1.000"),
            ("journalAmount", "-1.000"), ("rate", "1.00001")
        })
            await Assert.ThrowsAsync<MongoWriteException>(() => collection.InsertOneAsync(new BsonDocument
            { { "_id", Guid.NewGuid().ToString("N") }, { "schemaVersion", 1 },
              { field, new BsonDecimal128(Decimal128.Parse(value)) } }));
        Assert.Throws<DomainValidationException>(() => Money.Multiply(amount, 2));
        Assert.Throws<DomainValidationException>(() => Money.Add(amount, new Money(0.01m)));
        Assert.Throws<DomainValidationException>(() => NumericContracts.ParseRate("100.0001"));
        Assert.Throws<DomainValidationException>(() => NumericContracts.ParseRate("1.00001"));
        Assert.Throws<DomainValidationException>(() => new IdempotencyScope("123", "op", "key", null!).Validate());
        Assert.Throws<DomainValidationException>(() => new IdempotencyScope("000123", "op", "key", new string('a', 64)).Validate());
    }

    [Fact]
    public async Task Schema_and_unique_index_reject_invalid_documents()
    {
        await new CollectionMigrations(database, clock).ApplyAsync();
        var identities = database.GetCollection<BsonDocument>("identities");
        var suffix = Guid.NewGuid().ToString("N");
        var allocator = new IdAllocator(database);
        var firstId = await allocator.NextAsync("identities");
        var secondId = await allocator.NextAsync("identities");
        await Assert.ThrowsAsync<MongoWriteException>(() => identities.InsertOneAsync(new BsonDocument("issuer", suffix)));
        await identities.InsertOneAsync(new BsonDocument
        { { "_id", firstId.Value }, { "schemaVersion", 1 }, { "issuer", suffix }, { "subject", "subject" },
          { "status", "active" }, { "grantVersion", 1L }, { "fence", 0L } });
        await Assert.ThrowsAsync<MongoWriteException>(() => identities.InsertOneAsync(new BsonDocument
        { { "_id", secondId.Value }, { "schemaVersion", 1 }, { "issuer", suffix }, { "subject", "subject" },
          { "status", "active" }, { "grantVersion", 1L }, { "fence", 0L } }));
    }
}

[Collection("Mongo")]
public sealed class MongoConflictTests(MongoFixture fixture)
{
    private readonly IMongoDatabase database = fixture.Client.GetDatabase(fixture.DatabaseName);
    private readonly MongoUnitOfWork unitOfWork = new(fixture.Client);
    private readonly IClock clock = new SystemClock();

    [Fact]
    public async Task Four_conflicts_rollback_every_partial_write_and_fresh_retry_succeeds()
    {
        await new CollectionMigrations(database, clock).ApplyAsync();
        var orders = database.GetCollection<BsonDocument>("orders");
        var audit = database.GetCollection<BsonDocument>("auditEvents");
        var existing = Guid.NewGuid().ToString("N");
        var partial = Guid.NewGuid().ToString("N");
        await orders.InsertOneAsync(new BsonDocument { { "_id", existing }, { "schemaVersion", 1 } });
        var attempts = 0;
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => unitOfWork.ExecuteAsync(async (session, token) =>
        {
            Interlocked.Increment(ref attempts);
            await audit.InsertOneAsync(session, new BsonDocument { { "_id", partial }, { "schemaVersion", 1 } }, cancellationToken: token);
            await orders.InsertOneAsync(session, new BsonDocument { { "_id", existing }, { "schemaVersion", 1 } }, cancellationToken: token);
            return 0;
        }));
        Assert.Equal("version_conflict", error.Code);
        Assert.Equal(MongoUnitOfWork.MaximumAttempts, attempts);
        Assert.Null(await audit.Find(new BsonDocument("_id", partial)).FirstOrDefaultAsync());
        var fresh = Guid.NewGuid().ToString("N");
        await unitOfWork.ExecuteAsync(async (session, token) =>
        {
            await audit.InsertOneAsync(session, new BsonDocument { { "_id", fresh }, { "schemaVersion", 1 } }, cancellationToken: token);
            await orders.InsertOneAsync(session, new BsonDocument { { "_id", fresh }, { "schemaVersion", 1 } }, cancellationToken: token);
            return 0;
        });
        Assert.NotNull(await audit.Find(new BsonDocument("_id", fresh)).FirstOrDefaultAsync());
    }

    [Fact]
    public async Task Atomic_counter_and_partial_future_indexes_accept_multiple_unkeyed_documents()
    {
        await new CollectionMigrations(database, clock).ApplyAsync();
        var ids = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => new IdAllocator(database).NextAsync("orders-" + fixture.DatabaseName)));
        Assert.Equal(20, ids.Select(x => x.Value).Distinct().Count());
        var maxEntity = "max-" + Guid.NewGuid().ToString("N");
        var counters = database.GetCollection<BsonDocument>("idCounters");
        await counters.InsertOneAsync(new BsonDocument { { "_id", maxEntity }, { "value", long.MaxValue } });
        var exhausted = await Assert.ThrowsAsync<PersistenceConflictException>(() => new IdAllocator(database).NextAsync(maxEntity));
        Assert.Equal("id_exhausted", exhausted.Code);
        Assert.Equal(long.MaxValue, (await counters.Find(new BsonDocument("_id", maxEntity)).FirstAsync())["value"].AsInt64);        var invoices = database.GetCollection<BsonDocument>("invoices");
        await invoices.InsertOneAsync(new BsonDocument { { "_id", Guid.NewGuid().ToString("N") }, { "schemaVersion", 1 } });
        await invoices.InsertOneAsync(new BsonDocument { { "_id", Guid.NewGuid().ToString("N") }, { "schemaVersion", 1 } });
    }

    [Fact]
    public async Task Concurrent_different_writes_with_same_expected_version_have_one_winner()
    {
        await new CollectionMigrations(database, clock).ApplyAsync();
        var orders = database.GetCollection<BsonDocument>("orders");
        var id = Guid.NewGuid().ToString("N");
        await orders.InsertOneAsync(new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "version", 1L }, { "quantity", 10 } });
        async Task<bool> Reserve(int quantity)
        {
            try
            {
                await unitOfWork.ExecuteAsync(async (session, token) =>
                {
                    await MongoUnitOfWork.RequireVersionAsync(orders, session, Builders<BsonDocument>.Filter.Eq("_id", id), 1,
                        Builders<BsonDocument>.Update.Inc("quantity", -quantity), token);
                    return 0;
                });
                return true;
            }
            catch (PersistenceConflictException) { return false; }
        }
        var outcomes = await Task.WhenAll(Reserve(3), Reserve(4));
        Assert.Single(outcomes, x => x);
        var final = await orders.Find(new BsonDocument("_id", id)).FirstAsync();
        Assert.Equal(2L, final["version"].AsInt64);
        Assert.Contains(final["quantity"].AsInt32, new[] { 6, 7 });
    }
}
internal sealed class MutableClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = now;
    public void Advance(TimeSpan duration) => UtcNow += duration;
}
