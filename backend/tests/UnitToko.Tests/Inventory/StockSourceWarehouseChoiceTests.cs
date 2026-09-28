using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class StockSourceWarehouseChoiceTests(MongoFixture fixture)
{
    private static readonly StockLocation Location = new(new(100), new(101), new(102), new(103));

    [Theory]
    [InlineData("rename")]
    [InlineData("revoke")]
    [InlineData("suspend")]
    public async Task Concurrent_organization_or_access_change_cannot_return_obsolete_choices(string change)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(49001, Location.ToScope(), Permissions.StockSourcePropose);
        await Profiles(data);
        var captured = 0; var changed = 0;
        var settings = fixture.Client.Settings.Clone();
        settings.ApplicationName = "SourceChoiceRace-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.CommandName == "find" && command.Command.GetValue("find", "") == "organizationProfiles")
                    captured = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (command.RequestId != captured || Interlocked.CompareExchange(ref changed, 1, 0) != 0) return;
                new MongoUnitOfWork(fixture.Client).ExecuteAsync(async (tx, ct) =>
                {
                    if (change == "rename")
                    {
                        // The organization writer changes profile and scope versions in one transaction.
                        await Rows(data, "organizationProfiles").UpdateOneAsync(tx, new BsonDocument("_id", 102L),
                            Builders<BsonDocument>.Update.Set("name", "Updated warehouse").Inc("version", 1L), cancellationToken: ct);
                        await Rows(data, "authorizationScopes").UpdateOneAsync(tx, new BsonDocument("_id", 102L),
                            Builders<BsonDocument>.Update.Inc("version", 1L).Inc("fence", 1L), cancellationToken: ct);
                    }
                    else if (change == "revoke")
                        await Rows(data, "authorizationGrants").UpdateOneAsync(tx, new BsonDocument("_id", actor.UserId),
                            Builders<BsonDocument>.Update.Set("status", "revoked"), cancellationToken: ct);
                    else
                        await Rows(data, "authorizationScopes").UpdateOneAsync(tx, new BsonDocument("_id", 102L),
                            Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L), cancellationToken: ct);
                    return true;
                }).GetAwaiter().GetResult();
            });
        };
        var client = new MongoClient(settings);
        var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new StockSourceSetupService(database, new(client), new(database), data.Clock);
        if (change == "rename")
        {
            var choices = await service.WarehousesAsync(actor, "inventory-source-propose", "choice-race");
            Assert.Equal("Updated warehouse", Assert.Single(choices).WarehouseName);
            Assert.Equal(1, await AuditCount(data));
        }
        else
        {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
                service.WarehousesAsync(actor, "inventory-source-propose", "choice-race"));
            Assert.Equal(0, await AuditCount(data));
        }
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Review_factor_expiring_after_audit_insertion_rolls_back_the_read_and_fences()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(49001, Location.ToScope(), Permissions.StockSourceApprove);
        await Profiles(data);
        var changed = 0;
        var settings = fixture.Client.Settings.Clone();
        settings.ApplicationName = "SourceChoiceExpiry-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(command =>
        {
            if (command.CommandName == "insert" && Interlocked.CompareExchange(ref changed, 1, 0) == 0)
                data.Clock.UtcNow += TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(1);
        });
        var client = new MongoClient(settings);
        var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new StockSourceSetupService(database, new(client), new(database), data.Clock);
        var before = await Rows(data, "identities").Find(new BsonDocument("_id", actor.UserId)).SingleAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.WarehousesAsync(actor, "inventory-source-review", "expiry"));
        Assert.Equal(1, changed); Assert.Equal(0, await AuditCount(data));
        Assert.Equal(before, await Rows(data, "identities").Find(new BsonDocument("_id", actor.UserId)).SingleAsync());
    }

    [Fact]
    public async Task Active_shop_with_no_active_warehouses_returns_an_audited_empty_choice_list()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(49001, new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103), Permissions.StockSourceRead);
        await Rows(data, "authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 102L), Builders<BsonDocument>.Update.Set("status", "suspended"));
        var service = new StockSourceSetupService(data.Database, new(fixture.Client), new(data.Database), data.Clock);
        Assert.Empty(await service.WarehousesAsync(actor, "inventory-source-history", "empty"));
        Assert.Equal(1, await AuditCount(data));
    }

    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private static Task<long> AuditCount(StoreTestData data) =>
        Rows(data, "auditEvents").CountDocumentsAsync(new BsonDocument("operation", "inventory.source.warehouses"));
    private static async Task Profiles(StoreTestData data)
    {
        foreach (var node in await Rows(data, "authorizationScopes").Find(new BsonDocument("_id", new BsonDocument("$gte", 100L))).ToListAsync())
        {
            var id = node["_id"].AsInt64;
            await Rows(data, "organizationProfiles").InsertOneAsync(new BsonDocument
            {
                { "_id", id }, { "schemaVersion", 1 }, { "kind", node["kind"] }, { "parentId", node.GetValue("parentId", BsonNull.Value) },
                { "code", "NODE-" + id }, { "normalizedCode", "NODE-" + id }, { "name", "Synthetic node " + id }, { "version", 1L },
                { "createdBy", 998L }, { "updatedBy", 999L }, { "createdAt", data.Clock.UtcNow.UtcDateTime },
                { "updatedAt", data.Clock.UtcNow.UtcDateTime }, { "approvedProposalId", id + 10000 }
            });
        }
    }
}
