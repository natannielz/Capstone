using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class IdentityFullHostTests(MongoFixture fixture)
{
    [Fact]
    public async Task Real_host_login_callback_actor_antiforgery_stepup_and_logout_share_one_session_boundary()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        var actor = await data.AddActorAsync(9007199254740993, new(GrantScopeKind.System), Permissions.RecoveryInitiate);
        await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
            Builders<BsonDocument>.Update.Set("issuer", configured.Issuer).Set("subject", "auth0|fixture-user"));
        using var provider = new ProviderHandlerTests.FakeProvider(configured, data.Clock);
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.Configure<OpenIdConnectOptions>(Auth0Options.Scheme, options => options.Backchannel = new HttpClient(provider, disposeHandler: false));
        }));
        using var client = factory.CreateClient(new() { BaseAddress = new(configured.PublicOrigin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", configured.PublicOrigin);
        async Task<string> Csrf()
        {
            var response = await client.GetAsync("/api/v1/identity/csrf"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("requestToken").GetString()!;
        }
        async Task<HttpResponseMessage> Form(string path, string token, params KeyValuePair<string, string>[] fields) =>
            await client.PostAsync(path, new FormUrlEncodedContent([new("__RequestVerificationToken", token), .. fields]));
        async Task<HttpResponseMessage> Callback(HttpResponseMessage challenge)
        {
            Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
            provider.Challenge = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query).ToDictionary(item => item.Key, item => item.Value.ToString());
            return await client.GetAsync("/auth/callback?code=fixture-code&state=" + Uri.EscapeDataString(provider.Challenge["state"]));
        }
        var anonymousToken = await Csrf();
        var challenge = await Form("/auth/login", anonymousToken, new KeyValuePair<string, string>("returnPath", "/identity/account"));
        var callback = await Callback(challenge);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/identity/account", callback.Headers.Location?.OriginalString);
        var firstCookie = Assert.Single(callback.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(OpaqueSessionCookie.Name + "=", StringComparison.Ordinal));
        Assert.True(provider.SawValidPkce);
        var current = await client.GetAsync("/api/v1/identity/session");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using var profile = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        Assert.True(profile.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal("9007199254740993", profile.RootElement.GetProperty("account").GetProperty("userId").GetString());
        var deadline = profile.RootElement.GetProperty("absoluteExpiresAt").GetDateTimeOffset();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/access/current")).StatusCode);
        // Anonymous anti-forgery evidence cannot be reused after becoming an authenticated account.
        Assert.Equal(HttpStatusCode.Forbidden, (await Form("/auth/logout", anonymousToken, new KeyValuePair<string, string>("expectedActorId", actor.UserId.ToString()))).StatusCode);
        data.Clock.UtcNow += TimeSpan.FromMinutes(6);
        var stepup = await Form("/auth/step-up", await Csrf(), new KeyValuePair<string, string>("expectedActorId", actor.UserId.ToString()), new KeyValuePair<string, string>("operation", Permissions.RecoveryInitiate), new KeyValuePair<string, string>("returnPath", "/identity/account"));
        var rotated = await Callback(stepup);
        Assert.Equal(HttpStatusCode.Redirect, rotated.StatusCode);
        var secondCookie = Assert.Single(rotated.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(OpaqueSessionCookie.Name + "=", StringComparison.Ordinal));
        Assert.NotEqual(firstCookie, secondCookie);
        using var secondProfile = JsonDocument.Parse(await (await client.GetAsync("/api/v1/identity/session")).Content.ReadAsStringAsync());
        Assert.Equal(deadline, secondProfile.RootElement.GetProperty("absoluteExpiresAt").GetDateTimeOffset());
        using var stolenOld = factory.CreateClient(new() { BaseAddress = new(configured.PublicOrigin), AllowAutoRedirect = false, HandleCookies = false });
        stolenOld.DefaultRequestHeaders.Add("Cookie", firstCookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stolenOld.GetAsync("/api/v1/access/current")).StatusCode);
        var sessions = data.Database.GetCollection<BsonDocument>("identitySessions");
        using (var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/auth/logout"))
        {
            crossOrigin.Headers.Add("Origin", "https://outside.invalid");
            crossOrigin.Content = new FormUrlEncodedContent([
                new("__RequestVerificationToken", await Csrf()), new("expectedActorId", actor.UserId.ToString())]);
            var denied = await client.SendAsync(crossOrigin);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.False(denied.Headers.Contains("Set-Cookie"));
            Assert.Equal(0, await sessions.CountDocumentsAsync(new BsonDocument("status", "revoked")));
        }
        using var definitions = await data.Database.ListCollectionsAsync(new ListCollectionsOptions
        {
            Filter = new BsonDocument("name", "identitySessions")
        });
        var originalValidator = (await definitions.SingleAsync())["options"]["validator"].AsBsonDocument.DeepClone();
        // Fail the real revocation write in this isolated database, while permitting
        // authentication's active-session fence. No global Mongo failpoint is used.
        var denyRevocation = new BsonDocument("$and", new BsonArray
        {
            originalValidator, new BsonDocument("revocationReason", new BsonDocument("$ne", "owner_logout"))
        });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", "identitySessions" }, { "validator", denyRevocation }
        });
        try
        {
            var failed = await Form("/auth/logout", await Csrf(), new KeyValuePair<string, string>("expectedActorId", actor.UserId.ToString()));
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.False(failed.Headers.Contains("Set-Cookie"));
            using var failure = JsonDocument.Parse(await failed.Content.ReadAsStringAsync());
            Assert.Equal("internal_error", failure.RootElement.GetProperty("code").GetString());
            Assert.Equal(0, await sessions.CountDocumentsAsync(new BsonDocument("status", "revoked")));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/access/current")).StatusCode);
        }
        finally
        {
            await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "collMod", "identitySessions" }, { "validator", originalValidator }
            });
        }
        var logout = await Form("/auth/logout", await Csrf(), new KeyValuePair<string, string>("expectedActorId", actor.UserId.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        var clearedCookie = Assert.Single(logout.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(OpaqueSessionCookie.Name + "=;", StringComparison.Ordinal));
        Assert.Contains("secure", clearedCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", clearedCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/access/current")).StatusCode);
        using var stolenNew = factory.CreateClient(new() { BaseAddress = new(configured.PublicOrigin), AllowAutoRedirect = false, HandleCookies = false });
        stolenNew.DefaultRequestHeaders.Add("Cookie", secondCookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stolenNew.GetAsync("/api/v1/access/current")).StatusCode);
    }
}
