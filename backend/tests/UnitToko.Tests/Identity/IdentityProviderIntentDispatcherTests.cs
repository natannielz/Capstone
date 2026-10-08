using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Identity;

// Source-only Mongo cases reuse the existing owner/session/recovery fixtures. All sends are fake.
[Collection("Mongo")]
public sealed class IdentityProviderIntentDispatcherTests(MongoFixture fixture)
{
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Owner approved recovery", key);
    private static Task<Auth0ProviderDeliveryResult> Accepted() => Task.FromResult(
        new Auth0ProviderDeliveryResult(Auth0ProviderDeliveryState.ProviderAccepted, "provider_request_accepted", true));

    private static async Task EnableSchema(OnboardingTests.Data data) => await data.Store.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
        { "collMod", "identityProviderIntents" }, { "validator", IdentityProviderIntentDispatcher.RequiredValidator() },
        { "validationLevel", "strict" }, { "validationAction", "error" }
    });
    private static IdentityProviderIntentDispatcher Dispatcher(OnboardingTests.Data data,
        IdentityProviderIntentDispatcher.DeliveryAttempt send, bool enabled = true) => new(data.Store.Database,
        data.Unit, data.Store.Store, data.Sessions, data.Protector, data.Store.Clock,
        new() { Enabled = enabled, Issuer = "https://approved.example/" }, send);

    private static async Task<(RecoveryResult Intent, SessionIssue Session)> Requested(OnboardingTests.Data data, bool self = false)
    {
        var owner = await data.AddActor(2001, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var actor = self ? owner : data.Maker;
        var proof = data.Proof() with { Subject = actor.ProviderSubject, Email = "actor@example.test" };
        var issued = await data.Admit(proof, null);
        var current = new ActorContext(actor.UserId, proof.Issuer, proof.Subject, issued.Session.GrantVersion,
            proof.FactorCompletedAt, FactorAssurance.MultiFactor, issued.Session.Reference);
        var recovery = new AccountRecoveryService(data.Store.Database, data.Store.Store, new(data.Store.Database),
            data.Sessions, data.Protector, data.Store.Clock);
        var intent = await recovery.InitiateAsync(current, owner.UserId, 1, AccountRecoveryKind.Password, Meta(self ? "self" : "admin"));
        return (intent, self ? await data.Admit(proof, null) : issued);
    }

    [Fact]
    public async Task Default_and_original_blocked_only_schema_permit_no_claim_or_send()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var (intent, session) = await Requested(data); var sends = 0;
        Task<Auth0ProviderDeliveryResult> Send(BsonDocument _, Func<CancellationToken, Task<bool>> __,
            Func<CancellationToken, Task<bool>> ___, CancellationToken ____) { sends++; return Accepted(); }
        Assert.Equal("delivery_disabled", (await Dispatcher(data, Send, false).DispatchAsync(intent.Id, session.OpaqueToken)).Code);
        Assert.Equal("storage_not_ready", (await Dispatcher(data, Send).DispatchAsync(intent.Id, session.OpaqueToken)).Code);
        Assert.Equal(0, sends);
        Assert.Equal("blocked", (await data.Rows("identityProviderIntents").Find(new BsonDocument("_id", intent.Id)).SingleAsync())["status"]);
    }

    [Fact]
    public async Task Capped_collection_with_exact_strict_validator_refuses_claim_and_send()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var (intent, session) = await Requested(data); var sends = 0;
        var original = await data.Rows("identityProviderIntents").Find(new BsonDocument("_id", intent.Id)).SingleAsync();
        // Only this fixture's private collection is recreated; the real producer row and full validator are retained.
        await data.Store.Database.DropCollectionAsync("identityProviderIntents");
        await data.Store.Database.CreateCollectionAsync("identityProviderIntents", new CreateCollectionOptions<BsonDocument> {
            Capped = true, MaxSize = 1_048_576, Validator = IdentityProviderIntentDispatcher.RequiredValidator(),
            ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error
        });
        await data.Rows("identityProviderIntents").InsertOneAsync(original);
        var dispatcher = Dispatcher(data, (_, _, _, _) => { sends++; return Accepted(); });
        Assert.Equal("storage_not_ready", (await dispatcher.DispatchAsync(intent.Id, session.OpaqueToken)).Code);
        Assert.Equal(0, sends);
        var unchanged = await data.Rows("identityProviderIntents").Find(new BsonDocument("_id", intent.Id)).SingleAsync();
        Assert.Equal(original, unchanged); Assert.False(unchanged.Contains("delivery"));
    }

    [Fact]
    public async Task Already_cancelled_request_never_claims_or_sends()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var (intent, session) = await Requested(data); await EnableSchema(data); var sends = 0;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var result = await Dispatcher(data, (_, _, _, _) => { sends++; return Accepted(); })
            .DispatchAsync(intent.Id, session.OpaqueToken, cancellation.Token);
        Assert.Equal("blocked", result.Status); Assert.Equal(0, sends);
        var row = await data.Rows("identityProviderIntents").Find(new BsonDocument("_id", intent.Id)).SingleAsync();
        Assert.Equal("blocked", row["status"]); Assert.Equal(1L, row["version"].AsInt64); Assert.False(row.Contains("delivery"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_producer_admin_and_self_incremented_epoch_send_once_and_acceptance_is_not_recovery(bool self)
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var (intent, session) = await Requested(data, self); await EnableSchema(data); var sends = 0;
        async Task<Auth0ProviderDeliveryResult> Send(BsonDocument original, Func<CancellationToken, Task<bool>> current,
            Func<CancellationToken, Task<bool>> start, CancellationToken ct) {
            Assert.Equal("blocked", original["status"]); Assert.Equal(1L, original["version"].AsInt64);
            Assert.True(await current(ct)); Assert.True(await start(ct)); sends++; return await Accepted();
        }
        var dispatcher = Dispatcher(data, Send);
        var result = await dispatcher.DispatchAsync(intent.Id, session.OpaqueToken);
        Assert.Equal("providerAccepted", result.Status); Assert.False(result.DeliveryVerified); Assert.False(result.RecoveryCompleted);
        Assert.False(result.CanAutomaticallyRetry);
        Assert.Equal("providerAccepted", (await dispatcher.DispatchAsync(intent.Id, session.OpaqueToken)).Status);
        Assert.Equal(1, sends);
        var row = await data.Rows("identityProviderIntents").Find(new BsonDocument("_id", intent.Id)).SingleAsync();
        Assert.DoesNotContain(session.OpaqueToken, row.ToJson());
        Assert.Equal(session.Session.Reference.SecurityVersion, row["delivery"]["actorSecurityVersion"].AsInt64);
        Assert.Equal(2L, row["securityVersion"].AsInt64);
    }

    [Fact]
    public async Task Permission_removed_after_lookup_refuses_durable_send_marker()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var (intent, session) = await Requested(data); await EnableSchema(data); var sends = 0;
        var dispatcher = Dispatcher(data, async (_, current, start, ct) => {
            Assert.True(await current(ct));
            await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", data.Maker.UserId),
                Builders<BsonDocument>.Update.Pull("permissions", Permissions.RecoveryInitiate), cancellationToken: ct);
            var marked = await start(ct); if (marked) sends++;
            Assert.False(marked);
            return new(Auth0ProviderDeliveryState.Blocked, "authority_changed", false);
        });
        var result = await dispatcher.DispatchAsync(intent.Id, session.OpaqueToken);
        Assert.Equal(0, sends); Assert.Equal("outcomeUnknown", result.Status);
        var row = await data.Rows("identityProviderIntents").Find(new BsonDocument("_id", intent.Id)).SingleAsync();
        Assert.Equal("claimed", row["status"]); Assert.True(row["delivery"]["dispatchedAt"].IsBsonNull);
    }

    [Fact]
    public async Task Ambiguous_send_and_expired_dispatched_lease_never_resend()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var (intent, session) = await Requested(data); await EnableSchema(data); var sends = 0;
        var dispatcher = Dispatcher(data, async (_, current, start, ct) => {
            Assert.True(await current(ct)); Assert.True(await start(ct)); sends++;
            data.Store.Clock.UtcNow = data.Store.Clock.UtcNow.AddMinutes(2);
            throw new HttpRequestException("Lost response without exposing provider body");
        });
        Assert.Equal("outcomeUnknown", (await dispatcher.DispatchAsync(intent.Id, session.OpaqueToken)).Status);
        Assert.Equal("outcomeUnknown", (await dispatcher.DispatchAsync(intent.Id, session.OpaqueToken)).Status);
        Assert.Equal(1, sends);
        Assert.Equal("dispatched", (await data.Rows("identityProviderIntents").Find(new BsonDocument("_id", intent.Id)).SingleAsync())["status"]);
    }

    [Fact]
    public async Task Target_epoch_changed_before_send_and_expired_factor_refuse_send()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var (intent, session) = await Requested(data); await EnableSchema(data); var sends = 0;
        var dispatcher = Dispatcher(data, async (_, current, start, ct) => {
            Assert.True(await current(ct));
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", intent.UserId),
                Builders<BsonDocument>.Update.Inc("securityVersion", 1L), cancellationToken: ct);
            Assert.False(await start(ct)); return new(Auth0ProviderDeliveryState.Blocked, "authority_changed", false);
        });
        Assert.Equal("outcomeUnknown", (await dispatcher.DispatchAsync(intent.Id, session.OpaqueToken)).Status);
        data.Store.Clock.UtcNow = data.Store.Clock.UtcNow.AddMinutes(6);
        Assert.Equal("authority_changed", (await Dispatcher(data, (_, _, _, _) => { sends++; return Accepted(); })
            .DispatchAsync(intent.Id, session.OpaqueToken)).Code);
        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData(Auth0ProviderDeliveryState.ProviderRejected, "providerRejected")]
    [InlineData(Auth0ProviderDeliveryState.OutcomeUnknown, "outcomeUnknown")]
    public async Task Rejected_and_ambiguous_results_are_durable_terminal_non_resend_outcomes(Auth0ProviderDeliveryState outcome, string status)
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var (intent, session) = await Requested(data); await EnableSchema(data); var sends = 0;
        var dispatcher = Dispatcher(data, async (_, current, start, ct) => {
            Assert.True(await current(ct)); Assert.True(await start(ct)); sends++;
            return new(outcome, "untrusted_provider_detail_is_not_persisted", true);
        });
        Assert.Equal(status, (await dispatcher.DispatchAsync(intent.Id, session.OpaqueToken)).Status);
        Assert.Equal(status, (await dispatcher.DispatchAsync(intent.Id, session.OpaqueToken)).Status);
        Assert.Equal(1, sends);
        var row = await data.Rows("identityProviderIntents").Find(new BsonDocument("_id", intent.Id)).SingleAsync();
        Assert.Equal(status, row["status"]); Assert.False(row["delivery"]["completedAt"].IsBsonNull);
        Assert.DoesNotContain("untrusted_provider_detail", row.ToJson());
    }

    [Fact]
    public async Task Authority_changed_after_post_refuses_success_completion_and_keeps_irreversible_marker()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var (intent, session) = await Requested(data); await EnableSchema(data); var sends = 0;
        var dispatcher = Dispatcher(data, async (_, current, start, ct) => {
            Assert.True(await current(ct)); Assert.True(await start(ct)); sends++;
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", intent.UserId),
                Builders<BsonDocument>.Update.Inc("securityVersion", 1L), cancellationToken: ct);
            return await Accepted();
        });
        Assert.Equal("outcomeUnknown", (await dispatcher.DispatchAsync(intent.Id, session.OpaqueToken)).Status);
        Assert.Equal("authority_changed", (await dispatcher.DispatchAsync(intent.Id, session.OpaqueToken)).Code);
        Assert.Equal(1, sends);
        var row = await data.Rows("identityProviderIntents").Find(new BsonDocument("_id", intent.Id)).SingleAsync();
        Assert.Equal("dispatched", row["status"]); Assert.True(row["delivery"]["completedAt"].IsBsonNull);
    }
}

