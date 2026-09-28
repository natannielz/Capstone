using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

public sealed partial class CatalogStockCountChoiceTests
{
    [Theory]
    [InlineData("grant", false)]
    [InlineData("membership", false)]
    [InlineData("session", false)]
    [InlineData("grant", true)]
    [InlineData("membership", true)]
    [InlineData("session", true)]
    public async Task Expiry_after_final_database_response_never_returns_choices(string expiry, bool warehouseOptions)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); await NamesAsync(data); await data.SkuAsync();
        await PermissionAsync(data, data.WarehouseActor, Permissions.StockCount);
        var actor = data.WarehouseActor; var deadline = data.Clock.UtcNow.AddSeconds(1);
        if (expiry == "session") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", actor.Session!.Id), U.Set("idleExpiresAt", deadline.UtcDateTime));
        else await data.Rows(expiry == "grant" ? "authorizationGrants" : "memberships").UpdateOneAsync(new BsonDocument("_id", actor.UserId), U.Set("expiresAt", deadline.UtcDateTime));
        var selected = 0; var observed = 0; var settings = data.Client.Settings.Clone(); var configure = settings.ClusterConfigurator;
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
        var service = data.NewService(new MongoClient(settings)); var before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
        {
            if (warehouseOptions) await service.StockWarehouseOptionsAsync(actor, StockCountChoicePurpose.Count, CountAudit);
            else await service.CountablePackagesAsync(actor, Warehouse, 0, 25, null, CountAudit);
        });
        Assert.Equal(1, observed); Assert.Equal(before, await data.StateAsync());
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("membership")]
    [InlineData("session")]
    [InlineData("warehouse")]
    [InlineData("definition")]
    public async Task Concurrent_change_after_selection_is_fenced_before_return(string change)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); var sku = await data.SkuAsync();
        await PermissionAsync(data, data.WarehouseActor, Permissions.StockCount);
        var selected = 0; var observed = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = data.Client.Settings.Clone(); var configure = settings.ClusterConfigurator;
        settings.ClusterConfigurator = builder =>
        {
            configure?.Invoke(builder);
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.GetValue("aggregate", BsonNull.Value) == "skus") selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (selected != 0 && selected == command.RequestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0)
                {
                    entered.SetResult(); release.Task.Wait(TimeSpan.FromSeconds(30));
                }
            });
        };
        var service = data.NewService(new MongoClient(settings));
        var pending = service.CountablePackagesAsync(data.WarehouseActor, Warehouse, 0, 25, null, CountAudit);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "definition") await data.Service.ReviseDefinitionAsync(data.Maker, new(sku.Id, 1, Definition(label: "Paket baru")), Metadata("choice-definition-race"));
            else if (change == "session") await data.Sessions.RevokeAsync(data.Issued[data.WarehouseActor.UserId].OpaqueToken, "synthetic choice race");
            else if (change == "warehouse") await data.Rows("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 102L), U.Set("status", "suspended").Inc("version", 1L));
            else await data.Rows(change == "grant" ? "authorizationGrants" : "memberships").UpdateOneAsync(new BsonDocument("_id", data.WarehouseActor.UserId), U.Set("status", "revoked").Inc("version", 1L));
        }
        finally { release.TrySetResult(); }
        if (change == "definition")
        {
            var page = await pending.WaitAsync(TimeSpan.FromSeconds(30));
            var current = Assert.Single(page.Items); Assert.Equal(2, current.SkuRevision); Assert.Equal("Paket baru", current.PackageLabel);
        }
        else await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task Approved_profile_change_during_options_read_cannot_return_old_names()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); await NamesAsync(data);
        await PermissionAsync(data, data.WarehouseActor, Permissions.StockCount);
        var selected = 0; var observed = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = data.Client.Settings.Clone(); var configure = settings.ClusterConfigurator;
        settings.ClusterConfigurator = builder =>
        {
            configure?.Invoke(builder);
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.GetValue("find", BsonNull.Value) == "organizationProfiles") selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (selected != 0 && selected == command.RequestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0)
                {
                    entered.SetResult(); release.Task.Wait(TimeSpan.FromSeconds(30));
                }
            });
        };
        var pending = data.NewService(new MongoClient(settings)).StockWarehouseOptionsAsync(data.WarehouseActor, StockCountChoicePurpose.Count, CountAudit);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await new MongoUnitOfWork(data.Client).ExecuteAsync(async (tx, ct) =>
            {
                await data.Rows("organizationProfiles").UpdateOneAsync(tx, new BsonDocument("_id", 102L), U.Set("name", "Gudang Baru").Inc("version", 1L), cancellationToken: ct);
                await data.Rows("authorizationScopes").UpdateOneAsync(tx, new BsonDocument("_id", 102L), U.Inc("version", 1L), cancellationToken: ct);
                return true;
            });
        }
        finally { release.TrySetResult(); }
        Assert.Equal("Gudang Baru", Assert.Single(await pending.WaitAsync(TimeSpan.FromSeconds(30))).WarehouseName);
        Assert.Equal(1, observed);
    }
}
