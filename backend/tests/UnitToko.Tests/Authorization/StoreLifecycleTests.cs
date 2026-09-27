using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class StoreLifecycleTests(MongoFixture fixture)
{
    private static AuthorizationCommandMetadata Metadata(string key) => new(key, "Reviewed lifecycle request", "lifecycle-test");

    [Fact]
    public async Task Deactivated_branch_permissions_can_be_revoked_by_active_organization_authority()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var technical = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessRevoke);
        var owner = await data.AddActorAsync(1001, new(GrantScopeKind.BuyingOrganization, 10), Permissions.AccessRevoke);
        var subject = await data.AddActorAsync(1002, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        await data.Administration.SuspendScopeAsync(technical, 11, 1, Metadata("scope-off"));
        var revoked = await data.Administration.RevokeGrantAsync(owner, subject.UserId, 1, Metadata("revoke"));
        Assert.Equal("revoked", revoked.Status);
        var suspended = await data.Administration.SuspendMembershipAsync(owner, subject.UserId, 1, Metadata("suspend-member"));
        Assert.Equal("suspended", suspended.Status);
        var scope = await data.Database.GetCollection<BsonDocument>("authorizationScopes").Find(new BsonDocument("_id", 11L)).FirstAsync();
        var history = await data.Database.GetCollection<BsonDocument>("authorizationHistory").Find(new BsonDocument("operation", "access.scope.suspended")).FirstAsync();
        Assert.Equal(scope["fence"], history["after"]["fence"]);
    }

    [Fact]
    public async Task Membership_needs_independent_owner_and_suspended_account_can_have_access_cleaned_up()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var technical = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.MembershipPropose, Permissions.AccessRevoke, Permissions.AccessAuditRead);
        var subject = await data.AddActorAsync(1001, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var owner = await data.AddActorAsync(1002, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.MembershipApprove, Permissions.AccessRevoke);
        var request = new MembershipChangeRequest(subject.UserId, new(GrantScopeKind.BuyingBranch, 10, 11), data.Clock.UtcNow, data.Clock.UtcNow.AddDays(30));
        var proposal = await data.Administration.ProposeMembershipAsync(technical, request, Metadata("propose"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ApproveMembershipAsync(technical, proposal.Id, 1, Metadata("self")));
        var pending = Assert.Single(await data.Administration.ListProposalsAsync(owner, 0, 10));
        Assert.Equal(new[] { "approve" }, pending.AllowedActions.ToArray());
        Assert.Equal(request.ExpiresAt, pending.ExpiresAt);
        var membership = await data.Administration.ApproveMembershipAsync(owner, proposal.Id, 1, Metadata("approve"));
        Assert.Equal(2, membership.GrantVersion);
        var suspended = await data.Administration.SuspendIdentityAsync(technical, subject.UserId, 2, Metadata("suspend-identity"));
        Assert.Equal(3, suspended.GrantVersion);
        var revoked = await data.Administration.RevokeGrantAsync(owner, subject.UserId, 1, Metadata("revoke"));
        Assert.Equal(4, revoked.GrantVersion);
        var removed = await data.Administration.SuspendMembershipAsync(owner, membership.Id, 1, Metadata("suspend-membership"));
        Assert.Equal(5, removed.GrantVersion);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.ReadCurrentAsync(subject with { GrantVersion = 5 }));
        Assert.Equal(5, await data.Database.GetCollection<BsonDocument>("authorizationHistory").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Delegation_only_grant_is_reviewable_without_implying_branch_pic()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.BuyingBranch, 10, 11);
        var technical = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessPropose);
        var subject = await data.AddActorAsync(1001, scope, Permissions.AccessRevoke);
        var approver = await data.AddActorAsync(1002, scope, Permissions.AccessApprove);
        await data.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", approver.UserId),
            Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray { Permissions.AccessApprove, Permissions.OrderRead }));
        var request = new GrantChangeRequest(subject.UserId, subject.UserId, "finance", [], [Permissions.AccessApprove], scope,
            data.Clock.UtcNow, data.Clock.UtcNow.AddDays(7), [Permissions.OrderRead]);
        var preview = await data.Administration.PreviewGrantAsync(technical, request);
        Assert.DoesNotContain(Permissions.BranchPic, preview.Permissions);
        Assert.Equal(request.DelegablePermissions.ToArray(), preview.DelegablePermissions.ToArray());
        var proposal = await data.Administration.ProposeGrantAsync(technical, request, Metadata("propose"));
        var review = Assert.Single(await data.Administration.ListProposalsAsync(approver, 0, 10));
        Assert.Equal(request.DelegablePermissions.ToArray(), review.DelegablePermissions.ToArray());
        Assert.Equal(request.ExpiresAt, review.ExpiresAt);
        Assert.Equal(request.MembershipId, review.MembershipId);
        Assert.Equal(new[] { "approve" }, review.AllowedActions.ToArray());
        Assert.Empty(Assert.Single(await data.Administration.ListProposalsAsync(technical, 0, 10)).AllowedActions);
        var result = await data.Administration.ApproveGrantAsync(approver, proposal.Id, 1, Metadata("approve"));
        var current = await data.Store.ReadCurrentAsync(subject with { GrantVersion = result.GrantVersion });
        Assert.False(ScopePolicy.Compile(subject with { GrantVersion = result.GrantVersion }, current, Permissions.OrderRead, data.Clock.UtcNow).IsAllowed);
    }

    [Fact]
    public async Task Missing_and_out_of_scope_subjects_have_the_same_failure()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var approver = await data.AddActorAsync(1000, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.AccessApprove);
        var outside = await data.AddActorAsync(1001, new(GrantScopeKind.BuyerOwned, 20, 21), Permissions.OrderRead);
        var missing = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ReadSubjectAsync(approver, 999999));
        var inaccessible = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ReadSubjectAsync(approver, outside.UserId));
        Assert.Equal("resource_unavailable", missing.Code);
        Assert.Equal(missing.Code, inaccessible.Code);
    }
}
