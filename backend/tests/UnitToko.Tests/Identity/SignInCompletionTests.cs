using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class SignInCompletionTests(MongoFixture fixture)
{
    [Fact]
    public async Task Existing_approved_binding_gets_cookie_only_after_atomic_state_and_session_commit_and_cannot_replay()
    {
        await using var setup = await Setup.CreateAsync(fixture);
        var start = await setup.StartAsync();
        var http = new DefaultHttpContext();
        await setup.CompleteAsync(http, start);
        var token = Cookie(http);
        var session = Assert.IsType<IdentitySession>(await setup.Sessions.ReadAsync(token));
        Assert.Equal(setup.Actor.UserId, session.UserId);
        var header = http.Response.Headers.SetCookie.ToString();
        Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-provider-token", header);
        Assert.Equal("consumed", (await setup.Transactions.Find(new BsonDocument("_id", start.Transaction.Id)).SingleAsync())["status"].AsString);
        var replay = new DefaultHttpContext();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => setup.CompleteAsync(replay, start));
        Assert.False(replay.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task Session_issue_failure_rolls_back_new_profile_epoch_and_state_consumption_without_cookie()
    {
        await using var setup = await Setup.CreateAsync(fixture);
        var start = await setup.StartAsync();
        var http = new DefaultHttpContext();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => setup.CompleteAsync(http, start,
            payload: new string('x', SessionPolicy.MaximumProviderPayloadBytes + 1)));
        Assert.False(http.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Equal(0, await setup.Data.Database.GetCollection<BsonDocument>("identitySessions").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(0, await setup.Data.Database.GetCollection<BsonDocument>("accountProfiles").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.False((await setup.Data.Database.GetCollection<BsonDocument>("identities").Find(new BsonDocument("_id", setup.Actor.UserId)).SingleAsync()).Contains("securityVersion"));
        Assert.Equal("pending", (await setup.Transactions.Find(new BsonDocument("_id", start.Transaction.Id)).SingleAsync())["status"].AsString);
        await setup.CompleteAsync(new DefaultHttpContext(), start);
    }

    [Fact]
    public async Task Step_up_requires_original_browser_session_and_rotates_without_extending_absolute_expiry()
    {
        await using var setup = await Setup.CreateAsync(fixture);
        var login = await setup.StartAsync();
        var firstHttp = new DefaultHttpContext();
        await setup.CompleteAsync(firstHttp, login);
        var firstToken = Cookie(firstHttp);
        var first = Assert.IsType<IdentitySession>(await setup.Sessions.ReadAsync(firstToken));
        setup.Data.Clock.UtcNow += TimeSpan.FromMinutes(2);
        var step = await setup.Oidc.BeginAsync(new(OidcTransactionPurpose.StepUp, setup.Options.Issuer, setup.Options.ClientId, "/",
            first.UserId, first.Reference, first.GrantVersion, first.Proof.Subject, Permissions.OrderSubmit));
        var wrongBrowser = new DefaultHttpContext();
        wrongBrowser.Request.Headers.Cookie = OpaqueSessionCookie.Name + "=" + new string('x', 43);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => setup.CompleteAsync(wrongBrowser, step));
        Assert.False(wrongBrowser.Response.Headers.ContainsKey("Set-Cookie"));
        var correctBrowser = new DefaultHttpContext();
        correctBrowser.Request.Headers.Cookie = OpaqueSessionCookie.Name + "=" + firstToken;
        await setup.CompleteAsync(correctBrowser, step);
        var secondToken = Cookie(correctBrowser);
        Assert.NotEqual(firstToken, secondToken);
        Assert.Null(await setup.Sessions.ReadAsync(firstToken));
        var second = Assert.IsType<IdentitySession>(await setup.Sessions.ReadAsync(secondToken));
        Assert.Equal(first.AbsoluteExpiresAt, second.AbsoluteExpiresAt);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
    }

    private static string Cookie(DefaultHttpContext http) =>
        Assert.Single(http.Response.Headers.SetCookie, value => value!.StartsWith(OpaqueSessionCookie.Name + "=", StringComparison.Ordinal))!
            .Split(';')[0][(OpaqueSessionCookie.Name.Length + 1)..];

    private sealed class Setup : IAsyncDisposable
    {
        public required StoreTestData Data { get; init; }
        public required ActorContext Actor { get; init; }
        public required Auth0Options Options { get; init; }
        public required OidcTransactionStore Oidc { get; init; }
        public required MongoIdentitySessionStore Sessions { get; init; }
        public required VerifiedOidcSignInCompletion Completion { get; init; }
        public IMongoCollection<BsonDocument> Transactions => Data.Database.GetCollection<BsonDocument>("oidcTransactions");
        public static async Task<Setup> CreateAsync(MongoFixture fixture)
        {
            var data = await StoreTestData.CreateAsync(fixture);
            if (data.Clock.UtcNow < DateTimeOffset.UtcNow)
                data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var actor = await data.AddActorAsync(1000, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderSubmit);
            var options = Auth0ClaimsTests.Settings(); options.Issuer = actor.ProviderIssuer;
            var unit = new MongoUnitOfWork(fixture.Client);
            var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
            var policy = new SessionPolicy(options.Issuer, options.ConnectionId, options.AssurancePolicyVersion);
            var sessions = new MongoIdentitySessionStore(data.Database, unit, data.Clock, policy, protector);
            var onboarding = new IdentityOnboardingService(data.Database, data.Store, new(data.Database), protector, policy, data.Clock);
            var transactions = new OidcTransactionStore(data.Database, data.Clock);
            return new()
            {
                Data = data, Actor = actor, Options = options, Oidc = transactions, Sessions = sessions,
                Completion = new(unit, transactions, onboarding, sessions, Microsoft.Extensions.Options.Options.Create(options))
            };
        }
        public Task<OidcTransactionStart> StartAsync() => Oidc.BeginAsync(new(OidcTransactionPurpose.Login, Options.Issuer, Options.ClientId, "/"));
        public async Task CompleteAsync(DefaultHttpContext http, OidcTransactionStart start, string payload = "fixture-provider-token")
        {
            var proof = new ProviderIdentityProof(Actor.ProviderIssuer, Actor.ProviderSubject, "fixture@example.test", true,
                Options.ConnectionId, Data.Clock.UtcNow, Data.Clock.UtcNow, "otp", Options.AssurancePolicyVersion,
                start.ChallengeBinding, DisplayName: "Synthetic person");
            var current = await Oidc.ReadPendingAsync(start.StateToken);
            await Completion.CompleteAsync(http, start.StateToken, current, proof, new(payload), CancellationToken.None);
        }
        public ValueTask DisposeAsync() => Data.DisposeAsync();
    }
}
