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
public sealed class ProviderSecurityEventTests(MongoFixture fixture)
{
    [Fact]
    public async Task Verified_block_is_atomic_deduplicated_and_closes_sessions_on_two_hosts_and_future_admission()
    {
        await using var data = await Data.CreateAsync(fixture);
        var issued = await data.IssueAsync();
        Assert.NotNull(await data.SecondHost.ReadAsync(issued.OpaqueToken));
        var body = ProviderSecurityFixtures.Event();
        var first = await data.ProcessAsync(body);
        Assert.Equal(new(1, 0, 0, 0), first);
        Assert.Equal(new(0, 1, 0, 0), await data.ProcessAsync(body));
        var eventRow = await data.Rows("identitySecurityEvents").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal("applied", eventRow["outcome"].AsString);
        Assert.Equal(1L, eventRow["previousSecurityVersion"].AsInt64);
        Assert.Equal(2L, eventRow["securityVersion"].AsInt64);
        Assert.DoesNotContain("auth0|target-user", eventRow.ToJson());
        Assert.DoesNotContain("owner@example.test", eventRow.ToJson());
        var hold = await data.Rows("identityAdmissionHolds").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal(eventRow["_id"], hold["_id"]);
        Assert.Equal("providerBlocked", hold["reason"].AsString);
        Assert.Null(await data.FirstHost.ReadAsync(issued.OpaqueToken));
        Assert.Null(await data.SecondHost.ReadAsync(issued.OpaqueToken));
        Assert.Equal(2L, (await data.IdentityAsync())["securityVersion"].AsInt64);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.IssueAsync(2));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync((tx, ct) =>
            data.Onboarding.AdmitAsync(tx, data.Proof(), null, ct)));
    }

    [Fact]
    public async Task Unknown_subject_stays_unknown_on_replay_and_reused_id_with_changed_facts_conflicts()
    {
        await using var data = await Data.CreateAsync(fixture);
        var body = ProviderSecurityFixtures.Event(subject: "auth0|unknown-person");
        Assert.Equal(new(0, 0, 1, 0), await data.ProcessAsync(body));
        Assert.Equal(0, await data.Rows("identityAdmissionHolds").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(1L, (await data.IdentityAsync())["securityVersion"].AsInt64);
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", data.Actor.UserId),
            Builders<BsonDocument>.Update.Set("subject", "auth0|unknown-person"));
        Assert.Equal(new(0, 1, 0, 0), await data.ProcessAsync(body));
        Assert.Equal(1L, (await data.IdentityAsync())["securityVersion"].AsInt64);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.ProcessAsync(
            ProviderSecurityFixtures.Event("user.deleted", "auth0|unknown-person")));
        Assert.Equal(1, await data.Rows("identitySecurityEvents").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Password_and_factor_change_revoke_even_when_events_arrive_out_of_order_without_creating_a_hold()
    {
        await using var data = await Data.CreateAsync(fixture);
        data.Store.Clock.UtcNow = ProviderSecurityFixtures.Now.AddMinutes(2);
        var newer = ProviderSecurityFixtures.Log("scp", id: "90020260928080000000000002",
            occurredAt: ProviderSecurityFixtures.Now.AddMinutes(1));
        var older = ProviderSecurityFixtures.Log("gd_enrollment_complete", id: "90020260928080000000000001",
            occurredAt: ProviderSecurityFixtures.Now.AddMinutes(-1));
        Assert.Equal(new(1, 0, 0, 0), await data.ProcessAsync(newer, ProviderEventTransport.Auth0Logs));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.IssueAsync(2));
        var cutoff = data.Store.Clock.UtcNow;
        data.Store.Clock.UtcNow = data.Store.Clock.UtcNow.AddSeconds(1);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.IssueAsync(2,
            data.Proof() with { ProviderAuthenticatedAt = cutoff }));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.IssueAsync(2,
            data.Proof() with { FactorCompletedAt = cutoff }));
        var issued = await data.IssueAsync(2);
        data.Store.Clock.UtcNow = ProviderSecurityFixtures.Now.AddMinutes(1);
        Assert.Equal(new(1, 0, 0, 0), await data.ProcessAsync(older, ProviderEventTransport.Auth0Logs));
        Assert.Null(await data.SecondHost.ReadAsync(issued.OpaqueToken));
        Assert.Equal(3L, (await data.IdentityAsync())["securityVersion"].AsInt64);
        Assert.Equal(0, await data.Rows("identityAdmissionHolds").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(cutoff.UtcDateTime, (await data.Rows("identityProviderSubjects")
            .Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())["proofNotBefore"].ToUniversalTime());
    }

    [Fact]
    public async Task Failed_event_audit_insert_rolls_back_security_revision_and_session_revocation()
    {
        await using var data = await Data.CreateAsync(fixture);
        var issued = await data.IssueAsync();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument
            { { "collMod", "identitySecurityEvents" }, { "validator", new BsonDocument("$expr", false) }, { "validationLevel", "strict" } });
        await Assert.ThrowsAnyAsync<MongoException>(() => data.ProcessAsync(ProviderSecurityFixtures.Event()));
        Assert.Equal(1L, (await data.IdentityAsync())["securityVersion"].AsInt64);
        Assert.NotNull(await data.SecondHost.ReadAsync(issued.OpaqueToken));
        Assert.Equal(0, await data.Rows("identityAdmissionHolds").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Concurrent_duplicate_has_one_applied_event_and_legacy_identity_transitions_from_baseline_one()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", data.Actor.UserId),
            Builders<BsonDocument>.Update.Unset("securityVersion"));
        var body = ProviderSecurityFixtures.Event();
        var outcomes = await Task.WhenAll(data.ProcessAsync(body), data.ProcessAsync(body));
        Assert.Equal(1, outcomes.Sum(result => result.Applied));
        Assert.Equal(1, outcomes.Sum(result => result.Duplicates));
        Assert.Equal(2L, (await data.IdentityAsync())["securityVersion"].AsInt64);
        Assert.Equal(1, await data.Rows("identitySecurityEvents").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(1, await data.Rows("identityAdmissionHolds").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Factor_removal_holds_admission_and_provider_event_preserves_inactive_identity_status()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", data.Actor.UserId),
            Builders<BsonDocument>.Update.Set("status", "suspended"));
        Assert.Equal(new(1, 0, 0, 0), await data.ProcessAsync(
            ProviderSecurityFixtures.Log("gd_unenroll"), ProviderEventTransport.Auth0Logs));
        var identity = await data.IdentityAsync();
        Assert.Equal("suspended", identity["status"].AsString);
        Assert.Equal(2L, identity["securityVersion"].AsInt64);
        Assert.Equal("factorRemoved", (await data.Rows("identityAdmissionHolds")
            .Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())["reason"].AsString);
    }

    [Fact]
    public async Task Paused_business_command_cannot_commit_after_verified_provider_event()
    {
        await using var data = await Data.CreateAsync(fixture);
        var issued = await data.IssueAsync();
        await data.Store.AddResourceAsync(1, 10, 11, data.Actor.UserId);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var orders = data.Rows("orders");
        var writes = 0;
        var pending = data.Store.Store.ExecuteAuthorizedAsync(data.Actor with { Session = issued.Session.Reference },
            "order.submit", Permissions.OrderSubmit, new("event-race", "Reviewed order", "provider-event-test"), new { id = 1 },
            async (tx, ct) =>
            {
                var row = await orders.Find(tx, new BsonDocument("_id", 1L)).SingleAsync(ct);
                ready.TrySetResult();
                await resume.Task.WaitAsync(ct);
                return new(AuthorizationBson.Resource(row));
            },
            async (tx, ct) =>
            {
                Interlocked.Increment(ref writes);
                await orders.UpdateOneAsync(tx, new BsonDocument("_id", 1L),
                    Builders<BsonDocument>.Update.Set("submitted", true), cancellationToken: ct);
                return new BsonDocument("accepted", true);
            });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { Assert.Equal(new(1, 0, 0, 0), await data.ProcessAsync(ProviderSecurityFixtures.Event())); }
        finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        Assert.Equal(0, writes);
        Assert.False((await orders.Find(new BsonDocument("_id", 1L)).SingleAsync()).Contains("submitted"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_change_racing_a_paused_first_invitation_callback_rejects_stale_proof(bool passwordChange)
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        data.Store.Clock.UtcNow = ProviderSecurityFixtures.Now;
        var options = ProviderSecurityFixtures.Options();
        var policy = new SessionPolicy(options.Issuer, "fixture-connection", "fixture-policy");
        var onboarding = new IdentityOnboardingService(data.Store.Database, data.Store.Store,
            new(data.Store.Database), data.Protector, policy, data.Store.Clock);
        var maker = data.Maker with { AuthenticatedAt = data.Store.Clock.UtcNow };
        var checker = data.Checker with { AuthenticatedAt = data.Store.Clock.UtcNow };
        var proposed = await onboarding.ProposeAsync(maker,
            new("new.owner@example.test", new(GrantScopeKind.BuyingBranch, 10, 11), data.Store.Clock.UtcNow.AddDays(1)),
            new("race-propose", "Reviewed invitation", "provider-race"));
        var approved = await onboarding.ApproveAsync(checker, proposed.Id, 1,
            new("race-approve", "Independent approval", "provider-race"));
        var intent = await data.Rows("identityProviderIntents").Find(new BsonDocument("invitationId", approved.Id)).SingleAsync();
        var payload = BsonDocument.Parse(data.Protector.Unprotect("invitation-delivery", intent["protectedPayload"].AsString));
        var binding = await onboarding.BindInvitationAsync(approved.Id, payload["secret"].AsString);
        var proof = new ProviderIdentityProof(options.Issuer, "auth0|target-user", "new.owner@example.test", true,
            "fixture-connection", data.Store.Clock.UtcNow, data.Store.Clock.UtcNow, "otp", "fixture-policy", "race-challenge");
        var sessionStore = new MongoIdentitySessionStore(data.Store.Database, data.Unit, data.Store.Clock, policy, data.Protector);
        var service = new ProviderSecurityEventService(data.Store.Database, data.Unit, sessionStore,
            new ProviderSecurityEventVerifier(options, data.Store.Clock), data.Store.Clock);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            // Establish a real stale transaction snapshot before the event commits.
            Assert.True(await data.Rows("accountInvitations").Find(tx,
                new BsonDocument("_id", approved.Id)).AnyAsync(ct));
            ready.TrySetResult();
            await resume.Task.WaitAsync(ct);
            var admitted = await onboarding.AdmitAsync(tx, proof, binding, ct);
            return await sessionStore.CreateAsync(tx, admitted, proof, "{}", ct);
        });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var result = await service.ProcessAsync(passwordChange ? ProviderEventTransport.Auth0Logs : ProviderEventTransport.Auth0Events,
                passwordChange ? ProviderSecurityFixtures.Log("scp") : ProviderSecurityFixtures.Event(),
                "Bearer " + (passwordChange ? options.LogsBearerToken : options.EventsBearerToken));
            Assert.Equal(new(0, 0, 1, 0), result);
        }
        finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => callback);
        Assert.False(await data.Rows("identities").Find(new BsonDocument("_id", approved.UserId)).AnyAsync());
        Assert.Equal("approved", (await data.Rows("accountInvitations")
            .Find(new BsonDocument("_id", approved.Id)).SingleAsync())["status"].AsString);
        Assert.Equal(0, await data.Rows("identitySessions").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var subject = await data.Rows("identityProviderSubjects").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal(!passwordChange, subject["admissionRestricted"].AsBoolean);
        if (passwordChange)
        {
            Assert.Equal(ProviderSecurityFixtures.Now.UtcDateTime, subject["proofNotBefore"].ToUniversalTime());
            data.Store.Clock.UtcNow = data.Store.Clock.UtcNow.AddSeconds(1);
            var fresh = proof with { ProviderAuthenticatedAt = data.Store.Clock.UtcNow,
                FactorCompletedAt = data.Store.Clock.UtcNow, ChallengeBinding = "fresh-challenge" };
            var issued = await data.Unit.ExecuteAsync(async (tx, ct) =>
                await sessionStore.CreateAsync(tx, await onboarding.AdmitAsync(tx, fresh, binding, ct), fresh, "{}", ct));
            Assert.Equal(approved.UserId, issued.Session.UserId);
        }
    }

    private sealed class Data : IAsyncDisposable
    {
        private readonly StoreTestData store;
        private readonly ProviderSecurityEventService service;
        private readonly Auth0SecurityEventOptions options;
        public IMongoDatabase Database => store.Database;
        public StoreTestData Store => store;
        public MongoUnitOfWork Unit { get; }
        public MongoIdentitySessionStore FirstHost { get; }
        public MongoIdentitySessionStore SecondHost { get; }
        public IdentityOnboardingService Onboarding { get; }
        public ActorContext Actor { get; }
        public IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
        private Data(StoreTestData store, MongoUnitOfWork unit, MongoIdentitySessionStore first,
            MongoIdentitySessionStore second, IdentityOnboardingService onboarding,
            ProviderSecurityEventService service, Auth0SecurityEventOptions options, ActorContext actor)
        {
            this.store = store; Unit = unit; FirstHost = first; SecondHost = second;
            Onboarding = onboarding; this.service = service; this.options = options; Actor = actor;
        }
        public static async Task<Data> CreateAsync(MongoFixture fixture)
        {
            var store = await StoreTestData.CreateAsync(fixture);
            store.Clock.UtcNow = ProviderSecurityFixtures.Now;
            var original = await store.AddActorAsync(1000, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderSubmit);
            var options = ProviderSecurityFixtures.Options();
            var actor = original with { ProviderIssuer = options.Issuer, ProviderSubject = "auth0|target-user" };
            await store.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
                Builders<BsonDocument>.Update.Set("issuer", actor.ProviderIssuer).Set("subject", actor.ProviderSubject).Set("securityVersion", 1L));
            var unit = new MongoUnitOfWork(fixture.Client);
            var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
            var policy = new SessionPolicy(options.Issuer, "fixture-connection", "fixture-policy");
            var first = new MongoIdentitySessionStore(store.Database, unit, store.Clock, policy, protector);
            var second = new MongoIdentitySessionStore(store.Database, new(fixture.Client), store.Clock, policy, protector);
            var onboarding = new IdentityOnboardingService(store.Database, store.Store, new(store.Database), protector, policy, store.Clock);
            var service = new ProviderSecurityEventService(store.Database, unit, first,
                new ProviderSecurityEventVerifier(options, store.Clock), store.Clock);
            return new(store, unit, first, second, onboarding, service, options, actor);
        }
        public ProviderIdentityProof Proof() => new(Actor.ProviderIssuer, Actor.ProviderSubject, "owner@example.test", true,
            "fixture-connection", store.Clock.UtcNow, store.Clock.UtcNow, "otp", "fixture-policy");
        public Task<SessionIssue> IssueAsync(long version = 1, ProviderIdentityProof? proof = null) => Unit.ExecuteAsync((tx, ct) => FirstHost.CreateAsync(tx,
            new(Actor.UserId, version, Actor.GrantVersion, Actor.ProviderIssuer, Actor.ProviderSubject), proof ?? Proof(), "{}", ct));
        public Task<ProviderEventBatchResult> ProcessAsync(ReadOnlyMemory<byte> body,
            ProviderEventTransport transport = ProviderEventTransport.Auth0Events) =>
            service.ProcessAsync(transport, body, "Bearer " + (transport == ProviderEventTransport.Auth0Events
                ? options.EventsBearerToken : options.LogsBearerToken));
        public Task<BsonDocument> IdentityAsync() => Rows("identities").Find(new BsonDocument("_id", Actor.UserId)).SingleAsync();
        public ValueTask DisposeAsync() => store.DisposeAsync();
    }
}
