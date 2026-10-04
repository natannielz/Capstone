using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace UnitToko.LocalPilot;

// Only the separate LocalPilot executable constructs this handler. It is never API DI registration.
public sealed class SyntheticOidcProvider(PilotSecrets secrets, RSA signingKey) : HttpMessageHandler
{
    public const string ClientId = "unit-toko-synthetic-local-pilot";
    public const string ConnectionId = "con_synthetic_local_pilot";
    public const string ConnectionName = "SYNTHETIC-local-only";
    public const string ClaimsNamespace = PilotBoundary.Origin + "/claims/";
    private const string Cookie = "__Host-ut_pilot_login";
    private sealed record Challenge(string State, string Nonce, string ChallengeBinding, string Pkce, string Csrf, DateTimeOffset ExpiresAt);
    private sealed record Code(Challenge Challenge, PilotAccount Account, DateTimeOffset AuthenticatedAt, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, Challenge> challenges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Code> codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> failures = new(StringComparer.Ordinal);

    public async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        // Keep the browser's same-origin form Origin; no-referrer produces Origin:null.
        // Cross-origin navigation still omits the referrer (including authorization parameters).
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self' " + PilotBoundary.Origin + "; frame-ancestors 'none'; base-uri 'none'";
        RemoveExpired();
        if (context.Request.Method == "GET" && context.Request.Path == "/authorize")
        {
            var query = context.Request.Query;
            var state = query["state"].ToString(); var nonce = query["nonce"].ToString();
            var binding = query["unit_toko_challenge"].ToString(); var pkce = query["code_challenge"].ToString();
            if (query["client_id"] != ClientId || query["redirect_uri"] != PilotBoundary.Origin + "/auth/callback" ||
                query["response_type"] != "code" || query["code_challenge_method"] != "S256" ||
                state.Length is < 16 or > 8192 || nonce.Length is < 16 or > 1024 || binding.Length != 43 || pkce.Length != 43 || challenges.Count >= 128)
            { await Deny(context); return; }
            var id = Random(); var csrf = Random();
            challenges[id] = new(state, nonce, binding, pkce, csrf, DateTimeOffset.UtcNow.AddMinutes(5));
            context.Response.Cookies.Append(Cookie, csrf, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromMinutes(5) });
            await Form(context, id, csrf, null); return;
        }
        if (context.Request.Method == "POST" && context.Request.Path == "/local-login")
        {
            if (!context.Request.HasFormContentType || context.Request.Headers.Origin != PilotBoundary.Issuer.TrimEnd('/') || context.Request.ContentLength > 16384)
            { await Deny(context); return; }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var id = form["transaction"].ToString(); var csrf = form["csrf"].ToString();
            if (!challenges.TryGetValue(id, out var challenge) || challenge.ExpiresAt <= DateTimeOffset.UtcNow ||
                !Equal(csrf, challenge.Csrf) || !Equal(context.Request.Cookies[Cookie], challenge.Csrf))
            { await Deny(context); return; }
            var account = secrets.Accounts.SingleOrDefault(x => x.Username == form["username"]);
            if (account is null || !Equal(form["password"].ToString(), account.Password) || !Equal(form["factor"].ToString(), account.FactorCode))
            {
                if (failures.AddOrUpdate(id, 1, (_, count) => count + 1) >= 5) { challenges.TryRemove(id, out _); await Deny(context); return; }
                await Form(context, id, csrf, "Akun, kata sandi, atau kode faktor simulasi tidak cocok."); return;
            }
            if (!challenges.TryRemove(id, out _)) { await Deny(context); return; }
            failures.TryRemove(id, out _);
            var code = Random(); var now = DateTimeOffset.UtcNow;
            codes[code] = new(challenge, account, now, now.AddMinutes(2));
            context.Response.Cookies.Delete(Cookie, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
            context.Response.Redirect(QueryHelpers.AddQueryString(PilotBoundary.Origin + "/auth/callback", new Dictionary<string, string?> { ["code"] = code, ["state"] = challenge.State })); return;
        }
        if (context.Request.Method == "GET" && context.Request.Path == "/oidc/logout" &&
            context.Request.Query["post_logout_redirect_uri"] == PilotBoundary.Origin + "/auth/signed-out")
        {
            var state = context.Request.Query["state"].ToString();
            if (state.Length > 8192) { await Deny(context); return; }
            context.Response.Redirect(QueryHelpers.AddQueryString(PilotBoundary.Origin + "/auth/signed-out", "state", state)); return;
        }
        await Deny(context);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } uri || uri.Scheme != "https" || uri.Host != PilotBoundary.ProviderHost || !uri.IsDefaultPort)
            return Failure();
        if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/.well-known/openid-configuration") return Json(new
        {
            issuer = PilotBoundary.Issuer, authorization_endpoint = PilotBoundary.Issuer + "authorize", token_endpoint = PilotBoundary.Issuer + "oauth/token",
            jwks_uri = PilotBoundary.Issuer + ".well-known/jwks.json", end_session_endpoint = PilotBoundary.Issuer + "oidc/logout",
            response_types_supported = new[] { "code" }, subject_types_supported = new[] { "public" }, id_token_signing_alg_values_supported = new[] { "RS256" },
            code_challenge_methods_supported = new[] { "S256" }
        });
        if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/.well-known/jwks.json")
        {
            var key = signingKey.ExportParameters(false);
            return Json(new { keys = new[] { new { kty = "RSA", kid = "synthetic-pilot-rsa", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) } } });
        }
        if (request.Method != HttpMethod.Post || uri.AbsolutePath != "/oauth/token" || request.Content is null) return Failure();
        var body = await request.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 16384) return Failure();
        var form = QueryHelpers.ParseQuery(body);
        if (form["client_id"] != ClientId || !Equal(form["client_secret"].ToString(), secrets.ClientSecret) ||
            form["redirect_uri"] != PilotBoundary.Origin + "/auth/callback" || form["grant_type"] != "authorization_code" ||
            !codes.TryRemove(form["code"].ToString(), out var code) || code.ExpiresAt <= DateTimeOffset.UtcNow) return Failure();
        var verifier = form["code_verifier"].ToString();
        if (verifier.Length is < 43 or > 128 || !Equal(Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), code.Challenge.Pkce)) return Failure();
        var now = DateTimeOffset.UtcNow;
        var payload = new JwtPayload(PilotBoundary.Issuer, ClientId, [], now.AddSeconds(-5).UtcDateTime, now.AddMinutes(5).UtcDateTime, now.UtcDateTime)
        {
            ["sub"] = code.Account.Subject, ["name"] = code.Account.Name, ["nonce"] = code.Challenge.Nonce,
            ["email"] = code.Account.Email, ["email_verified"] = true, ["auth_time"] = code.AuthenticatedAt.ToUnixTimeSeconds(),
            [ClaimsNamespace + "assurance"] = new Dictionary<string, object>
            {
                ["policyVersion"] = "unit-toko-otp-v1", ["connectionId"] = ConnectionId, ["method"] = "otp",
                ["factorCompletedAt"] = code.AuthenticatedAt.ToUnixTimeSeconds(), ["challengeBinding"] = code.Challenge.ChallengeBinding
            }
        };
        var signing = new SigningCredentials(new RsaSecurityKey(signingKey) { KeyId = "synthetic-pilot-rsa" }, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(signing), payload));
        return Json(new { id_token = token, access_token = "synthetic-local-no-external-access", token_type = "Bearer", expires_in = 300 });
    }

    private static bool Equal(string? left, string right) => left is not null && left.Length <= 16384 &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(left)), SHA256.HashData(Encoding.UTF8.GetBytes(right)));
    private static string Random() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in challenges.Where(x => x.Value.ExpiresAt <= now)) { challenges.TryRemove(item.Key, out _); failures.TryRemove(item.Key, out _); }
        foreach (var item in codes.Where(x => x.Value.ExpiresAt <= now)) codes.TryRemove(item.Key, out _);
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Failure() => new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json") };
    private static async Task Deny(HttpContext context) { context.Response.StatusCode = 400; await context.Response.WriteAsync("Permintaan masuk lokal tidak berlaku. Mulai lagi dari Akun saya."); }
    private static Task Form(HttpContext context, string id, string csrf, string? error)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        var message = error is null ? "" : $"<p role=alert>{WebUtility.HtmlEncode(error)}</p>";
        return context.Response.WriteAsync($$$"""
            <!doctype html><html lang="id"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Masuk pilot lokal · SINTETIS</title>
            <style>body{font:16px system-ui;background:#f2f6f7;color:#1d3640;margin:0}main{max-width:28rem;margin:4rem auto;padding:1.5rem;background:white;border:1px solid #b6c8ca;border-radius:12px}label{display:block;margin:1rem 0}input,button{display:block;box-sizing:border-box;width:100%;font:inherit;padding:.75rem;margin-top:.35rem}button{background:#087f8c;color:white;border:0;border-radius:6px}a{color:#075967}strong{color:#8b4100}@media(max-width:520px){main{margin:1rem}}</style>
            <main><h1>Masuk pilot lokal</h1><p><strong>SINTETIS — bukan akun atau MFA Auth0 nyata.</strong></p><p>Gunakan akun dari berkas privat ACCOUNTS-PRIVATE.txt di folder pilot. Data tetap tersimpan setelah aplikasi ditutup.</p>{{{message}}}
            <form method="post" action="/local-login"><input type="hidden" name="transaction" value="{{{id}}}"><input type="hidden" name="csrf" value="{{{csrf}}}">
            <label>Nama masuk<input name="username" autocomplete="username" required maxlength="40" autofocus></label>
            <label>Kata sandi lokal<input name="password" type="password" autocomplete="current-password" required maxlength="128"></label>
            <label>Kode faktor SIMULASI<input name="factor" type="password" inputmode="numeric" autocomplete="off" required minlength="6" maxlength="6"></label>
            <button>Masuk dengan identitas sintetis</button></form><p><a href="{{{PilotBoundary.Origin}}}/identity/login">Kembali</a></p></main></html>
            """);
    }
}
