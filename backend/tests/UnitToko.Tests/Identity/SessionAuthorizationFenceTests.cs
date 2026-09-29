using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class SessionAuthorizationFenceTests(MongoFixture fixture)
{
    [Theory]
    [InlineData("logout")]
    [InlineData("recovery")]
    [InlineData("step-up")]
    public async Task A_business_request_already_in_progress_cannot_commit_after_its_session_is_revoked_or_rotated(string change)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        if (data.Clock.UtcNow < DateTimeOffset.UtcNow) data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var actor = await data.AddActorAsync(1000, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderSubmit);
        var unit = new MongoUnitOfWork(fixture.Client);
        var sessions = new MongoIdentitySessionStore(data.Database, unit, data.Clock,
            new(actor.ProviderIssuer, "fixture-connection", "fixture-policy"),
            SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
            Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "fixture@example.test", true,
            "fixture-connection", data.Clock.UtcNow, data.Clock.UtcNow, "otp", "fixture-policy");
        var issued = await unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
            new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "encrypted-fixture", ct));
        var signedIn = actor with { Session = issued.Session.Reference };
        await data.AddResourceAsync(1, 10, 11, actor.UserId);
        var orders = data.Database.GetCollection<BsonDocument>("fixtureBusinessRecords");
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var pending = data.Store.ExecuteAuthorizedAsync(signedIn, "order.submit", Permissions.OrderSubmit,
            new("in-flight-session", "Submit synthetic order", "session-fence"), new { id = 1 },
            async (tx, ct) =>
            {
                var row = await orders.Find(tx, new BsonDocument("_id", 1L)).SingleAsync(ct);
                read.TrySetResult();
                await resume.Task.WaitAsync(ct);
                return new(AuthorizationBson.Resource(row));
            },
            async (tx, ct) =>
            {
                Interlocked.Increment(ref writes);
                await orders.UpdateOneAsync(tx, new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("submitted", true), cancellationToken: ct);
                return new BsonDocument("accepted", true);
            });
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (change == "logout") await sessions.RevokeAsync(issued.OpaqueToken, "owner_logout");
            else if (change == "recovery")
                await unit.ExecuteAsync(async (tx, ct) =>
                {
                    await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(tx, new BsonDocument("_id", actor.UserId),
                        Builders<BsonDocument>.Update.Inc("securityVersion", 1L).Inc("fence", 1L), cancellationToken: ct);
                    await sessions.RevokeUserSessionsAsync(tx, actor.UserId, "owner_recovery", ct);
                    return true;
                });
            else
                await unit.ExecuteAsync((tx, ct) => sessions.RotateForStepUpAsync(tx, issued.Session.Reference,
                    proof with { ChallengeBinding = "synthetic-factor-challenge" }, "encrypted-fixture", "synthetic-factor-challenge", data.Clock.UtcNow, ct));
        }
        finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        Assert.Equal(0, writes);
        Assert.False((await orders.Find(new BsonDocument("_id", 1L)).SingleAsync()).Contains("submitted"));
        Assert.Null(await data.Database.GetCollection<BsonDocument>("idempotencyRecords")
            .Find(new BsonDocument("key", "in-flight-session")).FirstOrDefaultAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.ReadCurrentAsync(signedIn));
    }

    [Fact]
    public async Task Expired_session_or_wrong_user_reference_cannot_read_current_authority()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        if (data.Clock.UtcNow < DateTimeOffset.UtcNow) data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var actor = await data.AddActorAsync(1000, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderSubmit);
        var other = await data.AddActorAsync(1001, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderSubmit);
        await data.Database.GetCollection<BsonDocument>("identities").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
            Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var unit = new MongoUnitOfWork(fixture.Client);
        var sessions = new MongoIdentitySessionStore(data.Database, unit, data.Clock, new(actor.ProviderIssuer, "fixture", "fixture"),
            SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "fixture@example.test", true, "fixture",
            data.Clock.UtcNow, data.Clock.UtcNow, "otp", "fixture");
        var issued = await unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
            new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "fixture", ct));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.ReadCurrentAsync(other with { Session = issued.Session.Reference }));
        data.Clock.UtcNow += SessionPolicy.IdleTimeout;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.ReadCurrentAsync(actor with { Session = issued.Session.Reference }));
    }
}
