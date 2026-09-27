using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class StoreResolutionTests(MongoFixture fixture)
{
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Reviewed resolution reason", "resolution-test");

    [Fact]
    public async Task Independent_scoped_reviewer_can_reject_excessive_permissions_without_being_able_to_grant_them()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.BuyingBranch, 10, 11);
        var proposer = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessPropose);
        var subject = await data.AddActorAsync(1001, scope, Permissions.AccessApprove);
        var reviewer = await data.AddActorAsync(1002, scope, Permissions.AccessApprove);
        var outside = await data.AddActorAsync(1003, new(GrantScopeKind.BuyingBranch, 10, 12), Permissions.AccessApprove);
        var request = new GrantChangeRequest(subject.UserId, subject.UserId, "buyer", [], [Permissions.BranchPic], scope, data.Clock.UtcNow);
        var proposal = await data.Administration.ProposeGrantAsync(proposer, request, Meta("propose"));
        var review = Assert.Single(await data.Administration.ListProposalsAsync(reviewer, 0, 10));
        Assert.Contains("reject", review.AllowedActions); Assert.DoesNotContain("approve", review.AllowedActions);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.RejectProposalAsync(subject, proposal.Id, 1, Meta("self-reject")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.RejectProposalAsync(outside, proposal.Id, 1, Meta("outside")));
        var result = await data.Administration.RejectProposalAsync(reviewer, proposal.Id, 1, Meta("reject"));
        Assert.Equal("rejected", result.Status); Assert.Equal(2, result.Version);
        Assert.Equal(result, await data.Administration.RejectProposalAsync(reviewer, proposal.Id, 1, Meta("reject")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Administration.RejectProposalAsync(reviewer, proposal.Id, 1, Meta("another-reject")));
        Assert.Equal(1, (await data.Store.ReadCurrentAsync(subject)).GrantVersion);
        Assert.Equal(4, await data.Database.GetCollection<BsonDocument>("authorizationGrants").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var raw = await data.Database.GetCollection<BsonDocument>("authorizationProposals").Find(new BsonDocument("_id", proposal.Id)).FirstAsync();
        Assert.Equal(reviewer.UserId, raw["resolvedBy"].AsInt64); Assert.Equal("rejected", raw["resolutionKind"].AsString);
        Assert.Empty(Assert.Single(await data.Administration.ListProposalsAsync(reviewer, 0, 10)).AllowedActions);
    }

    [Fact]
    public async Task Only_current_maker_can_withdraw_pending_membership_and_replay_remains_authorized()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var maker = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.MembershipPropose);
        var other = await data.AddActorAsync(1001, new(GrantScopeKind.System), Permissions.MembershipPropose, Permissions.AccessRevoke);
        var subject = await data.AddActorAsync(1002, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var proposal = await data.Administration.ProposeMembershipAsync(maker,
            new(subject.UserId, new(GrantScopeKind.BuyingBranch, 10, 11), data.Clock.UtcNow), Meta("propose-membership"));
        Assert.Contains("withdraw", Assert.Single(await data.Administration.ListProposalsAsync(maker, 0, 10)).AllowedActions);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.WithdrawProposalAsync(other, proposal.Id, 1, Meta("other-withdraw")));
        var withdrawn = await data.Administration.WithdrawProposalAsync(maker, proposal.Id, 1, Meta("withdraw"));
        Assert.Equal("withdrawn", withdrawn.Status);
        Assert.Equal(withdrawn, await data.Administration.WithdrawProposalAsync(maker, proposal.Id, 1, Meta("withdraw")));
        var raw = await data.Database.GetCollection<BsonDocument>("authorizationProposals").Find(new BsonDocument("_id", proposal.Id)).FirstAsync();
        Assert.Equal("rejected", raw["status"].AsString); Assert.Equal("withdrawn", raw["resolutionKind"].AsString);
        await data.Administration.RevokeGrantAsync(other, maker.UserId, 1, Meta("revoke-maker"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.WithdrawProposalAsync(maker, proposal.Id, 1, Meta("withdraw")));
        Assert.Equal(3, await data.Database.GetCollection<BsonDocument>("memberships").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }
}
