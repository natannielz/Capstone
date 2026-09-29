using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSuspensionTests
{
    [Theory]
    [InlineData("owner-grant")] [InlineData("linked-grant")] [InlineData("membership")] [InlineData("mfa")]
    public async Task Final_authority_change_rolls_back_pool_stop_event_audit_and_receipt(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (_, _, actor, pool) = await Setup(data, true); var service = Service(data);
        await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument("_id", pool), Builders<BsonDocument>.Update.Set("active", true));
        var expected = Expected(await service.PreviewSuspensionAsync(actor, pool)); var before = await Pool(data, pool);
        var captured = 0; var changed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "SuspendFinal-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "insert" && e.Command.GetValue("insert", "") == "auditEvents" &&
                    e.Command["documents"].AsBsonArray[0]["operation"] == "stock.source.suspend") captured = e.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.RequestId != captured || Interlocked.CompareExchange(ref changed, 1, 0) != 0) return;
                if (fault == "mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(5).AddMilliseconds(1);
                else if (fault == "membership") Rows(data, "memberships").UpdateOne(new BsonDocument("_id", 3001L), Builders<BsonDocument>.Update.Set("status", "revoked"));
                else Rows(data, "authorizationGrants").UpdateOne(new BsonDocument("_id", fault == "owner-grant" ? 3000L : 3001L), Builders<BsonDocument>.Update.Set("status", "revoked"));
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new StockSourceSetupService(db, new(client), new(db), data.Clock).SuspendAsync(actor, pool, expected, Meta("rollback")));
        Assert.Equal(1, changed); Assert.Equal(before, await Pool(data, pool));
        Assert.Equal(0L, await Count(data, StockSourceSuspensionSchemas.Collection));
        Assert.Equal(4L, await Count(data, "auditEvents")); Assert.Equal(4L, await Count(data, "idempotencyRecords"));
    }

    [Fact]
    public async Task Concurrent_suspensions_from_one_review_record_exactly_one_stop()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (_, _, actor, pool) = await Setup(data); var service = Service(data);
        await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument("_id", pool), Builders<BsonDocument>.Update.Set("active", true));
        var expected = Expected(await service.PreviewSuspensionAsync(actor, pool));
        using var barrier = new Barrier(2); var arrivals = 0;
        StockSourceSetupService Racing()
        {
            var captured = 0; var held = 0;
            var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "SuspendRace-" + Guid.NewGuid().ToString("N");
            settings.ClusterConfigurator = builder =>
            {
                builder.Subscribe<CommandStartedEvent>(e =>
                {
                    if (e.CommandName == "find" && e.Command.GetValue("find", "") == "inventoryPools" && e.Command.Contains("projection")) captured = e.RequestId;
                });
                builder.Subscribe<CommandSucceededEvent>(e =>
                {
                    if (e.RequestId != captured || Interlocked.CompareExchange(ref held, 1, 0) != 0) return;
                    Interlocked.Increment(ref arrivals);
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Both suspension transactions must load their original snapshot.");
                });
            };
            var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
            return new(db, new(client), new(db), data.Clock);
        }
        async Task<bool> Attempt(StockSourceSetupService candidate, string key)
        {
            try { await candidate.SuspendAsync(actor, pool, expected, Meta(key)); return true; }
            catch (PersistenceConflictException) { return false; }
        }
        var result = await Task.WhenAll(Task.Run(() => Attempt(Racing(), "stop-a")), Task.Run(() => Attempt(Racing(), "stop-b")));
        Assert.Equal(2, arrivals); Assert.Single(result, value => value);
        Assert.False((await Pool(data, pool))["active"].AsBoolean); Assert.Equal(1L, (await Pool(data, pool))["fence"].AsInt64);
        Assert.Equal(1L, await Count(data, StockSourceSuspensionSchemas.Collection));
        Assert.Equal(3L, await Count(data, "auditEvents")); Assert.Equal(3L, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Suspension_and_real_canonical_stock_writes_share_the_same_transaction_fence(bool suspensionWins)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (_, _, actor, pool) = await Setup(data); var service = Service(data);
        await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument("_id", pool), Builders<BsonDocument>.Update.Set("active", true));
        await InventoryGraphTests.InstallBalances(data); await Rows(data, "lots").InsertOneAsync(InventoryGraphTests.Lot());
        var balance = InventoryGraphTests.Balance() with { PhysicalPoolId = pool };
        await Rows(data, "stockBalances").InsertOneAsync(InventoryBson.Balance(balance));
        var change = new InventoryBalanceChange(balance.Id, pool, balance.Balance.Key, 1, InventoryBalanceAction.Receive, 1);
        var expected = Expected(await service.PreviewSuspensionAsync(actor, pool));
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); var captured = 0; var held = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "SuspendWriterRace-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "find" && e.Command.GetValue("find", "") == "inventoryPools" &&
                    e.Command.Contains("projection") != suspensionWins) captured = e.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.RequestId != captured || Interlocked.CompareExchange(ref held, 1, 0) != 0) return;
                loaded.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The winning transaction must complete before the older snapshot resumes.");
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        if (suspensionWins)
        {
            var pending = Task.Run(() => new MongoUnitOfWork(client).ExecuteAsync((tx, ct) => new InventoryRepository(db).ApplyAsync(tx, [change], ct)));
            try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30)); await service.SuspendAsync(actor, pool, expected, Meta("winner")); }
            finally { release.Set(); }
            await Assert.ThrowsAsync<DomainValidationException>(() => pending);
        }
        else
        {
            var pending = Task.Run(() => new StockSourceSetupService(db, new(client), new(db), data.Clock).SuspendAsync(actor, pool, expected, Meta("after-writer")));
            try
            {
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await new MongoUnitOfWork(fixture.Client).ExecuteAsync((tx, ct) => new InventoryRepository(data.Database).ApplyAsync(tx, [change], ct));
            }
            finally { release.Set(); }
            Assert.Equal("suspended", (await pending)["status"].AsString);
        }
        Assert.Equal(1, held); Assert.False((await Pool(data, pool))["active"].AsBoolean);
        var stored = InventoryBson.ReadBalance(await Rows(data, "stockBalances").Find(new BsonDocument("_id", balance.Id)).SingleAsync());
        Assert.Equal(suspensionWins ? 10 : 11, stored.Balance.OnHand);
        Assert.Equal(suspensionWins ? 1 : 2, stored.Balance.Revision);
        Assert.Equal(1L, await Count(data, StockSourceSuspensionSchemas.Collection));
        await Assert.ThrowsAsync<DomainValidationException>(() => new MongoUnitOfWork(fixture.Client).ExecuteAsync((tx, ct) =>
            new InventoryRepository(data.Database).ApplyAsync(tx, [change with { ExpectedRevision = stored.Balance.Revision }], ct)));
    }

    [Fact]
    public async Task Receipt_deleted_between_recovery_read_and_replay_cannot_execute_a_replacement_stop()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (_, _, actor, pool) = await Setup(data); var service = Service(data);
        await service.SuspendAsync(actor, pool, Expected(await service.PreviewSuspensionAsync(actor, pool)), Meta("lost"));
        var before = await Pool(data, pool); var removed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "SuspendRecoveryRace-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(e =>
        {
            if (e.CommandName != "commitTransaction" || Interlocked.CompareExchange(ref removed, 1, 0) != 0) return;
            Rows(data, "idempotencyRecords").DeleteOne(new BsonDocument { { "operation", "stock.source.suspend" }, { "key", "lost" } });
        });
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAsync<DomainValidationException>(() => new StockSourceSetupService(db, new(client), new(db), data.Clock)
            .RecoverSuspensionAsync(actor, pool, "lost", "recovery"));
        Assert.Equal(1, removed); Assert.Equal(before, await Pool(data, pool));
        Assert.Equal(1L, await Count(data, StockSourceSuspensionSchemas.Collection));
        Assert.Equal(3L, await Count(data, "auditEvents")); Assert.Equal(2L, await Count(data, "idempotencyRecords"));
    }
}
