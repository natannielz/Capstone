using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class BusinessPermissionStoreTests(MongoFixture fixture)
{
    private static AuthorizationCommandMetadata Metadata(string key) => new(key, "Reviewed business authority", "business-permission-test");

    [Fact]
    public async Task Suggested_permission_requires_explicit_selection_delegation_and_independent_approval()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.BuyerOwned, 10, 11);
        var maker = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessPropose);
        var subject = await data.AddActorAsync(1001, scope, Permissions.OrderRead);
        var checker = await data.AddActorAsync(1002, scope, Permissions.AccessApprove);
        var requested = RoleTemplates.Materialize("buyer", [], [Permissions.SubstitutionAccept]).Permissions;
        var request = new GrantChangeRequest(subject.UserId, subject.UserId, "buyer", [],
            [Permissions.SubstitutionAccept], scope, data.Clock.UtcNow);
        var preview = await data.Administration.PreviewGrantAsync(maker, request);
        Assert.Equal(1, preview.TemplateVersion);
        Assert.Contains(Permissions.SubstitutionAccept, preview.Permissions);
        Assert.DoesNotContain(Permissions.SubstitutionAccept, preview.ExistingPermissions);
        var proposal = await data.Administration.ProposeGrantAsync(maker, request, Metadata("business-proposal"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Administration.ApproveGrantAsync(checker, proposal.Id, 1, Metadata("without-delegation")));
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("authorizationGrants")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("requestedBy", maker.UserId)));
        await data.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", checker.UserId),
            Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray(requested)));
        var approved = await data.Administration.ApproveGrantAsync(checker, proposal.Id, 1, Metadata("approved"));
        Assert.Equal(2, approved.GrantVersion);
        var saved = await data.Database.GetCollection<BsonDocument>("authorizationGrants")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", approved.Id)).SingleAsync();
        Assert.Equal(1, saved["templateVersion"].AsInt32);
        Assert.Contains(Permissions.SubstitutionAccept, saved["permissions"].AsBsonArray.Select(item => item.AsString));
        var current = await data.Store.ReadCurrentAsync(subject with { GrantVersion = 2 });
        Assert.Contains(current.Grants, grant => grant.Id == approved.Id && grant.Permissions.Contains(Permissions.SubstitutionAccept));
        Assert.DoesNotContain(Permissions.SubstitutionAccept,
            current.Grants.Single(grant => grant.Id == subject.UserId).Permissions);
    }
}
