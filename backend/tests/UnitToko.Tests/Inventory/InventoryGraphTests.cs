using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class InventoryGraphTests(MongoFixture fixture)
{
    internal static BsonDocument Pool() => new()
    {
        { "_id", 400L }, { "schemaVersion", 1 }, { "kind", "inventory.pool" },
        { "location", InventoryBson.Balance(Balance())["location"].DeepClone() },
        { "active", true }, { "revision", 1L }, { "fence", 0L }
    };
    internal static InventoryBalanceDocument Balance() => new(1000, 400,
        new(new(new(new(100), new(101), new(102), new(103)), new(200), new(201), new(202)), 10, 0, 0, 0, 1));
    internal static BsonDocument Lot(long id = 202, long sku = 200, long definition = 201, long? parent = null) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "kind", "inventory.lot" }, { "organizationId", 100L },
        { "skuId", sku }, { "definitionId", definition }, { "rootLotId", parent.HasValue ? 202L : id },
        { "parentLotId", parent.HasValue ? new BsonInt64(parent.Value) : BsonNull.Value },
        { "origin", parent.HasValue ? "packageConversion" : "procurementReceipt" },
        { "sourceId", id + 1000 }, { "sourceLineId", id + 2000 },
        { "recordedAt", new BsonDateTime(new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc)) },
        { "expiresOn", "2027-01-31" }
    };
    internal static async Task InstallBalances(StoreTestData data, int? historicalFixtureVersion = null)
    {
        var required = InventorySchemas.BalanceValidator(); var key = InventorySchemas.BalanceKey();
        if (historicalFixtureVersion is { } version)
        {
            // Only these isolated pre-ledger migration fixtures install their synthetic balance contract.
            Assert.True(version is 17 or 18);
            Assert.True(await new MigrationGate(data.Database, data.Clock, version).IsCurrentAsync(default));
            var rows = data.Database.GetCollection<BsonDocument>("stockBalances");
            Assert.False(await rows.Find(new BsonDocument()).AnyAsync());
            var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "stockBalances") })).SingleAsync();
            Assert.Equal("collection", definition["type"].AsString);
            var options = definition["options"].AsBsonDocument;
            Assert.False(options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds"));
            Assert.Equal("strict", options.GetValue("validationLevel", "strict").AsString);
            Assert.Equal("error", options.GetValue("validationAction", "error").AsString);
            Assert.True(!options.TryGetValue("collation", out var collation) || collation.Equals(new BsonDocument("locale", "simple")));
            var previous = options["validator"].AsBsonDocument;
            var indexes = await (await rows.Indexes.ListAsync()).ToListAsync();
            foreach (var index in indexes)
            {
                Assert.False(index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean ||
                    index.Contains("partialFilterExpression") || index.Contains("expireAfterSeconds"));
                Assert.True(!index.TryGetValue("collation", out var indexCollation) || indexCollation.Equals(new BsonDocument("locale", "simple")));
                if (index["key"].Equals(key)) Assert.True(index.GetValue("unique", false).AsBoolean);
                else Assert.True(index["name"] == "_id_" || !index.GetValue("unique", false).AsBoolean);
            }
            if (!InventorySchemas.Contains(previous, required))
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
                    { "collMod", "stockBalances" }, { "validator", new BsonDocument("$and", new BsonArray { previous, required }) },
                    { "validationLevel", "strict" }, { "validationAction", "error" }
                });
            if (!indexes.Any(index => index["key"].Equals(key)))
                await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key,
                    new CreateIndexOptions { Name = "uq_historical_fixture_stock_balance", Unique = true }));
        }
        // Current fixtures use the registered migration's actual guard and key without changing DDL.
        await InventoryRepository.RequireStorageAsync(data.Database, "stockBalances", required, key, default);
    }
    private async Task<StoreTestData> Data()
    {
        var data = await StoreTestData.CreateAsync(fixture); await InstallBalances(data);
        await data.Database.GetCollection<BsonDocument>("inventoryPools").InsertOneAsync(Pool());
        await data.Database.GetCollection<BsonDocument>("lots").InsertOneAsync(Lot());
        await data.Database.GetCollection<BsonDocument>("stockBalances").InsertOneAsync(InventoryBson.Balance(Balance()));
        return data;
    }
    private static InventoryBalanceChange Receive() => new(1000, 400, Balance().Balance.Key, 1, InventoryBalanceAction.Receive, 1);

    [Theory]
    [InlineData("missing-pool")] [InlineData("inactive-pool")] [InlineData("wrong-owner")] [InlineData("malformed-pool")]
    [InlineData("missing-lot")] [InlineData("wrong-sku")] [InlineData("wrong-definition")] [InlineData("wrong-organization")]
    [InlineData("wrong-root")] [InlineData("missing-parent")] [InlineData("malformed-lot")] [InlineData("fractional-id")]
    public async Task Invalid_real_pool_or_lot_aborts_the_stock_change_and_prior_caller_work(string fault)
    {
        await using var data = await Data();
        var pool = Pool(); var lot = Lot();
        switch (fault)
        {
            case "missing-pool": await data.Database.GetCollection<BsonDocument>("inventoryPools").DeleteManyAsync(new BsonDocument()); break;
            case "inactive-pool": pool["active"] = false; break;
            case "wrong-owner": pool["location"]["channelId"] = 104L; break;
            case "malformed-pool": pool["extra"] = true; break;
            case "missing-lot": await data.Database.GetCollection<BsonDocument>("lots").DeleteManyAsync(new BsonDocument()); break;
            case "wrong-sku": lot["skuId"] = 999L; break;
            case "wrong-definition": lot["definitionId"] = 999L; break;
            case "wrong-organization": lot["organizationId"] = 999L; break;
            case "wrong-root": lot["rootLotId"] = 999L; break;
            case "missing-parent": lot["parentLotId"] = 999L; lot["rootLotId"] = 998L; lot["origin"] = "packageConversion"; break;
            case "malformed-lot": lot["expiresOn"] = "2027-02-31"; break;
            case "fractional-id": lot["definitionId"] = 201.5; break;
        }
        if (fault != "missing-pool") await data.Database.GetCollection<BsonDocument>("inventoryPools").ReplaceOneAsync(new BsonDocument("_id", 400L), pool, new ReplaceOptions { BypassDocumentValidation = true }); // Deliberately corrupt a guarded local fixture.
        if (fault != "missing-lot") await data.Database.GetCollection<BsonDocument>("lots").ReplaceOneAsync(new BsonDocument("_id", 202L), lot);
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
        await data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords").InsertOneAsync(tx, new BsonDocument { { "_id", 99000L }, { "schemaVersion", 1 } });
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => new InventoryRepository(data.Database).ApplyAsync(tx, [Receive()], default));
        Assert.Equal("inventory_graph_unavailable", error.Code); Assert.False(tx.IsInTransaction);
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(InventoryBson.Balance(Balance()), await data.Database.GetCollection<BsonDocument>("stockBalances").Find(new BsonDocument("_id", 1000L)).SingleAsync());
    }
    [Theory]
    [InlineData("create")] [InlineData("read")]
    public async Task Creating_or_reading_a_balance_also_requires_real_batch_ownership(string operation)
    {
        await using var data = await Data();
        await data.Database.GetCollection<BsonDocument>("lots").DeleteManyAsync(new BsonDocument());
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
        var repository = new InventoryRepository(data.Database);
        if (operation == "read") await Assert.ThrowsAsync<DomainValidationException>(() => repository.ReadAsync(tx, 1000, default));
        else await Assert.ThrowsAsync<DomainValidationException>(() => repository.CreateAsync(tx, Balance() with { Id = 1001 }, default));
        Assert.False(tx.IsInTransaction);
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("stockBalances").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task A_suspended_pool_remains_readable_without_mutation_but_cannot_change_stock()
    {
        await using var data = await Data();
        await data.Database.GetCollection<BsonDocument>("inventoryPools").UpdateOneAsync(new BsonDocument("_id", 400L), new BsonDocument("$set", new BsonDocument("active", false)));
        var unit = new MongoUnitOfWork(fixture.Client); var repository = new InventoryRepository(data.Database);
        Assert.Equal(Balance(), await unit.ExecuteAsync((tx, ct) => repository.ReadAsync(tx, 1000, ct), default));
        Assert.Equal(0, (await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument()).SingleAsync())["fence"].AsInt64);
        await Assert.ThrowsAsync<DomainValidationException>(() => unit.ExecuteAsync((tx, ct) => repository.ApplyAsync(tx, [Receive()], ct), default));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task A_real_conversion_child_retains_its_original_expiry_and_root_across_multiple_generations(bool withoutExpiry)
    {
        await using var data = await Data();
        var children = new[] { Lot(302, 300, 301, 202), Lot(402, 500, 501, 302) };
        if (withoutExpiry)
        {
            foreach (var childLot in children) childLot["expiresOn"] = BsonNull.Value;
            await data.Database.GetCollection<BsonDocument>("lots").UpdateOneAsync(new BsonDocument("_id", 202L), new BsonDocument("$set", new BsonDocument("expiresOn", BsonNull.Value)));
        }
        await data.Database.GetCollection<BsonDocument>("lots").InsertManyAsync(children);
        var child = Balance() with { Id = 2000, Balance = Balance().Balance with { Key = Balance().Balance.Key with { SkuId = new(500), DefinitionId = new(501), LotId = new(402) } } };
        var repository = new InventoryRepository(data.Database); var unit = new MongoUnitOfWork(fixture.Client);
        Assert.Equal(child, await unit.ExecuteAsync((tx, ct) => repository.CreateAsync(tx, child, ct), default));
        Assert.Equal(child, await unit.ExecuteAsync((tx, ct) => repository.ReadAsync(tx, 2000, ct), default));
        Assert.Equal(1, (await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument()).SingleAsync())["fence"].AsInt64);
    }

    [Theory]
    [InlineData("expiry-changed")] [InlineData("expiry-cleared")] [InlineData("older-than-parent")] [InlineData("wrong-organization")]
    [InlineData("wrong-root")] [InlineData("cycle")] [InlineData("self-parent")] [InlineData("missing-ancestor")]
    public async Task Conversion_lineage_cannot_be_fabricated_or_used_to_rejuvenate_expiry(string fault)
    {
        await using var data = await Data();
        var child = Lot(302, 300, 301, 202);
        switch (fault)
        {
            case "expiry-changed": child["expiresOn"] = "2027-02-01"; break;
            case "expiry-cleared": child["expiresOn"] = BsonNull.Value; break;
            case "older-than-parent": child["recordedAt"] = new BsonDateTime(new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc)); break;
            case "wrong-organization": child["organizationId"] = 999L; break;
            case "wrong-root": child["rootLotId"] = 999L; break;
            case "cycle":
                child["parentLotId"] = 402L;
                await data.Database.GetCollection<BsonDocument>("lots").InsertOneAsync(Lot(402, 500, 501, 302)); break;
            case "self-parent": child["parentLotId"] = 302L; break;
            case "missing-ancestor": child["parentLotId"] = 999L; break;
        }
        await data.Database.GetCollection<BsonDocument>("lots").InsertOneAsync(child);
        var balance = Balance() with { Id = 2000, Balance = Balance().Balance with { Key = Balance().Balance.Key with { SkuId = new(300), DefinitionId = new(301), LotId = new(302) } } };
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
        await Assert.ThrowsAsync<DomainValidationException>(() => new InventoryRepository(data.Database).CreateAsync(tx, balance, default));
        Assert.False(tx.IsInTransaction);
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("stockBalances").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, (await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument()).SingleAsync())["fence"].AsInt64);
    }

    [Fact]
    public async Task A_pool_suspension_after_the_transaction_read_cannot_commit_an_outdated_stock_write()
    {
        await using var data = await Data();
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(new TransactionOptions(ReadConcern.Snapshot, ReadPreference.Primary, WriteConcern.WMajority));
        var repository = new InventoryRepository(data.Database);
        Assert.Equal(Balance(), await repository.ReadAsync(tx, 1000, default));
        await data.Database.GetCollection<BsonDocument>("inventoryPools").UpdateOneAsync(new BsonDocument("_id", 400L), new BsonDocument("$set", new BsonDocument("active", false)));
        await Assert.ThrowsAnyAsync<MongoException>(() => repository.ApplyAsync(tx, [Receive()], default));
        Assert.False(tx.IsInTransaction);
        Assert.Equal(10, (await data.Database.GetCollection<BsonDocument>("stockBalances").Find(new BsonDocument()).SingleAsync())["onHand"].AsInt64);
    }
    [Fact]
    public void Provenance_decoding_rejects_missing_duplicate_extra_and_wrongly_typed_fields()
    {
        foreach (var field in Lot().Names.ToArray())
        {
            var missing = Lot(); missing.Remove(field);
            Assert.Throws<DomainValidationException>(() => InventoryGraph.ReadLot(missing));
        }
        foreach (var field in Pool().Names.ToArray())
        {
            var missing = Pool(); missing.Remove(field);
            Assert.Throws<DomainValidationException>(() => InventoryGraph.RequirePool(missing));
        }
        foreach (var field in new[] { "_id", "organizationId", "skuId", "definitionId", "rootLotId", "sourceId", "sourceLineId" })
        {
            foreach (var value in new BsonValue[] { 0L, -1L, 1, 1.0, "1", BsonNull.Value })
            {
                var invalid = Lot(); invalid[field] = value;
                Assert.Throws<DomainValidationException>(() => InventoryGraph.ReadLot(invalid));
            }
        }
        var extra = Lot(); extra["unexpected"] = true;
        Assert.Throws<DomainValidationException>(() => InventoryGraph.ReadLot(extra));
        var duplicate = new BsonDocument(Lot()) { AllowDuplicateNames = true }; duplicate.Add("skuId", 999L);
        Assert.Throws<DomainValidationException>(() => InventoryGraph.ReadLot(duplicate));
        var invalidDate = Lot(); invalidDate["recordedAt"] = new BsonDateTime(long.MaxValue);
        Assert.Throws<DomainValidationException>(() => InventoryGraph.ReadLot(invalidDate));
        var unknownOrigin = Lot(); unknownOrigin["origin"] = "countObservation";
        Assert.Throws<DomainValidationException>(() => InventoryGraph.ReadLot(unknownOrigin));
        var wrongSchema = Lot(); wrongSchema["schemaVersion"] = 1L;
        Assert.Throws<DomainValidationException>(() => InventoryGraph.ReadLot(wrongSchema));
        var duplicatePool = new BsonDocument(Pool()) { AllowDuplicateNames = true }; duplicatePool.Add("revision", 2L);
        Assert.Throws<DomainValidationException>(() => InventoryGraph.RequirePool(duplicatePool));
    }

    [Theory]
    [InlineData(64, true)] [InlineData(65, false)]
    public async Task Lineage_traversal_is_bounded_without_truncating_unknown_ancestry(int total, bool accepted)
    {
        await using var data = await Data();
        var rows = new List<BsonDocument>(); long parent = 202;
        for (var index = 1; index < total; index++)
        {
            var id = 10000L + index;
            rows.Add(Lot(id, 300, 301, parent)); parent = id;
        }
        await data.Database.GetCollection<BsonDocument>("lots").InsertManyAsync(rows);
        var balance = Balance() with { Id = 2000, Balance = Balance().Balance with { Key = Balance().Balance.Key with { SkuId = new(300), DefinitionId = new(301), LotId = new(parent) } } };
        var repository = new InventoryRepository(data.Database); var unit = new MongoUnitOfWork(fixture.Client);
        if (accepted) Assert.Equal(balance, await unit.ExecuteAsync((tx, ct) => repository.CreateAsync(tx, balance, ct), default));
        else await Assert.ThrowsAsync<DomainValidationException>(() => unit.ExecuteAsync((tx, ct) => repository.CreateAsync(tx, balance, ct), default));
        Assert.Equal(accepted ? 2 : 1, await data.Database.GetCollection<BsonDocument>("stockBalances").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task An_exhausted_pool_fence_prevents_stock_writes_without_losing_historical_reads()
    {
        await using var data = await Data();
        await data.Database.GetCollection<BsonDocument>("inventoryPools").UpdateOneAsync(new BsonDocument("_id", 400L), new BsonDocument("$set", new BsonDocument("fence", long.MaxValue)));
        var repository = new InventoryRepository(data.Database); var unit = new MongoUnitOfWork(fixture.Client);
        Assert.Equal(Balance(), await unit.ExecuteAsync((tx, ct) => repository.ReadAsync(tx, 1000, ct), default));
        await Assert.ThrowsAsync<DomainValidationException>(() => unit.ExecuteAsync((tx, ct) => repository.ApplyAsync(tx, [Receive()], ct), default));
        Assert.Equal(long.MaxValue, (await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument()).SingleAsync())["fence"].AsInt64);
    }

    [Fact]
    public async Task Quantity_failure_after_fencing_rolls_back_pool_metadata_and_the_callers_prior_work()
    {
        await using var data = await Data();
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
        await data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords").InsertOneAsync(tx, new BsonDocument { { "_id", 99000L }, { "schemaVersion", 1 } });
        await Assert.ThrowsAsync<DomainValidationException>(() => new InventoryRepository(data.Database).ApplyAsync(tx,
            [Receive() with { Action = InventoryBalanceAction.Reserve, Packages = 11 }], default));
        Assert.False(tx.IsInTransaction);
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, (await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument()).SingleAsync())["fence"].AsInt64);
        Assert.Equal(InventoryBson.Balance(Balance()), await data.Database.GetCollection<BsonDocument>("stockBalances").Find(new BsonDocument()).SingleAsync());
    }
}
