using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace UnitToko.LocalPilot.Tests;

public sealed class SyntheticOidcTests
{
    private static readonly PilotAccount Account = new(1001, "Pemohon SINTETIS", "pemohon-a", "private-test-password", "123456");
    private static readonly PilotSecrets Secrets = new(Guid.NewGuid().ToString("N"), "private-client-secret", "unused", [Account]);

    [Fact]
    public async Task Password_and_explicit_simulated_factor_produce_signed_bound_one_use_code()
    {
        using var key = RSA.Create(2048); using var provider = new SyntheticOidcProvider(Secrets, key);
        using var client = new HttpClient(provider, false);
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = await Challenge(provider, verifier);
        var wrong = await Login(provider, challenge, "wrong-factor");
        Assert.Equal(200, wrong.Response.StatusCode); Assert.False(wrong.Response.Headers.ContainsKey("Location"));
        Assert.Equal("same-origin", wrong.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; form-action 'self' " + PilotBoundary.Origin + "; frame-ancestors 'none'; base-uri 'none'",
            wrong.Response.Headers.ContentSecurityPolicy.ToString());
        var login = await Login(provider, challenge, Account.FactorCode);
        Assert.Equal(302, login.Response.StatusCode);
        var redirect = new Uri(login.Response.Headers.Location.ToString());
        Assert.Equal(PilotBoundary.Origin + "/auth/callback", redirect.GetLeftPart(UriPartial.Path));
        var parameters = QueryHelpers.ParseQuery(redirect.Query);
        Assert.Equal("state-12345678901234567890", parameters["state"]);
        var code = parameters["code"].ToString();
        using var token = await Exchange(client, code, verifier);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        using var body = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        var principal = new JwtSecurityTokenHandler().ValidateToken(body.RootElement.GetProperty("id_token").GetString(), new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = PilotBoundary.Issuer, ValidateAudience = true, ValidAudience = SyntheticOidcProvider.ClientId,
            ValidateIssuerSigningKey = true, IssuerSigningKey = new RsaSecurityKey(key), ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], NameClaimType = "sub"
        }, out var validated);
        Assert.NotNull(principal);
        var jwt = Assert.IsType<JwtSecurityToken>(validated);
        Assert.Equal(Account.Subject, jwt.Subject);
        Assert.Equal("nonce-12345678901234567890", jwt.Payload["nonce"]);
        Assert.Contains(SyntheticOidcProvider.ClaimsNamespace + "assurance", jwt.Payload.Keys);
        using var replay = await Exchange(client, code, verifier);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Foreign_origin_bad_pkce_and_arbitrary_redirect_cannot_issue_session_tokens()
    {
        using var key = RSA.Create(2048); using var provider = new SyntheticOidcProvider(Secrets, key); using var client = new HttpClient(provider, false);
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = await Challenge(provider, verifier);
        foreach (var origin in new[] { "null", "https://elsewhere.invalid" })
        {
            var denied = await Login(provider, challenge, Account.FactorCode, origin);
            Assert.Equal(400, denied.Response.StatusCode);
            Assert.False(denied.Response.Headers.ContainsKey("Location"));
        }
        var login = await Login(provider, challenge, Account.FactorCode);
        var code = QueryHelpers.ParseQuery(new Uri(login.Response.Headers.Location.ToString()).Query)["code"].ToString();
        using var rejected = await Exchange(client, code, new string('x', 43));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var consumed = await Exchange(client, code, verifier);
        Assert.Equal(HttpStatusCode.BadRequest, consumed.StatusCode);
        var arbitrary = Context("GET", "/authorize");
        arbitrary.Request.QueryString = QueryString.Create("redirect_uri", "https://elsewhere.invalid/callback");
        await provider.HandleAsync(arbitrary);
        Assert.Equal(400, arbitrary.Response.StatusCode);
        using var remote = await client.GetAsync("https://elsewhere.invalid/.well-known/openid-configuration");
        Assert.Equal(HttpStatusCode.BadRequest, remote.StatusCode);
    }

    private sealed record Pending(string Transaction, string Csrf);
    private static async Task<Pending> Challenge(SyntheticOidcProvider provider, string verifier)
    {
        var context = Context("GET", "/authorize");
        context.Request.QueryString = QueryString.Create(new Dictionary<string, string?>
        {
            ["client_id"] = SyntheticOidcProvider.ClientId, ["redirect_uri"] = PilotBoundary.Origin + "/auth/callback", ["response_type"] = "code",
            ["code_challenge_method"] = "S256", ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["state"] = "state-12345678901234567890", ["nonce"] = "nonce-12345678901234567890", ["unit_toko_challenge"] = new string('b', 43)
        });
        await provider.HandleAsync(context);
        Assert.Equal("same-origin", context.Response.Headers["Referrer-Policy"].ToString());
        context.Response.Body.Position = 0;
        var html = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return new(Regex.Match(html, "name=\"transaction\" value=\"([^\"]+)\"").Groups[1].Value,
            Regex.Match(html, "name=\"csrf\" value=\"([^\"]+)\"").Groups[1].Value);
    }
    private static async Task<DefaultHttpContext> Login(SyntheticOidcProvider provider, Pending pending, string factor, string? origin = null)
    {
        var context = Context("POST", "/local-login");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["transaction"] = pending.Transaction, ["csrf"] = pending.Csrf,
            ["username"] = Account.Username, ["password"] = Account.Password, ["factor"] = factor });
        var bytes = await form.ReadAsByteArrayAsync(); context.Request.Body = new MemoryStream(bytes); context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = "application/x-www-form-urlencoded"; context.Request.Headers.Origin = origin ?? PilotBoundary.Issuer.TrimEnd('/');
        context.Request.Headers.Cookie = "__Host-ut_pilot_login=" + pending.Csrf;
        await provider.HandleAsync(context); return context;
    }
    private static Task<HttpResponseMessage> Exchange(HttpClient client, string code, string verifier) => client.PostAsync(PilotBoundary.Issuer + "oauth/token",
        new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = SyntheticOidcProvider.ClientId, ["client_secret"] = Secrets.ClientSecret,
            ["redirect_uri"] = PilotBoundary.Origin + "/auth/callback", ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = verifier }));
    private static DefaultHttpContext Context(string method, string path)
    {
        var context = new DefaultHttpContext(); context.Request.Method = method; context.Request.Path = path; context.Request.Scheme = "https";
        context.Request.Host = new HostString(PilotBoundary.ProviderHost); context.Response.Body = new MemoryStream(); return context;
    }
}
