using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockCountTests
{
    [Theory]
    [InlineData("record", false)] [InlineData("record", true)]
    [InlineData("recover", false)] [InlineData("recover", true)]
    [InlineData("read", false)] [InlineData("read", true)]
    [InlineData("list", false)] [InlineData("list", true)]
    public async Task Final_inventory_await_rechecks_session_expiry_or_cancellation(string operation, bool cancel)
    {
        await using var data = await Setup();
        var saved = await Service(data).RecordCountAsync(data.WarehouseActor, Input(data), Metadata("before-expiry"));
        var deadline = data.Clock.UtcNow.AddSeconds(1);
        await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.WarehouseActor.Session!.Id),
            Builders<BsonDocument>.Update.Set("idleExpiresAt", deadline.UtcDateTime));
        using var cancellation = new CancellationTokenSource();
        var selected = 0;
        var observed = 0;
        var target = operation == "record" ? 2 : 1;
        var settings = fixture.Client.Settings.Clone();
        settings.ApplicationName = "StockCountFinalAwait-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.GetValue("find", BsonNull.Value) == "identitySessions" &&
                    command.Command.TryGetValue("projection", out var projection) &&
                    projection.AsBsonDocument.Contains("idleExpiresAt") && projection.AsBsonDocument.Contains("absoluteExpiresAt"))
                    selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (selected == command.RequestId && Interlocked.Increment(ref observed) == target)
                {
                    if (cancel) cancellation.Cancel();
                    else data.Clock.UtcNow = deadline;
                }
            });
        };
        var client = new MongoClient(settings);
        var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new StockCountService(db, new(client), new(db), data.Clock);
        Task Invoke() => operation switch
        {
            "record" => service.RecordCountAsync(data.WarehouseActor, Input(data), Metadata("after-expiry"), cancellation.Token),
            "recover" => service.RecoverAsync(data.WarehouseActor, "before-expiry", cancellation.Token),
            "read" => service.ReadAsync(data.WarehouseActor, saved.Id, cancellation.Token),
            _ => service.ListAsync(data.WarehouseActor, Warehouse, 0, 10, cancellation.Token)
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Invoke);
        else await Assert.ThrowsAsync<AuthorizationDeniedException>(Invoke);
        Assert.Equal(target, observed);
        Assert.Equal(1, await data.CountAsync("stockCountObservations"));
        Assert.Equal(1, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "inventory.stock-count.recorded")));
        Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "inventory.stock-count.record")));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Recovery_racing_revocation_cannot_return_old_authority(bool revokeSession)
    {
        await using var data = await Setup();
        await Service(data).RecordCountAsync(data.WarehouseActor, Input(data), Metadata("revoke"));
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = 0;
        var observed = 0;
        var settings = fixture.Client.Settings.Clone();
        settings.ApplicationName = "StockCountRevoke-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.GetValue("find", BsonNull.Value) == "idempotencyRecords") selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (selected == command.RequestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0)
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Count recovery barrier expired");
                }
            });
        };
        var client = new MongoClient(settings);
        var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var pending = Task.Run(() => new StockCountService(db, new(client), new(db), data.Clock).RecoverAsync(data.WarehouseActor, "revoke"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (revokeSession) await data.Sessions.RevokeAsync(data.Issued[data.WarehouseActor.UserId].OpaqueToken, "synthetic count race");
            else await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.WarehouseActor.UserId),
                Builders<BsonDocument>.Update.Set("permissions", new BsonArray { "stock.read" }));
        }
        finally { release.Set(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        Assert.Equal(1, observed);
        Assert.Equal(1, await data.CountAsync("stockCountObservations"));
    }

    [Fact]
    public async Task Definition_change_after_receipt_write_rolls_back_entire_count()
    {
        await using var data = await Setup();
        var input = Input(data);
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = 0;
        var observed = 0;
        var settings = fixture.Client.Settings.Clone();
        settings.ApplicationName = "StockCountDefinitionRace-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            { if (command.Command.GetValue("insert", BsonNull.Value) == "idempotencyRecords") selected = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (selected == command.RequestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0)
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Count definition barrier expired");
                }
            });
        };
        var client = new MongoClient(settings);
        var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var pending = Task.Run(() => new StockCountService(db, new(client), new(db), data.Clock).RecordCountAsync(data.WarehouseActor, input, Metadata("definition-race")));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await data.Service.ReviseDefinitionAsync(data.Maker, new(new(input.SkuId), 1, Definition(label: "Changed")), Metadata("change-during-count"));
        }
        finally { release.Set(); }
        await Assert.ThrowsAsync<PersistenceConflictException>(() => pending);
        Assert.Equal(1, observed);
        Assert.Equal(0, await data.CountAsync("stockCountObservations"));
        Assert.Equal(0, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "inventory.stock-count.recorded")));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "inventory.stock-count.record")));
    }

    [Fact]
    public async Task Actual_unknown_commit_recovers_original_count_and_does_not_repeat_effects()
    {
        await using var data = await Setup();
        var input = Input(data);
        var settings = fixture.Client.Settings.Clone();
        settings.ApplicationName = "StockCountUnknown-" + Guid.NewGuid().ToString("N");
        var commits = 0;
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command =>
        { if (command.CommandName == "commitTransaction") Interlocked.Increment(ref commits); });
        var client = new MongoClient(settings);
        var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new StockCountService(db, new(client), new(db), data.Clock);
        UnitToko.Modules.Inventory.StockCountObservation original;
        try
        {
            await fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "configureFailPoint", "failCommand" }, { "mode", "alwaysOn" }, { "data", new BsonDocument
                    {
                        { "appName", settings.ApplicationName }, { "failCommands", new BsonArray { "commitTransaction" } },
                        { "writeConcernError", new BsonDocument { { "code", 64 }, { "errmsg", "synthetic count uncertain commit" } } },
                        { "errorLabels", new BsonArray { "UnknownTransactionCommitResult" } }
                    } }
            });
            await Assert.ThrowsAsync<UncertainCommitException>(() => service.RecordCountAsync(data.WarehouseActor, input, Metadata("uncertain-count")));
            Assert.True(commits >= MongoUnitOfWork.MaximumAttempts);
            original = Assert.IsType<UnitToko.Modules.Inventory.StockCountObservation>(await Service(data).RecoverAsync(data.WarehouseActor, "uncertain-count"));
        }
        finally
        {
            await fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                new BsonDocument { { "configureFailPoint", "failCommand" }, { "mode", "off" } });
        }
        Assert.Equal(original, await service.RecordCountAsync(data.WarehouseActor, input, Metadata("uncertain-count")));
        Assert.Equal(1, await data.CountAsync("stockCountObservations"));
        Assert.Equal(1, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "inventory.stock-count.recorded")));
    }
}
