using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class InventoryRepositoryTests(MongoFixture fixture)
{
    private static InventoryBalanceDocument Balance(long id = 1000, long sku = 200, long onHand = 1, long reserved = 0, long channel = 103) =>
        new(id, 400, new(new(new(new(100), new(101), new(102), new(channel)), new(sku), new(sku + 1), new(sku + 2)),
            onHand, reserved, 0, 0, 1));
    private static InventoryBalanceChange Change(InventoryBalanceDocument row, InventoryBalanceAction action, long quantity, long? revision = null) =>
        new(row.Id, row.PhysicalPoolId, row.Balance.Key, revision ?? row.Balance.Revision, action, quantity);
    private MongoUnitOfWork Unit => new(fixture.Client);
    private static void Begin(IClientSessionHandle tx) => tx.StartTransaction(new TransactionOptions(ReadConcern.Snapshot, ReadPreference.Primary, WriteConcern.WMajority));
    private async Task<StoreTestData> Data()
    {
        var data = await StoreTestData.CreateAsync(fixture);
        await InventoryGraphTests.InstallBalances(data);
        await data.Database.GetCollection<BsonDocument>("inventoryPools").InsertOneAsync(InventoryGraphTests.Pool());
        await data.Database.GetCollection<BsonDocument>("lots").InsertManyAsync([InventoryGraphTests.Lot(), InventoryGraphTests.Lot(302, 300, 301)]);
        return data;
    }
    private Task Seed(InventoryRepository repository, params InventoryBalanceDocument[] rows) => Unit.ExecuteAsync(async (tx, ct) =>
    {
        foreach (var row in rows) await repository.CreateAsync(tx, row, ct);
        return true;
    }, default);
    private static Task EarlierWrite(StoreTestData data, IClientSessionHandle tx) =>
        data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords").InsertOneAsync(tx, new BsonDocument { { "_id", 99000L }, { "schemaVersion", 1 }, { "version", 1L } });
    private static Task<long> EarlierCount(StoreTestData data) => data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords").CountDocumentsAsync(new BsonDocument("_id", 99000L));
    private Task<InventoryBalanceDocument> Read(InventoryRepository repository, long id) => Unit.ExecuteAsync((tx, ct) => repository.ReadAsync(tx, id, ct), default);

    [Fact]
    public async Task Actual_two_balance_transaction_preserves_exact_persisted_results()
    {
        await using var data = await Data();
        var repository = new InventoryRepository(data.Database);
        var source = Balance(reserved: 1); var destination = Balance(1001, 300, 0);
        await Seed(repository, source, destination);
        var changed = await Unit.ExecuteAsync((tx, ct) => repository.ApplyAsync(tx,
            [Change(source, InventoryBalanceAction.Dispatch, 1), Change(destination, InventoryBalanceAction.Receive, 12)], ct), default);
        Assert.Equal(source with { Balance = source.Balance with { OnHand = 0, Reserved = 0, Revision = 2 } }, changed[0]);
        Assert.Equal(destination with { Balance = destination.Balance with { OnHand = 12, Revision = 2 } }, changed[1]);
        Assert.Equal(changed[0], await Read(repository, source.Id));
        Assert.Equal(changed[1], await Read(repository, destination.Id));
    }

    [Fact]
    public async Task A_caught_stale_second_leg_aborts_the_first_leg_and_the_callers_earlier_write()
    {
        await using var data = await Data();
        var repository = new InventoryRepository(data.Database);
        var source = Balance(reserved: 1); var destination = Balance(1001, 300, 0);
        await Seed(repository, source, destination);
        using var tx = await fixture.Client.StartSessionAsync(); Begin(tx);
        await EarlierWrite(data, tx);
        await repository.ApplyAsync(tx, [Change(source, InventoryBalanceAction.Dispatch, 1)], default);
        Assert.Equal(0, (await repository.ReadAsync(tx, source.Id, default)).Balance.OnHand);
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => repository.ApplyAsync(tx,
            [Change(destination, InventoryBalanceAction.Receive, 12, 999)], default));
        Assert.Equal("stock_revision_conflict", error.Code);
        Assert.False(tx.IsInTransaction);
        Assert.Equal(source, await Read(repository, source.Id)); Assert.Equal(destination, await Read(repository, destination.Id));
        Assert.Equal(0, await EarlierCount(data));
    }

    [Fact]
    public async Task Cancellation_between_legs_rolls_back_the_entire_transaction_even_if_caught()
    {
        await using var data = await Data();
        var repository = new InventoryRepository(data.Database);
        var source = Balance(reserved: 1); var destination = Balance(1001, 300, 0);
        await Seed(repository, source, destination);
        using var tx = await fixture.Client.StartSessionAsync(); Begin(tx);
        await EarlierWrite(data, tx);
        await repository.ApplyAsync(tx, [Change(source, InventoryBalanceAction.Dispatch, 1)], default);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ApplyAsync(tx,
            [Change(destination, InventoryBalanceAction.Receive, 12)], cancelled.Token));
        Assert.False(tx.IsInTransaction);
        Assert.Equal(source, await Read(repository, source.Id)); Assert.Equal(0, await EarlierCount(data));
    }

    [Fact]
    public async Task Caller_abort_after_successful_batch_restores_both_balances()
    {
        await using var data = await Data();
        var repository = new InventoryRepository(data.Database);
        var source = Balance(reserved: 1); var destination = Balance(1001, 300, 0);
        await Seed(repository, source, destination);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Unit.ExecuteAsync<bool>(async (tx, ct) =>
        {
            await repository.ApplyAsync(tx, [Change(source, InventoryBalanceAction.Dispatch, 1), Change(destination, InventoryBalanceAction.Receive, 12)], ct);
            throw new InvalidOperationException("Synthetic owning-command failure");
        }, default));
        Assert.Equal(source, await Read(repository, source.Id)); Assert.Equal(destination, await Read(repository, destination.Id));
    }

    [Fact]
    public async Task Competing_reservations_for_one_final_package_cannot_both_commit()
    {
        await using var data = await Data();
        var repository = new InventoryRepository(data.Database); var row = Balance();
        await Seed(repository, row);
        var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<bool> Attempt()
        {
            var attempt = 0;
            try
            {
                await Unit.ExecuteAsync(async (tx, ct) =>
                {
                    if (++attempt == 1)
                    {
                        var before = await repository.ReadAsync(tx, row.Id, ct);
                        Assert.Equal(1, before.Balance.Revision); Assert.Equal(0, before.Balance.Reserved);
                        if (Interlocked.Increment(ref arrivals) == 2) bothRead.SetResult();
                        await bothRead.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                    }
                    return await repository.ApplyAsync(tx, [Change(row, InventoryBalanceAction.Reserve, 1)], ct);
                }, default);
                return true;
            }
            catch (PersistenceConflictException error) when (error.Code is "stock_revision_conflict" or "version_conflict") { return false; }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(results, value => value);
        var stored = await Read(repository, row.Id);
        Assert.Equal(1, stored.Balance.Reserved); Assert.Equal(1, stored.Balance.OnHand); Assert.Equal(2, stored.Balance.Revision);
    }

    [Fact]
    public async Task Another_channel_cannot_create_a_second_spendable_copy_of_the_same_pool_balance()
    {
        await using var data = await Data();
        var repository = new InventoryRepository(data.Database); var row = Balance();
        await Seed(repository, row);
        using var tx = await fixture.Client.StartSessionAsync(); Begin(tx); await EarlierWrite(data, tx);
        await Assert.ThrowsAsync<DomainValidationException>(() => repository.CreateAsync(tx, Balance(1001, channel: 104), default));
        // The graph rejects forged ownership, and the database independently rejects a second copy.
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Database.GetCollection<BsonDocument>("stockBalances")
            .InsertOneAsync(InventoryBson.Balance(Balance(1001, channel: 104))));
        Assert.False(tx.IsInTransaction); Assert.Equal(0, await EarlierCount(data));
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("stockBalances").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Missing_required_index_blocks_writes_and_aborts_the_callers_work()
    {
        await using var data = await Data();
        var indexes = await (await data.Database.GetCollection<BsonDocument>("stockBalances").Indexes.ListAsync()).ToListAsync();
        var index = Assert.Single(indexes, value => value["key"] == InventorySchemas.BalanceKey() && value.GetValue("unique", false).AsBoolean);
        await data.Database.GetCollection<BsonDocument>("stockBalances").Indexes.DropOneAsync(index["name"].AsString);
        var repository = new InventoryRepository(data.Database);
        using var tx = await fixture.Client.StartSessionAsync(); Begin(tx); await EarlierWrite(data, tx);
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => repository.CreateAsync(tx, Balance(), default));
        Assert.Equal("inventory_storage_unavailable", error.Code);
        Assert.False(tx.IsInTransaction); Assert.Equal(0, await EarlierCount(data));
    }

    [Fact]
    public async Task A_relaxed_validator_cannot_be_used_as_a_write_precondition()
    {
        await using var data = await Data();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "stockBalances" }, { "validationLevel", "off" } });
        var repository = new InventoryRepository(data.Database);
        using var tx = await fixture.Client.StartSessionAsync(); Begin(tx);
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => repository.CreateAsync(tx, Balance(), default));
        Assert.Equal("inventory_storage_unavailable", error.Code); Assert.False(tx.IsInTransaction);
    }

    [Fact]
    public async Task The_database_rejects_invalid_buckets_and_noncanonical_numeric_types_directly()
    {
        await using var data = await Data();
        foreach (var field in new[] { "reserved", "held", "expired" })
        {
            var invalid = InventoryBson.Balance(Balance()); invalid[field] = 2L;
            await Assert.ThrowsAsync<MongoWriteException>(() => data.Database.GetCollection<BsonDocument>("stockBalances").InsertOneAsync(invalid));
        }
        var wrongType = InventoryBson.Balance(Balance()); wrongType["onHand"] = 1;
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Database.GetCollection<BsonDocument>("stockBalances").InsertOneAsync(wrongType));
    }

    [Fact]
    public async Task A_transaction_is_required_and_a_foreign_client_is_not_accepted()
    {
        await using var data = await Data();
        var repository = new InventoryRepository(data.Database);
        using var inactive = await fixture.Client.StartSessionAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => repository.CreateAsync(inactive, Balance(), default));
        var otherClient = new MongoClient(fixture.Client.Settings);
        using var other = await otherClient.StartSessionAsync(); Begin(other);
        await Assert.ThrowsAsync<DomainValidationException>(() => repository.CreateAsync(other, Balance(), default));
        Assert.False(other.IsInTransaction);
    }

    [Fact]
    public async Task Forged_scope_pool_or_duplicate_batch_entries_do_not_change_real_balances()
    {
        await using var data = await Data();
        var repository = new InventoryRepository(data.Database); var row = Balance();
        await Seed(repository, row);
        foreach (var changed in new[] { Change(row, InventoryBalanceAction.Receive, 1) with { PhysicalPoolId = 999 },
            Change(row, InventoryBalanceAction.Receive, 1) with { Key = row.Balance.Key with { Location = row.Balance.Key.Location with { ChannelId = new(999) } } },
            Change(row, InventoryBalanceAction.Receive, 1) with { Action = (InventoryBalanceAction)99 } })
            await Assert.ThrowsAsync<DomainValidationException>(() => Unit.ExecuteAsync((tx, ct) => repository.ApplyAsync(tx, [changed], ct), default));
        await Assert.ThrowsAsync<DomainValidationException>(() => Unit.ExecuteAsync((tx, ct) => repository.ApplyAsync(tx,
            [Change(row, InventoryBalanceAction.Receive, 1), Change(row, InventoryBalanceAction.Receive, 1)], ct), default));
        Assert.Equal(row, await Read(repository, row.Id));
    }

    [Fact]
    public async Task Large_ids_and_quantities_survive_actual_Bson_without_numeric_coercion()
    {
        await using var data = await Data();
        var repository = new InventoryRepository(data.Database); var row = Balance(9007199254740993, onHand: long.MaxValue);
        await Seed(repository, row);
        Assert.Equal(row, await Read(repository, row.Id));
        var stored = await data.Database.GetCollection<BsonDocument>("stockBalances").Find(new BsonDocument("_id", row.Id)).SingleAsync();
        Assert.True(stored["_id"].IsInt64); Assert.True(stored["onHand"].IsInt64);
    }
    [Theory]
    [InlineData("id-int")] [InlineData("schema-long")] [InlineData("kind")] [InlineData("pool-zero")]
    [InlineData("sku-decimal")] [InlineData("missing-definition")] [InlineData("lot-double")] [InlineData("missing-owner")]
    [InlineData("extra-owner")] [InlineData("overcommitted")] [InlineData("negative-reserved")] [InlineData("zero-version")]
    [InlineData("extra-field")] [InlineData("duplicate-root")] [InlineData("duplicate-owner")]
    public void The_codec_rejects_noncanonical_and_ambiguous_stored_records(string malformed)
    {
        var row = InvalidRow(malformed);
        Assert.Equal("invalid_inventory_balance", Assert.Throws<DomainValidationException>(() => InventoryBson.ReadBalance(row)).Code);
    }

    private static BsonDocument InvalidRow(string malformed)
    {
        var row = InventoryBson.Balance(Balance());
        switch (malformed)
        {
            case "id-int": row["_id"] = 1000; break;
            case "schema-long": row["schemaVersion"] = 1L; break;
            case "kind": row["kind"] = "other"; break;
            case "pool-zero": row["physicalPoolId"] = 0L; break;
            case "sku-decimal": row["skuId"] = new BsonDecimal128(200m); break;
            case "missing-definition": row.Remove("definitionId"); break;
            case "lot-double": row["lotId"] = 202.0; break;
            case "missing-owner": row["location"].AsBsonDocument.Remove("warehouseId"); break;
            case "extra-owner": row["location"]["branchId"] = 11L; break;
            case "overcommitted": row["reserved"] = 1L; row["held"] = 1L; break;
            case "negative-reserved": row["reserved"] = -1L; break;
            case "zero-version": row["version"] = 0L; break;
            case "extra-field": row["unexpected"] = true; break;
            case "duplicate-root": row.AllowDuplicateNames = true; row.Add("onHand", 2L); break;
            case "duplicate-owner":
                row["location"].AsBsonDocument.AllowDuplicateNames = true;
                row["location"].AsBsonDocument.Add("channelId", 999L); break;
            default: throw new InvalidOperationException();
        }
        return row;
    }

    [Fact]
    public async Task Mongo_rejects_missing_extra_or_wrongly_typed_balance_fields()
    {
        await using var data = await Data();
        foreach (var malformed in new[] { "id-int", "schema-long", "kind", "pool-zero", "sku-decimal", "missing-definition",
            "lot-double", "missing-owner", "extra-owner", "overcommitted", "negative-reserved", "zero-version", "extra-field" })
            await Assert.ThrowsAsync<MongoWriteException>(() => data.Database.GetCollection<BsonDocument>("stockBalances").InsertOneAsync(InvalidRow(malformed)));
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("stockBalances").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task A_malformed_preexisting_record_aborts_the_callers_transaction_without_repairing_it()
    {
        await using var data = await Data();
        var row = InvalidRow("overcommitted");
        await data.Database.GetCollection<BsonDocument>("stockBalances").InsertOneAsync(row, new InsertOneOptions { BypassDocumentValidation = true });
        var repository = new InventoryRepository(data.Database);
        using var tx = await fixture.Client.StartSessionAsync(); Begin(tx); await EarlierWrite(data, tx);
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => repository.ReadAsync(tx, 1000, default));
        Assert.Equal("invalid_inventory_balance", error.Code); Assert.False(tx.IsInTransaction);
        Assert.Equal(0, await EarlierCount(data));
        Assert.Equal(row, await data.Database.GetCollection<BsonDocument>("stockBalances").Find(new BsonDocument("_id", 1000L)).SingleAsync());
    }

    [Fact]
    public async Task A_required_schema_hidden_in_an_optional_or_clause_does_not_satisfy_the_storage_gate()
    {
        await using var data = await Data();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", "stockBalances" }, { "validator", new BsonDocument("$or", new BsonArray { InventorySchemas.BalanceValidator(), new BsonDocument() }) }
        });
        var repository = new InventoryRepository(data.Database);
        using var tx = await fixture.Client.StartSessionAsync(); Begin(tx); await EarlierWrite(data, tx);
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => repository.CreateAsync(tx, Balance(), default));
        Assert.Equal("inventory_storage_unavailable", error.Code); Assert.False(tx.IsInTransaction);
        Assert.Equal(0, await EarlierCount(data));
    }

    [Theory]
    [InlineData("sparse")] [InlineData("partial")] [InlineData("nonunique")] [InlineData("extra-unique")] [InlineData("ttl")]
    public async Task Unsafe_or_additionally_restrictive_indexes_block_the_storage_boundary(string kind)
    {
        await using var data = await Data();
        var rows = data.Database.GetCollection<BsonDocument>("stockBalances");
        if (kind is "extra-unique" or "ttl")
            await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("location.warehouseId", 1),
                new CreateIndexOptions { Name = "unreviewed_extra", Unique = kind == "extra-unique", ExpireAfter = kind == "ttl" ? TimeSpan.FromDays(1) : null }));
        else
        {
            var indexes = await (await rows.Indexes.ListAsync()).ToListAsync();
            var index = Assert.Single(indexes, value => value["key"] == InventorySchemas.BalanceKey() && value.GetValue("unique", false).AsBoolean);
            await rows.Indexes.DropOneAsync(index["name"].AsString);
            await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(InventorySchemas.BalanceKey(),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "ux_inventory_pool_balance", Unique = kind != "nonunique", Sparse = kind == "sparse",
                    PartialFilterExpression = kind == "partial" ? new BsonDocument("kind", InventoryBson.BalanceKind) : null
                }));
        }
        var repository = new InventoryRepository(data.Database);
        using var tx = await fixture.Client.StartSessionAsync(); Begin(tx); await EarlierWrite(data, tx);
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => repository.CreateAsync(tx, Balance(), default));
        Assert.Equal("inventory_storage_unavailable", error.Code); Assert.False(tx.IsInTransaction);
        Assert.Equal(0, await EarlierCount(data));
    }

    [Fact]
    public async Task Empty_or_unbounded_batches_abort_prior_caller_work()
    {
        await using var data = await Data();
        var repository = new InventoryRepository(data.Database);
        foreach (var changes in new[] { Array.Empty<InventoryBalanceChange>(),
            Enumerable.Range(1, 101).Select(id => Change(Balance(id), InventoryBalanceAction.Receive, 1)).ToArray() })
        {
            using var tx = await fixture.Client.StartSessionAsync(); Begin(tx); await EarlierWrite(data, tx);
            var error = await Assert.ThrowsAsync<DomainValidationException>(() => repository.ApplyAsync(tx, changes, default));
            Assert.Equal("invalid_inventory_change", error.Code); Assert.False(tx.IsInTransaction);
            Assert.Equal(0, await EarlierCount(data));
        }
    }
    [Fact]
    public async Task A_stale_later_entry_in_one_batch_aborts_all_earlier_updates()
    {
        await using var data = await Data();
        var repository = new InventoryRepository(data.Database);
        var source = Balance(reserved: 1); var destination = Balance(1001, 300, 0);
        await Seed(repository, source, destination);
        using var tx = await fixture.Client.StartSessionAsync(); Begin(tx); await EarlierWrite(data, tx);
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => repository.ApplyAsync(tx,
            [Change(source, InventoryBalanceAction.Dispatch, 1), Change(destination, InventoryBalanceAction.Receive, 12, 999)], default));
        Assert.Equal("stock_revision_conflict", error.Code); Assert.False(tx.IsInTransaction);
        Assert.Equal(source, await Read(repository, source.Id)); Assert.Equal(destination, await Read(repository, destination.Id));
        Assert.Equal(0, await EarlierCount(data));
    }
}
