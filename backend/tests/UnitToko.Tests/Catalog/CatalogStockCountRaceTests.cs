using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

public sealed partial class CatalogStockCountDefinitionTests
{
    [Theory]
    [InlineData("definition")]
    [InlineData("session")]
    [InlineData("grant")]
    [InlineData("membership")]
    [InlineData("warehouse")]
    public async Task Concurrent_catalog_or_authority_change_prevents_observation_commit(string change)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await StockOnlyAsync(data);
        await data.Rows("stockCountProbe").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "value", 0 } });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var pending = new MongoUnitOfWork(data.Client).ExecuteAsync((tx, ct) => data.Service.WithStockCountDefinitionAsync(
            data.Database, tx, data.WarehouseActor, Warehouse, sku.Id.Value, 1, sku.CurrentDefinitionId!.Value.Value,
            async (_, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
                }
                await data.Rows("stockCountProbe").UpdateOneAsync(tx, new BsonDocument("_id", 1L), U.Inc("value", 1), cancellationToken: token);
                return 1;
            }, ct));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "definition")
                await data.Service.ReviseDefinitionAsync(data.Maker, new(sku.Id, 1, Definition(label: "Kemasan baru")), Metadata("count-definition-race"));
            else if (change == "session") await data.Sessions.RevokeAsync(data.Issued[data.WarehouseActor.UserId].OpaqueToken, "synthetic count race");
            else if (change == "warehouse")
                await data.Rows("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 102L), U.Set("status", "suspended").Inc("version", 1L));
            else await data.Rows(change == "grant" ? "authorizationGrants" : "memberships").UpdateOneAsync(
                new BsonDocument("_id", data.WarehouseActor.UserId), U.Set("status", "revoked").Inc("version", 1L));
        }
        finally { release.TrySetResult(); }
        if (change == "definition") await Assert.ThrowsAsync<PersistenceConflictException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
        else await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, calls); Assert.Equal(0, (await data.RowAsync("stockCountProbe", 1))["value"].AsInt32);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("membership")]
    [InlineData("session")]
    public async Task Authority_expiry_after_final_database_response_aborts_observation(string expiry)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await StockOnlyAsync(data); var actor = data.WarehouseActor;
        var deadline = data.Clock.UtcNow.AddSeconds(1);
        if (expiry == "session") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", actor.Session!.Id), U.Set("idleExpiresAt", deadline.UtcDateTime));
        else await data.Rows(expiry == "grant" ? "authorizationGrants" : "memberships").UpdateOneAsync(new BsonDocument("_id", actor.UserId), U.Set("expiresAt", deadline.UtcDateTime));
        await data.Rows("stockCountProbe").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "value", 0 } });
        var selected = 0; var observed = 0;
        var settings = data.Client.Settings.Clone();
        var configure = settings.ClusterConfigurator;
        settings.ClusterConfigurator = builder =>
        {
            configure?.Invoke(builder);
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.GetValue("find", BsonNull.Value) == "identitySessions" && command.Command.TryGetValue("projection", out var projection) &&
                    projection.AsBsonDocument.Contains("idleExpiresAt") && projection.AsBsonDocument.Contains("absoluteExpiresAt")) selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (selected != 0 && selected == command.RequestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) data.Clock.UtcNow = deadline;
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        using var tx = await client.StartSessionAsync(); tx.StartTransaction();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.NewService(client).WithStockCountDefinitionAsync(db, tx, actor,
            Warehouse, sku.Id.Value, 1, sku.CurrentDefinitionId!.Value.Value, async (_, ct) =>
            {
                await db.GetCollection<BsonDocument>("stockCountProbe").UpdateOneAsync(tx, new BsonDocument("_id", 1L), U.Inc("value", 1), cancellationToken: ct); return 1;
            }));
        Assert.Equal(1, observed); Assert.False(tx.IsInTransaction);
        Assert.Equal(0, (await data.RowAsync("stockCountProbe", 1))["value"].AsInt32);
    }

    [Fact]
    public async Task Cancellation_aborts_caller_writes_with_an_independent_cleanup_token()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await StockOnlyAsync(data);
        using var cancellation = new CancellationTokenSource();
        using var tx = await data.Client.StartSessionAsync(); tx.StartTransaction();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => data.Service.WithStockCountDefinitionAsync(data.Database, tx,
            data.WarehouseActor, Warehouse, sku.Id.Value, 1, sku.CurrentDefinitionId!.Value.Value, async (_, ct) =>
            {
                await data.Rows("stockCountProbe").InsertOneAsync(tx, new BsonDocument("_id", 1L), cancellationToken: ct);
                cancellation.Cancel(); return 1;
            }, cancellation.Token));
        Assert.False(tx.IsInTransaction); Assert.Equal(0, await data.CountAsync("stockCountProbe"));
    }
}