// HTTP-only coverage of the exact production pre-message seam; no Mongo or external service.
public sealed class IdentityProviderIntentDeliveryHookTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durable_marker_must_succeed_after_lookups_before_the_only_post(bool markerAccepted)
    {
        const string issuer = "https://unit-toko.us.auth0.com/";
        var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
        var intent = new BsonDocument {
            { "_id", 1L }, { "userId", 2L }, { "requestedBy", 3L }, { "securityVersion", 2L }, { "version", 1L },
            { "kind", "passwordRecovery" }, { "status", "blocked" },
            { "protectedPayload", protector.Protect("account-recovery", new BsonDocument {
                { "issuer", issuer }, { "subject", "auth0|owned" }, { "kind", "password" },
                { "actorGrantVersion", 1L }, { "actorSecurityVersion", 1L }
            }.ToJson()) }
        };
        var lookups = 0; var markers = 0; var posts = 0;
        using var delivery = new Auth0ProviderDelivery(new() {
            Enabled = true, Issuer = issuer, ClientId = "client", ConnectionId = "con_owned", ConnectionName = "NativeDatabase"
        }, protector, _ => Task.FromResult("fake-management-token"), new Handler(request => {
            if (request.Method == HttpMethod.Post) {
                Assert.Equal(1, markers); Assert.True(markerAccepted); posts++;
                return new(HttpStatusCode.OK);
            }
            lookups++;
            var body = request.RequestUri!.AbsolutePath switch {
                "/api/v2/users/auth0%7Cowned" or "/api/v2/users/auth0|owned" => """{"user_id":"auth0|owned","email":"owner@example.test","email_verified":true,"identities":[{"provider":"auth0","user_id":"owned","connection":"NativeDatabase","isSocial":false}]}""",
                "/api/v2/connections/con_owned" => """{"id":"con_owned","name":"NativeDatabase","strategy":"auth0","options":{}}""",
                "/api/v2/connections/con_owned/clients" => """{"clients":[{"client_id":"client"}]}""",
                _ => throw new InvalidOperationException("Unexpected fixture lookup")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }));
        var result = await delivery.DeliverClaimedAsync(intent, _ => Task.FromResult(true), _ => {
            Assert.Equal(3, lookups); Assert.Equal(0, posts); markers++; return Task.FromResult(markerAccepted);
        }, CancellationToken.None);
        Assert.Equal(1, markers); Assert.Equal(markerAccepted ? 1 : 0, posts);
        Assert.Equal(markerAccepted ? Auth0ProviderDeliveryState.ProviderAccepted : Auth0ProviderDeliveryState.Blocked, result.State);
        Assert.Equal(markerAccepted, result.MessageRequestStarted);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }
}
