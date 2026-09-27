using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Authorization;
using UnitToko.Api.Identity;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class IdentityEndpointTests(MongoFixture fixture)
{
    [Fact]
    public async Task Anonymous_session_is_explicit_and_login_creates_only_hashed_pending_state_before_real_handler_challenge()
    {
        await using var host = await Host.CreateAsync(fixture);
        var response = await host.Client.GetAsync("/api/v1/identity/session");
        var session = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(session.GetProperty("authenticated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("account").ValueKind);
        Assert.Empty(session.GetProperty("stepUpOperations").EnumerateArray());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var login = await host.PostAsync("/auth/login", []);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var query = QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        var transaction = await host.Transactions.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal("/identity/account", transaction["returnPath"].AsString);
        Assert.Equal("pending", transaction["status"].AsString);
        Assert.DoesNotContain(query["unit_toko_challenge"].ToString(), transaction.ToJson());
        Assert.Equal(0, await host.Data.Database.GetCollection<BsonDocument>("identitySessions").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Theory]
    [InlineData("unknown", "unexpected")]
    [InlineData("returnPath", "https://outside.co.id/")]
    [InlineData("returnPath", "/%2foutside")]
    [InlineData("invitationId", "1")]
    [InlineData("invitationSecret", "secret-without-id")]
    public async Task Invalid_login_form_does_not_create_a_provider_transaction(string key, string value)
    {
        await using var host = await Host.CreateAsync(fixture);
        var response = await host.PostAsync("/auth/login", [new(key, value)]);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await host.Transactions.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Duplicate_fields_query_substitution_and_unknown_invitation_are_rejected()
    {
        await using var host = await Host.CreateAsync(fixture);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsync("/auth/login", [new("returnPath", "/"), new("returnPath", "/identity/account")])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsync("/auth/login?returnPath=/", [])).StatusCode);
        var multipart = await host.PostAsync("/auth/login", [], multipart: true);
        Assert.Contains(multipart.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.UnsupportedMediaType });
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsync("/auth/login", [new("invitationId", "99"), new("invitationSecret", new string('a', 64))])).StatusCode);
        Assert.Equal(0, await host.Transactions.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Approved_invitation_is_bound_to_server_state_without_consuming_it_or_exposing_its_secret_to_provider()
    {
        await using var host = await Host.CreateAsync(fixture);
        var (id, secret) = await host.ApprovedInvitationAsync();
        var response = await host.PostAsync("/auth/login", [new("invitationId", id.ToString()), new("invitationSecret", secret)]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var state = await host.Transactions.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal(id, state["invitationId"].AsInt64);
        Assert.Equal(OidcTransactionStore.Hash(secret), state["invitationSecretHash"].AsString);
        Assert.DoesNotContain(secret, state.ToJson());
        Assert.DoesNotContain(secret, response.Headers.Location!.OriginalString);
        Assert.Equal("approved", (await host.Data.Database.GetCollection<BsonDocument>("accountInvitations")
            .Find(new BsonDocument("_id", id)).SingleAsync())["status"].AsString);
    }

    [Fact]
    public async Task Verified_session_keeps_long_id_precision_and_original_factor_time_and_only_lists_current_step_up_eligibility()
    {
        await using var host = await Host.CreateAsync(fixture);
        var issued = await host.SignInAsync();
        host.Data.Clock.UtcNow += TimeSpan.FromMinutes(6);
        var response = await host.GetAsync("/api/v1/identity/session", issued.OpaqueToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("authenticated").GetBoolean());
        Assert.Equal("9007199254740993", body.GetProperty("account").GetProperty("userId").GetString());
        Assert.Equal(issued.Session.Proof.FactorCompletedAt, body.GetProperty("factorCompletedAt").GetDateTimeOffset());
        Assert.Equal(issued.Session.Proof.FactorCompletedAt + SessionPolicy.FreshFactorLifetime, body.GetProperty("freshFactorUntil").GetDateTimeOffset());
        Assert.Equal(issued.Session.AbsoluteExpiresAt, body.GetProperty("absoluteExpiresAt").GetDateTimeOffset());
        Assert.Equal(Permissions.RecoveryInitiate, Assert.Single(body.GetProperty("stepUpOperations").EnumerateArray()).GetString());
        Assert.DoesNotContain(issued.OpaqueToken, body.ToString());
        Assert.DoesNotContain(issued.Session.Reference.Id, body.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsync("/auth/login", [], issued.OpaqueToken)).StatusCode);
    }

    [Fact]
    public async Task Step_up_checks_expected_actor_and_real_authority_without_requiring_already_fresh_factor()
    {
        await using var host = await Host.CreateAsync(fixture);
        var issued = await host.SignInAsync();
        host.Data.Clock.UtcNow += TimeSpan.FromMinutes(6);
        var fields = new List<KeyValuePair<string, string>> { new("operation", Permissions.RecoveryInitiate), new("expectedActorId", issued.Session.UserId.ToString()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync("/auth/step-up", fields)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsync("/auth/step-up", [new("operation", Permissions.RecoveryInitiate), new("expectedActorId", "42")], issued.OpaqueToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsync("/auth/step-up", [new("operation", Permissions.RefundApprove), fields[1]], issued.OpaqueToken)).StatusCode);
        Assert.Equal(0, await host.Transactions.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(HttpStatusCode.Redirect, (await host.PostAsync("/auth/step-up", fields, issued.OpaqueToken)).StatusCode);
        var state = await host.Transactions.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal("stepUp", state["purpose"].AsString);
        Assert.Equal(issued.Session.UserId, state["expectedUserId"].AsInt64);
        Assert.Equal(issued.Session.Reference.Id, state["expectedSessionHash"].AsString);
        Assert.Equal(issued.Session.Reference.Version, state["expectedSessionVersion"].AsInt64);
        Assert.Equal(issued.Session.Reference.SecurityVersion, state["expectedSecurityVersion"].AsInt64);
        Assert.Equal(issued.Session.GrantVersion, state["expectedGrantVersion"].AsInt64);
        Assert.Equal(Permissions.RecoveryInitiate, state["operation"].AsString);
        Assert.NotNull(await host.Sessions.ReadAsync(issued.OpaqueToken));
    }

    [Fact]
    public async Task Suspended_membership_cannot_start_step_up_and_logout_requires_the_reviewed_actor_then_revokes_local_family()
    {
        await using var host = await Host.CreateAsync(fixture);
        var issued = await host.SignInAsync();
        await host.Data.Database.GetCollection<BsonDocument>("memberships").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
            Builders<BsonDocument>.Update.Set("status", "suspended"));
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsync("/auth/step-up", [new("operation", Permissions.RecoveryInitiate), new("expectedActorId", issued.Session.UserId.ToString())], issued.OpaqueToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsync("/auth/logout", [new("expectedActorId", "42")], issued.OpaqueToken)).StatusCode);
        Assert.NotNull(await host.Sessions.ReadAsync(issued.OpaqueToken));
        var logout = await host.PostAsync("/auth/logout", [new("expectedActorId", issued.Session.UserId.ToString())], issued.OpaqueToken);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/identity/login?loggedOut=1", logout.Headers.Location!.OriginalString);
        Assert.Null(await host.Sessions.ReadAsync(issued.OpaqueToken));
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), value => value.StartsWith(OpaqueSessionCookie.Name + "=;", StringComparison.Ordinal));
    }

    private sealed class Host(StoreTestData data, WebApplication app, HttpClient client, Auth0Options settings,
        MongoIdentitySessionStore sessions, IdentityOnboardingService onboarding, MongoUnitOfWork unit, SessionPayloadProtector protector) : IAsyncDisposable
    {
        public StoreTestData Data => data;
        public HttpClient Client => client;
        public MongoIdentitySessionStore Sessions => sessions;
        public IMongoCollection<BsonDocument> Transactions => data.Database.GetCollection<BsonDocument>("oidcTransactions");
        public static async Task<Host> CreateAsync(MongoFixture fixture)
        {
            var data = await StoreTestData.CreateAsync(fixture);
            data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var settings = Auth0ClaimsTests.Settings();
            settings.AllowedReturnPaths = ["/", "/identity/account"];
            var unit = new MongoUnitOfWork(fixture.Client);
            var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
            var policy = new SessionPolicy(settings.Issuer, settings.ConnectionId, settings.AssurancePolicyVersion);
            var sessions = new MongoIdentitySessionStore(data.Database, unit, data.Clock, policy, protector);
            var onboarding = new IdentityOnboardingService(data.Database, data.Store, new(data.Database), protector, policy, data.Clock);
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddAntiforgery();
            builder.Services.AddSingleton<IClock>(data.Clock); builder.Services.AddSingleton(data.Store);
            builder.Services.AddSingleton(sessions); builder.Services.AddSingleton(onboarding);
            builder.Services.AddSingleton(new OidcTransactionStore(data.Database, data.Clock));
            builder.Services.AddSingleton<IOptions<Auth0Options>>(Options.Create(settings));
            builder.Services.AddSingleton<IOidcSignInCompletion, UnusedCompletion>(); builder.Services.AddScoped<Auth0OidcEvents>();
            builder.Services.AddAuthentication(Auth0OidcAdapter.SessionScheme)
                .AddScheme<AuthenticationSchemeOptions, OpaqueSessionAuthenticationHandler>(Auth0OidcAdapter.SessionScheme, _ => { })
                .AddOpenIdConnect(Auth0Options.Scheme, options =>
                {
                    Auth0OidcAdapter.Configure(options, settings);
                    options.Configuration = new OpenIdConnectConfiguration { Issuer = settings.Issuer, AuthorizationEndpoint = settings.Issuer + "authorize" };
                });
            builder.Services.AddAuthorization();
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (DomainValidationException error) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = error.Code }); }
                catch (AuthorizationDeniedException error) { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { code = error.Code }); }
                catch (PersistenceConflictException error) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { code = error.Code }); }
            });
            app.UseAuthentication(); app.UseAuthorization();
            app.Use(async (context, next) =>
            {
                if (HttpMethods.IsPost(context.Request.Method))
                    await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
                await next(context);
            });
            app.MapGet("/test/csrf", (HttpContext http, IAntiforgery anti) => Results.Json(new { requestToken = anti.GetAndStoreTokens(http).RequestToken }));
            app.MapIdentityEndpoints();
            await app.StartAsync(); var client = app.GetTestClient(); client.BaseAddress = new(settings.PublicOrigin);
            return new(data, app, client, settings, sessions, onboarding, unit, protector);
        }
        public async Task<(long Id, string Secret)> ApprovedInvitationAsync()
        {
            var maker = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.InviteManage, Permissions.MembershipPropose);
            var checker = await data.AddActorAsync(1001, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.MembershipApprove);
            await data.Database.GetCollection<BsonDocument>("identities").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
                Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            var invite = await onboarding.ProposeAsync(maker, new("invitee@company.co.id", new(GrantScopeKind.BuyerOwned, 10, 11), data.Clock.UtcNow.AddDays(1)),
                new("invite", "Reviewed synthetic account invitation", "endpoint-test"));
            await onboarding.ApproveAsync(checker, invite.Id, 1, new("approve", "Independent synthetic approval", "endpoint-test"));
            var intent = await data.Database.GetCollection<BsonDocument>("identityProviderIntents").Find(new BsonDocument("invitationId", invite.Id)).SingleAsync();
            var payload = BsonDocument.Parse(protector.Unprotect("invitation-delivery", intent["protectedPayload"].AsString));
            return (invite.Id, payload["secret"].AsString);
        }
        public async Task<SessionIssue> SignInAsync()
        {
            var actor = await data.AddActorAsync(9007199254740993, new(GrantScopeKind.System), Permissions.RecoveryInitiate);
            await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("issuer", settings.Issuer));
            var proof = new ProviderIdentityProof(settings.Issuer, actor.ProviderSubject, "approved@company.co.id", true, settings.ConnectionId,
                data.Clock.UtcNow, data.Clock.UtcNow, "otp", settings.AssurancePolicyVersion, DisplayName: "Synthetic account");
            return await unit.ExecuteAsync(async (tx, ct) => await sessions.CreateAsync(tx, await onboarding.AdmitAsync(tx, proof, null, ct), proof, "fixture", ct));
        }
        public async Task<HttpResponseMessage> GetAsync(string path, string? cookie = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (cookie is not null) request.Headers.Add("Cookie", OpaqueSessionCookie.Name + "=" + cookie);
            return await client.SendAsync(request);
        }
        public async Task<HttpResponseMessage> PostAsync(string path, IEnumerable<KeyValuePair<string, string>> fields, string? cookie = null, bool multipart = false)
        {
            var csrf = await GetAsync("/test/csrf", cookie);
            var token = (await csrf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestToken").GetString()!;
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            var entries = fields.Append(new("__RequestVerificationToken", token));
            if (multipart)
            {
                var form = new MultipartFormDataContent();
                foreach (var entry in entries) form.Add(new StringContent(entry.Value), entry.Key);
                request.Content = form;
            }
            else request.Content = new FormUrlEncodedContent(entries);
            var cookies = csrf.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]).ToList();
            if (cookie is not null) cookies.Add(OpaqueSessionCookie.Name + "=" + cookie);
            request.Headers.Add("Cookie", string.Join("; ", cookies));
            return await client.SendAsync(request);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); await data.DisposeAsync(); }
    }
    private sealed class UnusedCompletion : IOidcSignInCompletion
    {
        public Task CompleteAsync(HttpContext http, string stateToken, OidcTransaction transaction, ProviderIdentityProof proof, ProviderTokenEnvelope tokens, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Endpoint tests never send a provider callback");
    }
}
