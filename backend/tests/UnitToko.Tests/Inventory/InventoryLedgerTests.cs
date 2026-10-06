using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Accounting;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class InventoryLedgerTests(MongoFixture fixture)
{
    private static readonly ActorContext Actor = new(42, "https://synthetic.invalid/", "staff:42", 1,
        new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero), FactorAssurance.MultiFactor);
    private static readonly InventorySource Source = new("test-receipt", 5000, "receive", 1);
    private static readonly InventoryWriteMetadata Metadata = new("Synthetic inspected receipt", "test-ledger");
    private BsonDocument initialPool = null!;
    private InventoryBalanceDocument FixtureBalance() => InventoryGraphTests.Balance() with { PhysicalPoolId = initialPool["_id"].AsInt64 };
    private MongoUnitOfWork Unit => new(fixture.Client);
    private async Task<StoreTestData> Data()
    {
        var data = await StoreTestData.CreateAsync(fixture);
        try
        {
            await InventoryGraphTests.InstallBalances(data);
            initialPool = await ActivateEmptyPoolAsync(data);
            await data.Database.GetCollection<BsonDocument>("lots").InsertOneAsync(InventoryGraphTests.Lot());
            await data.Database.GetCollection<BsonDocument>("stockBalances").InsertOneAsync(InventoryBson.Balance(FixtureBalance()));
            await InventoryRepository.RequireStorageAsync(data.Database, "movements", InventoryMovementSchemas.Validator(), InventoryMovementSchemas.Key(), default);
            await data.Database.GetCollection<BsonDocument>("inventoryTestSources").InsertOneAsync(new BsonDocument
            {
                { "_id", 5000L }, { "namespace", Source.Namespace }, { "eventKind", Source.EventKind }, { "occurrence", 1L },
                { "revision", 1L }, { "fence", 0L }, { "approved", true }, { "actorId", 42L },
                { "poolId", initialPool["_id"] }, { "poolRevision", initialPool["revision"] }, { "packages", 3L }
            });
            return data;
        }
        catch { await data.DisposeAsync(); throw; }
    }
    private async Task<BsonDocument> ActivateEmptyPoolAsync(StoreTestData data)
    {
        // The source adapter and on-hand balance remain explicit unit inputs. The pool's setup,
        // policy and activation are real owning commands, completed while this pool is empty.
        var location = InventoryGraphTests.Balance().Balance.Key.Location;
        var maker = await data.AddActorAsync(104100, location.ToScope(), Permissions.StockSourcePropose, Permissions.StockSourcePolicyPropose, Permissions.StockSourceRead);
        var checker = await data.AddActorAsync(104200, location.ToScope(), Permissions.StockSourceApprove, Permissions.StockSourcePolicyApprove, Permissions.StockSourceRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, checker.UserId);
        var sources = new StockSourceSetupService(data.Database, Unit, new(data.Database), data.Clock);
        AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic independently reviewed empty ledger fixture", "inventory-ledger-fixture");
        const string handoffReference = "Synthetic empty ledger pool handoff";
        var setup = await sources.ProposeAsync(maker, new(location, "synthetic-ledger-empty-pool", "Synthetic isolated ledger source", StockAuthorityMode.WebLedger,
            StockPoolControl.SharedWebLedger, 60000, 0, null, null, "Synthetic ledger owner", handoffReference), Meta("ledger-source"));
        var approved = await sources.DecideAsync(checker, setup["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Independent synthetic source review", Meta("ledger-source-approved"));
        var poolId = approved["poolId"].AsInt64;
        var policyPreview = await sources.PreviewOperatingPolicyAsync(maker, poolId);
        var policy = await sources.ProposeOperatingPolicyAsync(maker, poolId, new(policyPreview["poolRevision"].AsInt64, policyPreview["suspensionRevision"].AsInt64,
            policyPreview["policyRevision"].AsInt64, policyPreview["configurationHash"].AsString),
            new(StockMovementWriter.UnitToko, null, 0, "Synthetic zero-stock reconciliation", "Synthetic suspend before outage"), Meta("ledger-policy"));
        await sources.DecideOperatingPolicyAsync(checker, policy["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Independent synthetic operating policy review", Meta("ledger-policy-approved"));
        var preview = await sources.PreviewActivationAsync(maker, poolId);
        Assert.Equal(7, preview["obligations"].AsBsonArray.Count);
        Assert.All(preview["obligations"].AsBsonArray, obligation => {
            Assert.True(obligation["complete"].AsBoolean); Assert.Equal(0L, obligation["openCount"].AsInt64);
        });
        var handoff = new StockSourceEmptyHandoff(handoffReference, data.Clock.UtcNow, 0, 0, true, true, true);
        var activation = await sources.ProposeActivationAsync(maker, poolId, StockSourceActivationBson.ReadExpected(preview["expected"].AsBsonDocument), handoff, Meta("ledger-activation"));
        var review = await sources.PreviewActivationDecisionAsync(checker, activation["proposalId"].AsInt64, StockSourceSetupDecision.Approve, handoff);
        var committed = await sources.DecideActivationAsync(checker, activation["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, handoff,
            StockSourceActivationBson.ReadExpected(review["expected"].AsBsonDocument), review["binding"].AsString, Meta("ledger-activation-approved"));
        Assert.Equal("activated", committed["status"]);
        var pool = await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument("_id", poolId)).SingleAsync();
        Assert.True(pool["active"].AsBoolean);
        return pool;
    }
    private static FixtureReader Reader(StoreTestData data) => new(data.Database);
    private static InventoryLedger Ledger(StoreTestData data, params IInventorySourceReader[] readers) => new(data.Database, data.Clock, readers);
    private static Task Earlier(StoreTestData data, IClientSessionHandle tx) => data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords").InsertOneAsync(tx,
        new BsonDocument { { "_id", 99000L }, { "schemaVersion", 1 } });
    private async Task AssertUnchanged(StoreTestData data)
    {
        Assert.Equal(10, (await data.Database.GetCollection<BsonDocument>("stockBalances").Find(new BsonDocument()).SingleAsync())["onHand"].AsInt64);
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(initialPool, await data.Database.GetCollection<BsonDocument>("inventoryPools").Find(new BsonDocument()).SingleAsync());
        Assert.Equal(0, (await data.Database.GetCollection<BsonDocument>("inventoryTestSources").Find(new BsonDocument()).SingleAsync())["fence"].AsInt64);
    }
    [Fact]
    public async Task A_real_approved_source_commits_stock_and_an_immutable_before_after_record_together()
    {
        await using var data = await Data(); var ledger = Ledger(data, Reader(data));
        var receipt = await Unit.ExecuteAsync((tx, ct) => ledger.ApplyAsync(tx, Actor, Source, 1, Metadata, ct), default);
        Assert.True(receipt.Id > 0); Assert.Equal("test-receipt:5000:receive:1", receipt.SourceEvent);
        Assert.Equal(13, Assert.Single(receipt.Balances).Balance.OnHand);
        var movement = await data.Database.GetCollection<BsonDocument>("movements").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(42, movement["actorId"].AsInt64); Assert.Equal(1, movement["sourceRevision"].AsInt64);
        Assert.Equal(10, movement["legs"][0]["before"]["onHand"].AsInt64);
        Assert.Equal(13, movement["legs"][0]["after"]["onHand"].AsInt64);
        Assert.Equal(1, (await data.Database.GetCollection<BsonDocument>("inventoryTestSources").Find(new BsonDocument()).SingleAsync())["fence"].AsInt64);
    }
    [Theory]
    [InlineData("unapproved")] [InlineData("other-actor")] [InlineData("source-revision")] [InlineData("pool-revision")] [InlineData("missing-reader")]
    public async Task Source_or_authority_failure_aborts_all_prior_caller_work(string fault)
    {
        await using var data = await Data();
        if (fault == "unapproved") await data.Database.GetCollection<BsonDocument>("inventoryTestSources").UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("approved", false)));
        if (fault == "other-actor") await data.Database.GetCollection<BsonDocument>("inventoryTestSources").UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("actorId", 99L)));
        if (fault == "pool-revision") await data.Database.GetCollection<BsonDocument>("inventoryTestSources").UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("poolRevision", 99L)));
        var ledger = fault == "missing-reader" ? Ledger(data) : Ledger(data, Reader(data));
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        await Assert.ThrowsAsync<DomainValidationException>(() => ledger.ApplyAsync(tx, Actor, Source, fault == "source-revision" ? 99 : 1, Metadata));
        Assert.False(tx.IsInTransaction); await AssertUnchanged(data);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task A_duplicate_source_always_aborts_even_when_the_earlier_event_was_identical(bool changed)
    {
        await using var data = await Data(); var ledger = Ledger(data, Reader(data));
        await Unit.ExecuteAsync((tx, ct) => ledger.ApplyAsync(tx, Actor, Source, 1, Metadata, ct), default);
        if (changed) await data.Database.GetCollection<BsonDocument>("inventoryTestSources").UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("packages", 99L)));
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => ledger.ApplyAsync(tx, Actor, Source, 1, Metadata));
        Assert.Equal("inventory_source_already_applied", error.Code); Assert.False(tx.IsInTransaction);
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(13, (await data.Database.GetCollection<BsonDocument>("stockBalances").Find(new BsonDocument()).SingleAsync())["onHand"].AsInt64);
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("movements").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task The_owning_command_can_roll_back_a_successful_stock_and_movement_write()
    {
        await using var data = await Data(); var ledger = Ledger(data, Reader(data));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Unit.ExecuteAsync<bool>(async (tx, ct) =>
        {
            await Earlier(data, tx); await ledger.ApplyAsync(tx, Actor, Source, 1, Metadata, ct);
            throw new InvalidOperationException("Synthetic downstream owning-command failure");
        }, default));
        await AssertUnchanged(data);
    }
    [Fact]
    public async Task A_database_rejection_after_stock_changes_rolls_back_source_fences_and_balances()
    {
        await using var data = await Data();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", "movements" }, { "validator", new BsonDocument("$and", new BsonArray
            {
                InventoryMovementSchemas.Validator(), new BsonDocument("reason", new BsonDocument("$ne", Metadata.Reason))
            }) }
        });
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        var error = await Assert.ThrowsAsync<MongoWriteException>(() => Ledger(data, Reader(data)).ApplyAsync(tx, Actor, Source, 1, Metadata));
        Assert.Equal(121, error.WriteError.Code); Assert.False(tx.IsInTransaction); await AssertUnchanged(data);
    }
    [Theory]
    [InlineData("missing-index")] [InlineData("sparse-index")] [InlineData("disabled-validation")] [InlineData("optional-validator")]
    public async Task Missing_or_weakened_movement_constraints_block_the_entire_transaction(string fault)
    {
        await using var data = await Data();
        if (fault is "missing-index" or "sparse-index")
        {
            var indexes = await (await data.Database.GetCollection<BsonDocument>("movements").Indexes.ListAsync()).ToListAsync();
            var index = Assert.Single(indexes, value => value["key"] == InventoryMovementSchemas.Key() && value.GetValue("unique", false).AsBoolean);
            await data.Database.GetCollection<BsonDocument>("movements").Indexes.DropOneAsync(index["name"].AsString);
            if (fault == "sparse-index") await data.Database.GetCollection<BsonDocument>("movements").Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(InventoryMovementSchemas.Key(), new CreateIndexOptions { Name = "ux_inventory_source_event", Unique = true, Sparse = true }));
        }
        if (fault == "disabled-validation") await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "movements" }, { "validationLevel", "off" } });
        if (fault == "optional-validator") await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument
            { { "collMod", "movements" }, { "validator", new BsonDocument("$or", new BsonArray { InventoryMovementSchemas.Validator(), new BsonDocument("schemaVersion", 1) }) } });
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => Ledger(data, Reader(data)).ApplyAsync(tx, Actor, Source, 1, Metadata));
        Assert.Equal("inventory_storage_unavailable", error.Code); Assert.False(tx.IsInTransaction); await AssertUnchanged(data);
    }
    [Theory]
    [InlineData("source")] [InlineData("revision")] [InlineData("scope")] [InlineData("empty")] [InlineData("action")]
    public async Task A_malformed_or_mismatched_adapter_snapshot_is_not_trusted(string fault)
    {
        await using var data = await Data();
        var reader = new FixtureReader(data.Database) { Transform = value => fault switch
        {
            "source" => value with { Source = value.Source with { Occurrence = 2 } },
            "revision" => value with { Revision = 2 },
            "scope" => value with { Changes = [value.Changes[0] with { PhysicalPoolId = 999 }] },
            "empty" => value with { Changes = [] },
            "action" => value with { Changes = [value.Changes[0] with { Action = (InventoryBalanceAction)999 }] },
            _ => throw new InvalidOperationException()
        } };
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        await Assert.ThrowsAsync<DomainValidationException>(() => Ledger(data, reader).ApplyAsync(tx, Actor, Source, 1, Metadata));
        Assert.False(tx.IsInTransaction); await AssertUnchanged(data);
    }
    [Fact]
    public async Task Cancellation_after_fencing_the_source_rolls_back_the_whole_command()
    {
        await using var data = await Data(); using var cancellation = new CancellationTokenSource();
        var reader = new FixtureReader(data.Database) { Transform = value => { cancellation.Cancel(); return value; } };
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ledger(data, reader).ApplyAsync(tx, Actor, Source, 1, Metadata, cancellation.Token));
        Assert.False(tx.IsInTransaction); await AssertUnchanged(data);
    }
    [Fact]
    public async Task Foreign_databases_clients_and_inactive_transactions_are_not_accepted()
    {
        await using var data = await Data();
        Assert.Throws<ArgumentException>(() => Ledger(data, new FixtureReader(fixture.Client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName + "_other"))));
        var foreignClient = new MongoClient(fixture.Client.Settings);
        Assert.Throws<ArgumentException>(() => Ledger(data, new FixtureReader(foreignClient.GetDatabase(data.Database.DatabaseNamespace.DatabaseName))));
        Assert.Throws<ArgumentException>(() => Ledger(data, Reader(data), Reader(data)));
        using var inactive = await fixture.Client.StartSessionAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => Ledger(data, Reader(data)).ApplyAsync(inactive, Actor, Source, 1, Metadata));
        using var foreign = await foreignClient.StartSessionAsync(); foreign.StartTransaction();
        await Assert.ThrowsAsync<DomainValidationException>(() => Ledger(data, Reader(data)).ApplyAsync(foreign, Actor, Source, 1, Metadata));
        Assert.False(foreign.IsInTransaction); await AssertUnchanged(data);
    }
    [Fact]
    public async Task Two_transactions_reading_the_same_source_before_fencing_cannot_apply_it_twice()
    {
        await using var data = await Data(); var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var arrivals = 0;
        var reader = new FixtureReader(data.Database) { BeforeFence = async () =>
        {
            if (Interlocked.Increment(ref arrivals) == 2) bothRead.TrySetResult();
            await bothRead.Task.WaitAsync(TimeSpan.FromSeconds(20));
        } };
        var ledger = Ledger(data, reader);
        async Task<bool> Attempt()
        {
            try { await Unit.ExecuteAsync((tx, ct) => ledger.ApplyAsync(tx, Actor, Source, 1, Metadata, ct), default); return true; }
            catch (PersistenceConflictException error) when (error.Code is "inventory_source_already_applied" or "version_conflict") { return false; }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.True(arrivals >= 2); Assert.Single(results, value => value);
        Assert.Equal(13, (await data.Database.GetCollection<BsonDocument>("stockBalances").Find(new BsonDocument()).SingleAsync())["onHand"].AsInt64);
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, (await data.Database.GetCollection<BsonDocument>("inventoryTestSources").Find(new BsonDocument()).SingleAsync())["fence"].AsInt64);
    }
    [Fact]
    public async Task A_source_approval_revoked_after_snapshot_read_conflicts_before_stock_can_commit()
    {
        await using var data = await Data();
        var reader = new FixtureReader(data.Database) { BeforeFence = async () => await data.Database.GetCollection<BsonDocument>("inventoryTestSources")
            .UpdateOneAsync(new BsonDocument("_id", 5000L), new BsonDocument("$set", new BsonDocument("approved", false))) };
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        await Assert.ThrowsAnyAsync<MongoException>(() => Ledger(data, reader).ApplyAsync(tx, Actor, Source, 1, Metadata));
        Assert.False(tx.IsInTransaction); await AssertUnchanged(data);
    }
    [Fact]
    public async Task Failure_of_the_real_accounting_boundary_rolls_back_the_prior_inventory_movement()
    {
        await using var data = await Data();
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        await Ledger(data, Reader(data)).ApplyAsync(tx, Actor, Source, 1, Metadata);
        var accounting = new PostJournal(data.Database, data.Clock, []);
        await Assert.ThrowsAsync<DomainValidationException>(() => accounting.PostAsync(tx, Actor,
            new JournalSource(Source.Namespace, Source.DocumentId, Source.EventKind, Source.Occurrence), 1,
            new AccountingWriteMetadata(Metadata.Reason, Metadata.CorrelationId)));
        Assert.False(tx.IsInTransaction); await AssertUnchanged(data);
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task One_source_event_covers_all_stock_legs_or_rolls_back_every_leg(bool staleSecond)
    {
        await using var data = await Data();
        var first = FixtureBalance();
        var second = first with { Id = 1001, Balance = first.Balance with { Key = first.Balance.Key with { SkuId = new(300), DefinitionId = new(301), LotId = new(302) } } };
        await data.Database.GetCollection<BsonDocument>("lots").InsertOneAsync(InventoryGraphTests.Lot(302, 300, 301));
        await data.Database.GetCollection<BsonDocument>("stockBalances").InsertOneAsync(InventoryBson.Balance(second));
        await data.Database.GetCollection<BsonDocument>("inventoryTestSources").UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument
            { { "secondPackages", 5L }, { "secondRevision", staleSecond ? 999L : 1L } }));
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await Earlier(data, tx);
        var ledger = Ledger(data, Reader(data));
        if (staleSecond)
        {
            await Assert.ThrowsAsync<PersistenceConflictException>(() => ledger.ApplyAsync(tx, Actor, Source, 1, Metadata));
            Assert.False(tx.IsInTransaction);
            Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("movements").CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords").CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(0, (await data.Database.GetCollection<BsonDocument>("inventoryTestSources").Find(new BsonDocument()).SingleAsync())["fence"].AsInt64);
        }
        else { Assert.Equal(2, (await ledger.ApplyAsync(tx, Actor, Source, 1, Metadata)).Balances.Length); await tx.CommitTransactionAsync(); }
        var balances = await data.Database.GetCollection<BsonDocument>("stockBalances").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(staleSecond ? 10 : 13, balances[0]["onHand"].AsInt64); Assert.Equal(staleSecond ? 10 : 15, balances[1]["onHand"].AsInt64);
        if (!staleSecond) Assert.Equal(2, (await data.Database.GetCollection<BsonDocument>("movements").Find(new BsonDocument()).SingleAsync())["legs"].AsBsonArray.Count);
    }
    [Theory]
    [InlineData("fabricated-result")] [InlineData("wrong-package")] [InlineData("wrong-location")] [InlineData("wrong-balance")]
    [InlineData("wrong-pool")] [InlineData("wrong-revision")] [InlineData("duplicate-leg")] [InlineData("invalid-before-buckets")]
    [InlineData("revision-overflow")] [InlineData("quantity-overflow")]
    public async Task Mongo_rejects_a_movement_with_impossible_or_inconsistent_stock_effects(string fault)
    {
        await using var data = await Data();
        await Unit.ExecuteAsync((tx, ct) => Ledger(data, Reader(data)).ApplyAsync(tx, Actor, Source, 1, Metadata, ct), default);
        var row = await data.Database.GetCollection<BsonDocument>("movements").Find(new BsonDocument()).SingleAsync();
        row["_id"] = 9999L; row["source"]["occurrence"] = 2L; row["sourceEvent"] = "test-receipt:5000:receive:2";
        var leg = row["legs"][0].AsBsonDocument;
        switch (fault)
        {
            case "fabricated-result": leg["after"]["onHand"] = 99L; break;
            case "wrong-package": leg["after"]["skuId"] = 999L; break;
            case "wrong-location": leg["after"]["location"]["warehouseId"] = 999L; break;
            case "wrong-balance": leg["after"]["_id"] = 999L; break;
            case "wrong-pool": leg["after"]["physicalPoolId"] = 999L; break;
            case "wrong-revision": leg["after"]["version"] = 3L; break;
            case "duplicate-leg": row["legs"].AsBsonArray.Add(leg.DeepClone()); break;
            case "invalid-before-buckets": leg["before"]["reserved"] = 11L; leg["after"]["reserved"] = 11L; break;
            case "revision-overflow": leg["before"]["version"] = long.MaxValue; leg["after"]["version"] = long.MaxValue; break;
            case "quantity-overflow": leg["before"]["onHand"] = long.MaxValue; leg["after"]["onHand"] = long.MaxValue; break;
        }
        var error = await Assert.ThrowsAsync<MongoWriteException>(() => data.Database.GetCollection<BsonDocument>("movements").InsertOneAsync(row));
        Assert.Equal(121, error.WriteError.Code);
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("movements").CountDocumentsAsync(new BsonDocument()));
    }
    [Theory]
    [InlineData("Receive")] [InlineData("Reserve")] [InlineData("Release")] [InlineData("Dispatch")]
    [InlineData("Hold")] [InlineData("ReleaseHold")] [InlineData("Expire")] [InlineData("ExpireHeld")] [InlineData("ReceiveHeld")]
    public async Task Every_supported_transition_has_a_valid_exact_database_movement(string action)
    {
        await using var data = await Data();
        var original = FixtureBalance();
        var before = original with { Balance = original.Balance with { OnHand = 100, Reserved = 20, Held = 20, Expired = 10 } };
        await data.Database.GetCollection<BsonDocument>("stockBalances").ReplaceOneAsync(new BsonDocument("_id", before.Id), InventoryBson.Balance(before));
        await data.Database.GetCollection<BsonDocument>("inventoryTestSources").UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("action", action)));
        var result = await Unit.ExecuteAsync((tx, ct) => Ledger(data, Reader(data)).ApplyAsync(tx, Actor, Source, 1, Metadata, ct), default);
        var expected = action switch
        {
            "Receive" => before.Balance with { OnHand = 103, Revision = 2 },
            "ReceiveHeld" => before.Balance with { OnHand = 103, Held = 23, Revision = 2 },
            "Reserve" => before.Balance with { Reserved = 23, Revision = 2 },
            "Release" => before.Balance with { Reserved = 17, Revision = 2 },
            "Dispatch" => before.Balance with { OnHand = 97, Reserved = 17, Revision = 2 },
            "Hold" => before.Balance with { Held = 23, Revision = 2 },
            "ReleaseHold" => before.Balance with { Held = 17, Revision = 2 },
            "Expire" => before.Balance with { Expired = 13, Revision = 2 },
            "ExpireHeld" => before.Balance with { Held = 17, Expired = 13, Revision = 2 },
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(expected, Assert.Single(result.Balances).Balance);
        var movement = await data.Database.GetCollection<BsonDocument>("movements").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(InventoryBson.Balance(before), movement["legs"][0]["before"]);
        Assert.Equal(InventoryBson.Balance(before with { Balance = expected }), movement["legs"][0]["after"]);
    }
    [Theory]
    [InlineData("Receive")] [InlineData("Reserve")] [InlineData("ReceiveHeld")]
    public async Task Exact_Int64_boundary_quantities_remain_valid_without_double_rounding(string action)
    {
        await using var data = await Data();
        var original = FixtureBalance();
        var before = original with { Balance = original.Balance with { OnHand = action == "Reserve" ? long.MaxValue : long.MaxValue - 3,
            Reserved = action == "Reserve" ? long.MaxValue - 3 : 0, Held = action == "ReceiveHeld" ? long.MaxValue - 3 : 0 } };
        await data.Database.GetCollection<BsonDocument>("stockBalances").ReplaceOneAsync(new BsonDocument("_id", before.Id), InventoryBson.Balance(before));
        await data.Database.GetCollection<BsonDocument>("inventoryTestSources").UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("action", action)));
        var result = await Unit.ExecuteAsync((tx, ct) => Ledger(data, Reader(data)).ApplyAsync(tx, Actor, Source, 1, Metadata, ct), default);
        Assert.Equal(long.MaxValue, action == "Reserve" ? result.Balances[0].Balance.Reserved : result.Balances[0].Balance.OnHand);
        if (action == "ReceiveHeld") { Assert.Equal(long.MaxValue, result.Balances[0].Balance.Held); Assert.Equal(0, StockBalancePolicy.AvailablePackages(result.Balances[0].Balance)); }
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("movements").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Malformed_source_headers_dates_and_numeric_types_cannot_enter_the_movement_history()
    {
        await using var data = await Data();
        await Unit.ExecuteAsync((tx, ct) => Ledger(data, Reader(data)).ApplyAsync(tx, Actor, Source, 1, Metadata, ct), default);
        var valid = await data.Database.GetCollection<BsonDocument>("movements").Find(new BsonDocument()).SingleAsync();
        foreach (var fault in new[] { "extra", "schema", "actor", "revision", "date", "source-id", "event-key", "missing", "empty-legs", "packages" })
        {
            var row = valid.DeepClone().AsBsonDocument;
            row["_id"] = 9999L; row["source"]["occurrence"] = 2L; row["sourceEvent"] = "test-receipt:5000:receive:2";
            switch (fault)
            {
                case "extra": row["unexpected"] = true; break;
                case "schema": row["schemaVersion"] = 1L; break;
                case "actor": row["actorId"] = 0L; break;
                case "revision": row["sourceRevision"] = 0L; break;
                case "date": row["recordedAt"] = new BsonDateTime(long.MaxValue); break;
                case "source-id": row["source"]["documentId"] = 5000.0; break;
                case "event-key": row["sourceEvent"] = "test-receipt:5000:receive:002"; break;
                case "missing": row.Remove("reason"); break;
                case "empty-legs": row["legs"] = new BsonArray(); break;
                case "packages": row["legs"][0]["packages"] = 3; break;
            }
            var error = await Assert.ThrowsAsync<MongoWriteException>(() => data.Database.GetCollection<BsonDocument>("movements").InsertOneAsync(row));
            Assert.Equal(121, error.WriteError.Code);
        }
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("movements").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task The_longest_canonical_source_key_preserves_exact_Int64_identifiers_in_Mongo()
    {
        await using var data = await Data();
        var source = new InventorySource(new string('N', 64), long.MaxValue, new string('E', 64), long.MaxValue);
        source.Validate(); Assert.Equal(169, source.EventKey.Length);
        var row = await data.Database.GetCollection<BsonDocument>("inventoryTestSources").Find(new BsonDocument()).SingleAsync();
        await data.Database.GetCollection<BsonDocument>("inventoryTestSources").DeleteOneAsync(new BsonDocument("_id", 5000L));
        row["_id"] = source.DocumentId; row["namespace"] = source.Namespace; row["eventKind"] = source.EventKind; row["occurrence"] = source.Occurrence;
        await data.Database.GetCollection<BsonDocument>("inventoryTestSources").InsertOneAsync(row);
        var reader = new FixtureReader(data.Database) { Namespace = source.Namespace };
        var receipt = await Unit.ExecuteAsync((tx, ct) => Ledger(data, reader).ApplyAsync(tx, Actor, source, 1, Metadata, ct), default);
        Assert.Equal(source.EventKey, receipt.SourceEvent);
        var movement = await data.Database.GetCollection<BsonDocument>("movements").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(long.MaxValue, movement["source"]["documentId"].AsInt64); Assert.Equal(long.MaxValue, movement["source"]["occurrence"].AsInt64);
    }
    internal sealed class FixtureReader(IMongoDatabase database) : IInventorySourceReader
    {
        public string Namespace { get; init; } = "test-receipt";
        public IMongoDatabase Database => database;
        internal Func<Task>? BeforeFence { get; init; }
        internal Func<InventorySourceSnapshot, InventorySourceSnapshot> Transform { get; init; } = value => value;
        public async Task<InventorySourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, InventorySource source, long expectedRevision, CancellationToken ct)
        {
            var rows = database.GetCollection<BsonDocument>("inventoryTestSources");
            var row = await rows.Find(tx, new BsonDocument("_id", source.DocumentId)).SingleOrDefaultAsync(ct);
            if (row is null || row["revision"].AsInt64 != expectedRevision || !row["approved"].AsBoolean || row["actorId"].AsInt64 != actor.UserId)
                throw new DomainValidationException("synthetic_source_denied", "Synthetic fixture source is not approved for this actor/revision");
            if (BeforeFence is not null) await BeforeFence();
            var written = await rows.UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct);
            Assert.Equal(1, written.ModifiedCount);
            var actualSource = new InventorySource(row["namespace"].AsString, row["_id"].AsInt64, row["eventKind"].AsString, row["occurrence"].AsInt64);
            var balance = InventoryBson.ReadBalance(await database.GetCollection<BsonDocument>("stockBalances").Find(tx, new BsonDocument("_id", 1000L)).SingleAsync(ct));
            if (balance.PhysicalPoolId != row["poolId"].AsInt64) throw new DomainValidationException("synthetic_source_denied", "Synthetic source does not own the configured pool");
            var changes = ImmutableArray.CreateBuilder<InventoryBalanceChange>();
            changes.Add(new(balance.Id, balance.PhysicalPoolId, balance.Balance.Key, 1, Enum.Parse<InventoryBalanceAction>(row.GetValue("action", "Receive").AsString), row["packages"].AsInt64));
            if (row.Contains("secondPackages")) changes.Add(new(1001, balance.PhysicalPoolId,
                balance.Balance.Key with { SkuId = new(300), DefinitionId = new(301), LotId = new(302) },
                row["secondRevision"].AsInt64, InventoryBalanceAction.Receive, row["secondPackages"].AsInt64));
            return Transform(new(actualSource, row["revision"].AsInt64, new(balance.PhysicalPoolId, balance.Balance.Key.Location, row["poolRevision"].AsInt64), changes.ToImmutable()));
        }
    }
}
