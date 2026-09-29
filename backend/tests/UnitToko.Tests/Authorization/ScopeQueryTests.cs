using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class ScopeQueryTests(MongoFixture fixture)
{
    [Fact]
    public async Task Courier_lists_and_direct_reads_require_assignment_and_correct_fulfillment_scope()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var courier = await data.AddActorAsync(1000, new(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103), Permissions.ShipmentRead);
        var shipments = data.Database.GetCollection<BsonDocument>("shipments");
        for (var id = 1L; id <= 3; id++) await shipments.InsertOneAsync(new BsonDocument
        {
            { "_id", id }, { "schemaVersion", 1 }, { "version", 1L }, { "fulfillmentOrganizationId", 100L },
            { "shopId", 101L }, { "channelId", id == 3 ? 999L : 103L }, { "assignedUserIds", new BsonArray { id == 2 ? 1001L : courier.UserId } }
        });
        var page = await data.Store.QueryAsync(courier, Permissions.ShipmentRead, shipments, 0, 1);
        Assert.Equal(1, page.Total);
        Assert.Equal(1L, Assert.Single(page.Items)["_id"].AsInt64);
        Assert.Null(await data.Store.ReadAsync(courier, Permissions.ShipmentRead, shipments, 2));
        Assert.Null(await data.Store.ReadAsync(courier, Permissions.ShipmentRead, shipments, 3));
    }

    [Fact]
    public async Task Malformed_scalar_and_array_scope_and_ownership_values_cannot_match_authorized_queries()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(1000, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        await data.AddResourceAsync(1, 10, 11, actor.UserId);
        var collection = data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords");
        var original = await collection.Find(new BsonDocument("_id", 1L)).FirstAsync();
        var variants = new[]
        {
            ("buyingOrganizationId", (BsonValue)new BsonArray { 10L, 20L }),
            ("branchId", (BsonValue)new BsonArray { 11L, 21L }),
            ("createdBy", (BsonValue)new BsonArray { actor.UserId, 1001L }),
            ("buyingOrganizationId", (BsonValue)new BsonInt32(10)),
            ("isAdministrative", (BsonValue)"false"),
            ("isAdministrative", (BsonValue)BsonNull.Value),
            ("assignedUserIds", (BsonValue)BsonNull.Value)
        };
        long id = 2;
        foreach (var (field, value) in variants)
        {
            var malformed = original.DeepClone().AsBsonDocument; malformed["_id"] = id++; malformed[field] = value;
            await collection.InsertOneAsync(malformed);
            Assert.Throws<AuthorizationDeniedException>(() => AuthorizationBson.Resource(malformed));
        }
        var scalarAssignment = original.DeepClone().AsBsonDocument; scalarAssignment["_id"] = id; scalarAssignment["createdBy"] = 1001L; scalarAssignment["assignedUserIds"] = actor.UserId;
        await collection.InsertOneAsync(scalarAssignment);
        Assert.Throws<AuthorizationDeniedException>(() => AuthorizationBson.Resource(scalarAssignment));
        var page = await data.Store.QueryAsync(actor, Permissions.OrderRead, collection, 0, 20);
        Assert.Equal(1, page.Total);
        Assert.Single(page.Items);
        for (var rejected = 2L; rejected <= id; rejected++) Assert.Null(await data.Store.ReadAsync(actor, Permissions.OrderRead, collection, rejected));
    }

    [Fact]
    public async Task Own_and_assigned_records_are_filtered_before_count_and_pagination_and_direct_id_read()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(1000, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        await data.AddResourceAsync(1, 10, 11, 1001);
        await data.AddResourceAsync(2, 10, 11, actor.UserId);
        await data.AddResourceAsync(3, 10, 11, 1001, [actor.UserId]);
        await data.AddResourceAsync(4, 10, 12, actor.UserId);
        var collection = data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords");
        var result = await data.Store.QueryAsync(actor, Permissions.OrderRead, collection, 0, 1);
        Assert.Equal(2, result.Total);
        Assert.Equal(2L, Assert.Single(result.Items)["_id"].AsInt64);
        Assert.Null(await data.Store.ReadAsync(actor, Permissions.OrderRead, collection, 4));
        Assert.NotNull(await data.Store.ReadAsync(actor, Permissions.OrderRead, collection, 3));
    }

    [Fact]
    public async Task Multiple_memberships_remain_complete_disjunctions_and_unavailable_branch_is_excluded()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(1000, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.OrderRead, Permissions.BranchPic);
        await data.AddGrantAsync(actor, 1001, new(GrantScopeKind.BuyingBranch, 20, 21), [Permissions.OrderRead, Permissions.BranchPic]);
        await data.AddResourceAsync(1, 10, 11, 1002);
        await data.AddResourceAsync(2, 20, 21, 1002);
        await data.AddResourceAsync(3, 10, 21, 1002); // Invalid cross-product of otherwise authorized IDs.
        await data.AddResourceAsync(4, 10, 12, 1002);
        var collection = data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords");
        Assert.Equal(2, (await data.Store.QueryAsync(actor, Permissions.OrderRead, collection, 0, 20)).Total);
        await data.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 21L),
            Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L).Inc("fence", 1L));
        Assert.Single((await data.Store.QueryAsync(actor, Permissions.OrderRead, collection, 0, 20)).Items);
        Assert.Null(await data.Store.ReadAsync(actor, Permissions.OrderRead, collection, 2));
    }

    [Fact]
    public async Task Identity_binding_suspension_and_stale_versions_fail_closed()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(1000, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.ReadCurrentAsync(actor with { ProviderSubject = "different" }));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.ReadCurrentAsync(actor with { GrantVersion = 0 }));
        await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
            Builders<BsonDocument>.Update.Set("status", "suspended").Inc("grantVersion", 1L).Inc("fence", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.ReadCurrentAsync(actor with { GrantVersion = 2 }));
    }
}
