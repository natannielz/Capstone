using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class FenceTests(MongoFixture fixture)
{
    [Fact]
    public async Task Organization_wide_grant_still_fences_the_concrete_branch_during_suspension()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(1000, new(GrantScopeKind.BuyingOrganization, 10), Permissions.OrderSubmit, Permissions.OrganizationWide);
        var administrator = await data.AddActorAsync(1001, new(GrantScopeKind.System), Permissions.AccessRevoke);
        await data.AddResourceAsync(1, 10, 11, 2000);
        var snapshot = await data.Store.ReadCurrentAsync(actor);
        var query = ScopePolicy.Compile(actor, snapshot, Permissions.OrderSubmit, data.Clock.UtcNow);
        var grantNodes = Assert.Single(query.Clauses).RequiredNodes;
        Assert.Equal(ScopeNodeKind.BuyingOrganization, Assert.Single(grantNodes).Kind);

        var records = data.Database.GetCollection<BsonDocument>("orders");
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var writes = 0;
        var pending = data.Store.ExecuteAuthorizedAsync(actor, "order.submit", Permissions.OrderSubmit,
            new("organization-wide-write", "Submit branch order through organization authority", "descendant-race"), new { id = 1 },
            async (session, token) =>
            {
                var document = await records.Find(session, new BsonDocument("_id", 1L)).FirstAsync(token);
                if (Interlocked.Increment(ref reads) == 1) { read.SetResult(); await resume.Task.WaitAsync(token); }
                return new(AuthorizationBson.Resource(document));
            }, async (session, token) =>
            {
                Interlocked.Increment(ref writes);
                await MongoUnitOfWork.RequireVersionAsync(records, session, new BsonDocument("_id", 1L), 1,
                    Builders<BsonDocument>.Update.Set("submitted", true), token);
                return new BsonDocument("version", 2L);
            });
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await data.Administration.SuspendScopeAsync(administrator, 11, 1,
                new("suspend-descendant", "Branch unavailable", "descendant-race"));
        }
        finally { resume.TrySetResult(); }

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        Assert.True(reads >= 2, "The stale transaction must reload authoritative authorization after the branch fence conflicts.");
        Assert.Equal(0, writes);
        var unchanged = await records.Find(new BsonDocument("_id", 1L)).FirstAsync();
        Assert.Equal(1L, unchanged["version"].AsInt64);
        Assert.False(unchanged.Contains("submitted"));
        Assert.Null(await data.Database.GetCollection<BsonDocument>("idempotencyRecords")
            .Find(new BsonDocument("key", "organization-wide-write")).FirstOrDefaultAsync());
        var identity = await data.Database.GetCollection<BsonDocument>("identities").Find(new BsonDocument("_id", actor.UserId)).FirstAsync();
        Assert.Equal(1L, identity["grantVersion"].AsInt64);
        Assert.Equal(0L, identity["fence"].AsInt64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task In_flight_snapshot_cannot_commit_after_grant_revocation_or_branch_suspension(bool suspendBranch)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(1000, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderSubmit);
        var administrator = await data.AddActorAsync(1001, suspendBranch ? new(GrantScopeKind.System) : new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.AccessRevoke);
        await data.AddResourceAsync(1, 10, 11, actor.UserId);
        var records = data.Database.GetCollection<BsonDocument>("orders");
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var writes = 0;
        var pending = data.Store.ExecuteAuthorizedAsync(actor, "order.submit", Permissions.OrderSubmit,
            new("pending-write", "Submit synthetic order", "race-test"), new { id = 1 }, async (session, token) =>
            {
                var document = await records.Find(session, new BsonDocument("_id", 1L)).FirstAsync(token);
                if (Interlocked.Increment(ref reads) == 1) { read.SetResult(); await resume.Task.WaitAsync(token); }
                return new(AuthorizationBson.Resource(document));
            }, async (session, token) =>
            {
                Interlocked.Increment(ref writes);
                await MongoUnitOfWork.RequireVersionAsync(records, session, new BsonDocument("_id", 1L), 1,
                    Builders<BsonDocument>.Update.Set("submitted", true), token);
                return new BsonDocument("version", 2L);
            });
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (suspendBranch) await data.Administration.SuspendScopeAsync(administrator, 11, 1, new("suspend-branch", "Branch unavailable", "race-test"));
            else await data.Administration.RevokeGrantAsync(administrator, actor.UserId, 1, new("revoke-grant", "Access withdrawn", "race-test"));
        }
        finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        Assert.Equal(0, writes);
        Assert.Equal(1L, (await records.Find(new BsonDocument("_id", 1L)).FirstAsync())["version"].AsInt64);
        Assert.Null(await data.Database.GetCollection<BsonDocument>("idempotencyRecords").Find(new BsonDocument("key", "pending-write")).FirstOrDefaultAsync());
    }

    [Fact]
    public async Task Successful_approval_replay_ignores_completed_revision_but_rechecks_current_access_and_same_body()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        var verifier = await data.AddActorAsync(1000, scope, Permissions.PaymentVerify);
        var revoker = await data.AddActorAsync(1001, scope, Permissions.AccessRevoke);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, verifier.UserId, 2000);
        var payments = data.Database.GetCollection<BsonDocument>("payments");
        await payments.InsertOneAsync(new BsonDocument
        {
            { "_id", 1L }, { "schemaVersion", 1 }, { "version", 1L }, { "recordedBy", 2000L },
            { "fulfillmentOrganizationId", 100L }, { "shopId", 101L }, { "channelId", 103L }
        });
        var writes = 0;
        Task<BsonDocument> Verify(string body) => data.Store.ExecuteAuthorizedAsync(verifier, "payment.verify", Permissions.PaymentVerify,
            new("verify-key", "Verify independent payment evidence", "replay-test"), new { id = 1, expectedVersion = 1, body },
            async (session, token) =>
            {
                var payment = await payments.Find(session, new BsonDocument("_id", 1L)).FirstAsync(token);
                return new(AuthorizationBson.Resource(payment), new(payment["version"].AsInt64, 1, RecordedBy: payment["recordedBy"].AsInt64));
            }, async (session, token) =>
            {
                writes++;
                await MongoUnitOfWork.RequireVersionAsync(payments, session, new BsonDocument("_id", 1L), 1, Builders<BsonDocument>.Update.Set("verified", true), token);
                return new BsonDocument("version", 2L);
            });
        Assert.Equal(await Verify("same"), await Verify("same"));
        Assert.Equal(1, writes);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => Verify("changed"));
        await data.Administration.RevokeGrantAsync(revoker, verifier.UserId, 1, new("revoke", "Access withdrawn", "replay-test"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Verify("same"));
        Assert.Equal(1, writes);
    }
}
