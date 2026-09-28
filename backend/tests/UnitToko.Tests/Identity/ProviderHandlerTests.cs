using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

// All fake-provider code is confined to this test assembly. The actual OIDC middleware,
// signature/nonce/state checks, PKCE exchange, mapper and durable transaction store run unchanged.
[Collection("Mongo")]
public sealed class ProviderHandlerTests(MongoFixture fixture)
{
    [Fact]
    public async Task Valid_RSA_code_PKCE_callback_issues_only_opaque_session_and_consumes_state()
    {
        await using var host = await ProviderHost.CreateAsync(fixture);
        var challenge = await host.ChallengeAsync();
        Assert.Equal("code", challenge.Query["response_type"]); Assert.Equal("S256", challenge.Query["code_challenge_method"]);
        Assert.True(!challenge.Query.TryGetValue("response_mode", out var responseMode) || responseMode == "query");
        Assert.Equal("login", challenge.Query["prompt"]); Assert.Equal("0", challenge.Query["max_age"]);
        Assert.Equal(Auth0ClaimsTests.Settings().PublicOrigin + "/auth/callback", challenge.Query["redirect_uri"]);
        Assert.Contains("__Host-ut_oidc_correlation.", challenge.Cookies); Assert.Contains("__Host-ut_oidc_nonce.", challenge.Cookies);
        var response = await host.CallbackAsync(challenge);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); Assert.Equal("/administration/access", response.Headers.Location?.OriginalString);
        Assert.Equal(1, host.Completion.Count); Assert.True(host.Provider.SawValidPkce);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-ut_session=", StringComparison.Ordinal) && value.Contains("httponly", StringComparison.OrdinalIgnoreCase) && value.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("id_token", await response.Content.ReadAsStringAsync());
        Assert.Equal("auth0|fixture-user", host.Completion.LastProof!.Subject);
        var replay = await host.CallbackAsync(challenge);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode); Assert.Equal(1, host.Completion.Count);
    }
    [Theory]
    [InlineData("nonce")]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("email")]
    [InlineData("factor")]
    [InlineData("binding")]
    public async Task Invalid_callback_never_completes_business_session(string variation)
    {
        await using var host = await ProviderHost.CreateAsync(fixture);
        var challenge = await host.ChallengeAsync(); host.Provider.Variation = variation;
        var response = await host.CallbackAsync(challenge);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal(0, host.Completion.Count);
        Assert.Contains("provider_authentication_failed", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [], value => value.StartsWith("__Host-ut_session=", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_correlation_cookie_or_tampered_state_does_not_reach_token_exchange(bool removeCookie)
    {
        await using var host = await ProviderHost.CreateAsync(fixture);
        var challenge = await host.ChallengeAsync();
        var response = await host.CallbackAsync(challenge, removeCookie, !removeCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal(0, host.Completion.Count); Assert.Equal(0, host.Provider.TokenRequests);
    }

    private sealed record Challenge(Dictionary<string, string> Query, string Cookies);
    private sealed class ProviderHost : IAsyncDisposable
    {
        private readonly StoreTestData data;
        private readonly WebApplication app;
        private readonly HttpClient client;
        public FakeProvider Provider { get; }
        public TestCompletion Completion { get; }
        private ProviderHost(StoreTestData data, WebApplication app, HttpClient client, FakeProvider provider, TestCompletion completion)
        { this.data = data; this.app = app; this.client = client; Provider = provider; Completion = completion; }
        public static async Task<ProviderHost> CreateAsync(MongoFixture fixture)
        {
            var data = await StoreTestData.CreateAsync(fixture); data.Clock.UtcNow = DateTimeOffset.UtcNow;
            var configured = Auth0ClaimsTests.Settings();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Auth0:Enabled"] = "true", ["Identity:Auth0:Issuer"] = configured.Issuer,
                ["Identity:Auth0:PublicOrigin"] = configured.PublicOrigin, ["Identity:Auth0:ClientId"] = configured.ClientId,
                ["Identity:Auth0:ClientSecret"] = configured.ClientSecret, ["Identity:Auth0:ConnectionId"] = configured.ConnectionId,
                ["Identity:Auth0:ConnectionName"] = configured.ConnectionName, ["Identity:Auth0:ClaimsNamespace"] = configured.ClaimsNamespace,
                ["Identity:Auth0:AssurancePolicyVersion"] = configured.AssurancePolicyVersion
            });
            var transactions = new OidcTransactionStore(data.Database, data.Clock);
            var completion = new TestCompletion(transactions, new(fixture.Client));
            var provider = new FakeProvider(configured, data.Clock);
            builder.Services.AddSingleton<IClock>(data.Clock); builder.Services.AddSingleton(transactions);
            builder.Services.AddSingleton<IOidcSignInCompletion>(completion);
            builder.Services.AddAuthentication(Auth0OidcAdapter.SessionScheme)
                .AddScheme<AuthenticationSchemeOptions, NeverSignInHandler>(Auth0OidcAdapter.SessionScheme, _ => { });
            builder.Services.AddUnitTokoAuth0(builder.Configuration);
            builder.Services.Configure<OpenIdConnectOptions>(Auth0Options.Scheme, options =>
            {
                options.Backchannel = new HttpClient(provider) { Timeout = TimeSpan.FromSeconds(5) };
            });
            var app = builder.Build(); app.UseAuthentication();
            app.MapGet("/test/challenge", async (HttpContext context) =>
            {
                var start = await transactions.BeginAsync(new(OidcTransactionPurpose.Login, configured.Issuer, configured.ClientId, "/administration/access"));
                await context.ChallengeAsync(Auth0Options.Scheme, UnitTokoOidcChallenge.Properties(start));
            });
            await app.StartAsync(); var client = app.GetTestClient(); client.BaseAddress = new(configured.PublicOrigin);
            return new(data, app, client, provider, completion);
        }
        public async Task<Challenge> ChallengeAsync()
        {
            var response = await client.GetAsync("/test/challenge");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
            var cookies = response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]).ToArray();
            foreach (var cookie in response.Headers.GetValues("Set-Cookie")) { Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase); Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase); }
            Provider.Challenge = query;
            return new(query, string.Join("; ", cookies));
        }
        public async Task<HttpResponseMessage> CallbackAsync(Challenge challenge, bool removeCookie = false, bool tamperState = false)
        {
            var state = tamperState ? "invalid-state" : challenge.Query["state"];
            using var request = new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString("/auth/callback", new Dictionary<string, string?> { ["code"] = "fixture-code", ["state"] = state }));
            if (!removeCookie) request.Headers.Add("Cookie", challenge.Cookies);
            return await client.SendAsync(request);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); Provider.Dispose(); await data.DisposeAsync(); }
    }
    public sealed class NeverSignInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
    private sealed class TestCompletion(OidcTransactionStore transactions, MongoUnitOfWork unit) : IOidcSignInCompletion
    {
        public int Count { get; private set; }
        public ProviderIdentityProof? LastProof { get; private set; }
        public async Task CompleteAsync(HttpContext http, string stateToken, OidcTransaction transaction, ProviderIdentityProof proof, ProviderTokenEnvelope tokens, CancellationToken cancellationToken)
        {
            await unit.ExecuteAsync(async (session, token) =>
            { await transactions.ConsumeAsync(await transactions.ReadPendingAsync(stateToken, session, token), session, token); return true; }, cancellationToken);
            Count++; LastProof = proof;
            http.Response.Cookies.Append("__Host-ut_session", "opaque-test-reference", new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/" });
        }
    }
    internal sealed class FakeProvider(Auth0Options settings, IClock clock) : HttpMessageHandler
    {
        private readonly RSA rsa = RSA.Create(2048);
        private readonly RSA wrongRsa = RSA.Create(2048);
        public Dictionary<string, string> Challenge { get; set; } = [];
        public string? Variation { get; set; }
        public string Subject { get; set; } = "auth0|fixture-user";
        public string Email { get; set; } = "approved@company.co.id";
        public bool SawValidPkce { get; private set; }
        public int TokenRequests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(new Uri(settings.Issuer).Host, request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath == "/.well-known/openid-configuration") return Json(new
            {
                issuer = settings.Issuer, authorization_endpoint = settings.Issuer + "authorize", token_endpoint = settings.Issuer + "oauth/token",
                jwks_uri = settings.Issuer + ".well-known/jwks.json", end_session_endpoint = settings.Issuer + "oidc/logout",
                response_types_supported = new[] { "code" }, subject_types_supported = new[] { "public" }, id_token_signing_alg_values_supported = new[] { "RS256" }
            });
            if (request.RequestUri.AbsolutePath == "/.well-known/jwks.json")
            {
                var key = rsa.ExportParameters(false);
                return Json(new { keys = new[] { new { kty = "RSA", kid = "fixture-rsa", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) } } });
            }
            Assert.Equal("/oauth/token", request.RequestUri.AbsolutePath); Assert.Equal(HttpMethod.Post, request.Method); TokenRequests++;
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(settings.ClientId, form["client_id"].ToString()); Assert.Equal(settings.ClientSecret, form["client_secret"].ToString());
            Assert.Equal(settings.PublicOrigin + Auth0Options.CallbackPath, form["redirect_uri"].ToString());
            Assert.Equal("authorization_code", form["grant_type"].ToString());
            var digest = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
            Assert.Equal(Challenge["code_challenge"], digest); SawValidPkce = true;
            var now = clock.UtcNow;
            var signing = new RsaSecurityKey(Variation == "signature" ? wrongRsa : rsa) { KeyId = "fixture-rsa" };
            var payload = new JwtPayload(Variation == "issuer" ? "https://wrong-issuer.co.id/" : settings.Issuer,
                Variation == "audience" ? "wrong-client" : settings.ClientId, [], now.AddMinutes(-10).UtcDateTime,
                (Variation == "expired" ? now.AddMinutes(-5) : now.AddMinutes(5)).UtcDateTime, now.UtcDateTime)
            {
                ["sub"] = Subject, ["nonce"] = Variation == "nonce" ? "wrong-nonce" : Challenge["nonce"],
                ["email"] = Email, ["email_verified"] = Variation != "email", ["auth_time"] = now.AddSeconds(-10).ToUnixTimeSeconds(),
                [settings.ClaimsNamespace + "assurance"] = new Dictionary<string, object>
                {
                    ["policyVersion"] = settings.AssurancePolicyVersion, ["connectionId"] = settings.ConnectionId, ["method"] = "otp",
                    ["factorCompletedAt"] = (Variation == "factor" ? now.AddMinutes(-6) : now).ToUnixTimeSeconds(),
                    ["challengeBinding"] = Variation == "binding" ? new string('B', 43) : Challenge["unit_toko_challenge"]
                }
            };
            var idToken = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(new SigningCredentials(signing, SecurityAlgorithms.RsaSha256)), payload));
            return Json(new { id_token = idToken, access_token = "never-retained-test-token", token_type = "Bearer", expires_in = 300 });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { if (disposing) { rsa.Dispose(); wrongRsa.Dispose(); } base.Dispose(disposing); }
    }
}
