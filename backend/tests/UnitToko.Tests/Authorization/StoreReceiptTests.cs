using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class StoreReceiptTests(MongoFixture fixture)
{
    private static readonly AuthorizationReadMetadata ReadAudit = new("operation-recovery", "receipt-test");
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Resolve the interrupted access request", "receipt-test");

    [Fact]
    public async Task Receipts_are_actor_owned_allowlisted_and_absence_is_not_a_failure_or_success()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var maker = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.MembershipPropose);
        var other = await data.AddActorAsync(1001, new(GrantScopeKind.System), Permissions.MembershipPropose, Permissions.AccessRevoke);
        var subject = await data.AddActorAsync(1002, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var missing = await data.Administration.ReadOperationReceiptAsync(maker, "access.membership.propose", "request", ReadAudit);
        Assert.Equal("not_confirmed", missing.State); Assert.Null(missing.Result); Assert.Null(missing.RecordedAt);
        var proposal = await data.Administration.ProposeMembershipAsync(maker,
            new(subject.UserId, new(GrantScopeKind.BuyingBranch, 10, 11), data.Clock.UtcNow), Meta("request"));
        var receipt = await data.Administration.ReadOperationReceiptAsync(maker, "access.membership.propose", "request", ReadAudit);
        Assert.Equal("committed", receipt.State); Assert.Equal(proposal.Id, receipt.Result!.Id);
        Assert.Equal("not_confirmed", (await data.Administration.ReadOperationReceiptAsync(other, "access.membership.propose", "request", ReadAudit)).State);
        Assert.Equal("committed", (await data.Administration.ResolveOperationReceiptAsync(maker, "access.membership.propose", "request", Meta("request"))).State);
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Administration.ReadOperationReceiptAsync(maker, "arbitrary.operation", "request", ReadAudit));
        await data.Administration.RevokeGrantAsync(other, maker.UserId, 1, Meta("revoke"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ReadOperationReceiptAsync(maker, "access.membership.propose", "request", ReadAudit));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ReadOperationReceiptAsync(maker with { GrantVersion = 2 }, "access.membership.propose", "request", ReadAudit));
    }

    [Fact]
    public async Task Closing_unknown_request_wins_against_in_flight_original_and_prevents_all_original_effects()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessRevoke);
        var target = await data.AddActorAsync(1001, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        Task<BsonDocument> Original() => data.Store.ExecuteAuthorizedAsync(actor, "access.identity.suspend", Permissions.AccessRevoke,
            Meta("in-flight-key"), new { userId = target.UserId, expectedVersion = 1 }, async (_, token) =>
            {
                read.TrySetResult(); await resume.Task.WaitAsync(token);
                return new(new ResourceScope(IsAdministrative: true));
            }, async (session, token) =>
            {
                Interlocked.Increment(ref callbacks);
                await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(session, new BsonDocument("_id", target.UserId),
                    Builders<BsonDocument>.Update.Set("status", "suspended").Inc("grantVersion", 1L).Inc("fence", 1L), cancellationToken: token);
                return new BsonDocument { { "id", target.UserId }, { "subjectId", target.UserId }, { "version", 2L }, { "grantVersion", 2L }, { "status", "suspended" } };
            });
        var pending = Original();
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        OperationReceipt closed;
        try { closed = await data.Administration.ResolveOperationReceiptAsync(actor, "access.identity.suspend", "in-flight-key", Meta("in-flight-key")); }
        finally { resume.TrySetResult(); }
        Assert.Equal("closed", closed.State); Assert.Null(closed.Result);
        var interrupted = await Assert.ThrowsAsync<PersistenceConflictException>(() => pending);
        Assert.Equal("idempotency_key_reused", interrupted.Code);
        await Assert.ThrowsAsync<PersistenceConflictException>(Original);
        Assert.Equal(0, callbacks);
        Assert.Equal(1, (await data.Store.ReadCurrentAsync(target)).GrantVersion);
        Assert.Equal("closed", (await data.Administration.ReadOperationReceiptAsync(actor, "access.identity.suspend", "in-flight-key", ReadAudit)).State);
        Assert.Equal(closed, await data.Administration.ResolveOperationReceiptAsync(actor, "access.identity.suspend", "in-flight-key", Meta("in-flight-key")));
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("authorizationHistory").CountDocumentsAsync(new BsonDocument("operation", "access.receipt.closed")));
    }

    [Fact]
    public async Task Original_commit_wins_and_resolution_never_reports_it_closed()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessRevoke);
        var target = await data.AddActorAsync(1001, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var original = await data.Administration.SuspendIdentityAsync(actor, target.UserId, 1, Meta("completed"));
        var receipt = await data.Administration.ResolveOperationReceiptAsync(actor, "access.identity.suspend", "completed", Meta("completed"));
        Assert.Equal("committed", receipt.State); Assert.Equal(original.Id, receipt.Result!.Id); Assert.Equal(original.Status, receipt.Result.Status);
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("authorizationHistory").CountDocumentsAsync(new BsonDocument("operation", "access.receipt.closed")));
        var identity = await data.Database.GetCollection<BsonDocument>("identities").Find(new BsonDocument("_id", target.UserId)).FirstAsync();
        Assert.Equal("suspended", identity["status"].AsString); Assert.Equal(2L, identity["grantVersion"].AsInt64);
    }
}
