using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class StoreApprovalTests(MongoFixture fixture)
{
    private static AuthorizationCommandMetadata Metadata(string key) => new(key, "Reviewed synthetic access request", "authorization-test");

    [Fact]
    public async Task Receiving_verification_permission_does_not_erase_the_original_payment_recorder()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        var recorder = await data.AddActorAsync(1000, scope, Permissions.PaymentRecord);
        var payments = data.Database.GetCollection<BsonDocument>("payments");
        await payments.InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 }, { "version", 1L },
            { "recordedBy", recorder.UserId }, { "fulfillmentOrganizationId", 100L }, { "shopId", 101L }, { "channelId", 103L } });
        await data.AddGrantAsync(recorder, 1001, scope, [Permissions.PaymentVerify]);
        await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", recorder.UserId),
            Builders<BsonDocument>.Update.Inc("grantVersion", 1L).Inc("fence", 1L));
        var currentActor = recorder with { GrantVersion = 2 };
        var snapshot = await data.Store.ReadCurrentAsync(currentActor);
        Assert.True(ScopePolicy.Compile(currentActor, snapshot, Permissions.PaymentVerify, data.Clock.UtcNow).IsAllowed);
        var writes = 0;
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.ExecuteAuthorizedAsync(currentActor,
            "payment.verify", Permissions.PaymentVerify, Metadata("self-verification"), new { id = 1 },
            async (session, token) =>
            {
                var payment = await payments.Find(session, new BsonDocument("_id", 1L)).FirstAsync(token);
                return new(AuthorizationBson.Resource(payment), new(payment["version"].AsInt64, 1, RecordedBy: payment["recordedBy"].AsInt64));
            }, (_, _) => { writes++; return Task.FromResult(new BsonDocument("verified", true)); }));
        Assert.Equal("independent_actor_required", denied.Code);
        Assert.Equal(0, writes);
        Assert.Null(await data.Database.GetCollection<BsonDocument>("idempotencyRecords").Find(new BsonDocument("key", "self-verification")).FirstOrDefaultAsync());
    }

    [Fact]
    public async Task Proposed_access_requires_independent_designated_approver_and_replays_once()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var technical = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessPropose, Permissions.AccessAuditRead);
        var subject = await data.AddActorAsync(1001, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var businessScope = new GrantScope(GrantScopeKind.BuyingBranch, 10, 11);
        var approver = await data.AddActorAsync(1002, businessScope, Permissions.AccessApprove);
        var delegated = RoleTemplates.Materialize("buyer", [], [Permissions.BranchPic]).Permissions.ToArray();
        await data.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", approver.UserId),
            Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray(delegated)));
        await data.AddGrantAsync(subject, 1003, businessScope, [Permissions.OrderRead, Permissions.BranchPic]);
        var request = new GrantChangeRequest(subject.UserId, 1003, "buyer", [], [Permissions.BranchPic], businessScope, data.Clock.UtcNow);
        var proposal = await data.Administration.ProposeGrantAsync(technical, request, Metadata("proposal-one"));
        Assert.Equal(proposal, await data.Administration.ProposeGrantAsync(technical, request, Metadata("proposal-one")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ApproveGrantAsync(technical, proposal.Id, 1, Metadata("self-approval")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ApproveGrantAsync(subject, proposal.Id, 1, Metadata("beneficiary-approval")));
        var result = await data.Administration.ApproveGrantAsync(approver, proposal.Id, 1, Metadata("approve-one"));
        Assert.Equal(result, await data.Administration.ApproveGrantAsync(approver, proposal.Id, 1, Metadata("approve-one")));
        Assert.Equal(2, result.GrantVersion);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.ReadCurrentAsync(subject));
        var current = await data.Store.ReadCurrentAsync(subject with { GrantVersion = 2 });
        var activated = Assert.Single(current.Grants.Where(grant => grant.Id == result.Id));
        Assert.Equal(technical.UserId, activated.RequestedBy);
        Assert.Equal(approver.UserId, activated.ApprovedBy);
        Assert.Equal(2, await data.Database.GetCollection<BsonDocument>("authorizationHistory").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Administration.ProposeGrantAsync(technical,
            request with { ExpiresAt = data.Clock.UtcNow.AddDays(1) }, Metadata("proposal-one")));
    }

    [Fact]
    public async Task Ordinary_business_permission_does_not_authorize_delegation_and_approval_scope_cannot_cross_branch()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var technical = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessPropose);
        var subject = await data.AddActorAsync(1001, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var wrongBranch = await data.AddActorAsync(1002, new(GrantScopeKind.BuyingBranch, 10, 12), Permissions.AccessApprove);
        var ordinary = await data.AddActorAsync(1003, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead, Permissions.OrderSubmit);
        var request = new GrantChangeRequest(subject.UserId, subject.UserId, "buyer", [], [], new(GrantScopeKind.BuyerOwned, 10, 11), data.Clock.UtcNow);
        var proposal = await data.Administration.ProposeGrantAsync(technical, request, Metadata("proposal"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ApproveGrantAsync(ordinary, proposal.Id, 1, Metadata("ordinary")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ApproveGrantAsync(wrongBranch, proposal.Id, 1, Metadata("wrong-branch")));
        Assert.Equal(1, (await data.Store.ReadCurrentAsync(subject)).GrantVersion);
    }
}
