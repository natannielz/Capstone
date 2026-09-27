using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class ProviderSecurityEndpointTests(MongoFixture fixture)
{
    private const string EventsPath = "/api/v1/identity/provider-events/auth0-events";
    private const string LogsPath = "/api/v1/identity/provider-events/auth0-logs";
    private static readonly string EventsToken = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-events-only-fixture")));
    private static readonly string LogsToken = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-logs-only-fixture")));

    [Fact]
    public async Task Machine_credentials_precede_parsing_and_never_replace_browser_security()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        data.Clock.UtcNow = DateTimeOffset.UtcNow;
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var factory = IdentityRuntimeTests.Factory(Settings(keys, data.Database.DatabaseNamespace.DatabaseName))
            .WithWebHostBuilder(web => web.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            }));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://portal.unit-toko.co.id"), HandleCookies = false });
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, EventsPath, "{", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, EventsPath, "{", "Bearer wrong-fixture")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, EventsPath, Event(data.Clock.UtcNow), "Bearer " + LogsToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, EventsPath, "{", "Bearer " + EventsToken)).StatusCode);

        var accepted = await Send(client, EventsPath.ToUpperInvariant(), Event(data.Clock.UtcNow), "Bearer " + EventsToken);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.True(accepted.Headers.CacheControl?.NoStore);
        Assert.False(accepted.Headers.Contains("Set-Cookie"));
        Assert.Equal("", await accepted.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, EventsPath, Event(data.Clock.UtcNow), "Bearer " + EventsToken)).StatusCode);
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("identitySecurityEvents").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var saved = await data.Database.GetCollection<BsonDocument>("identitySecurityEvents").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal("unknown", saved["outcome"]);
        Assert.DoesNotContain("unknown-provider-user", saved.ToJson());
        Assert.DoesNotContain(EventsToken, saved.ToJson());

        // Even the correct machine credential cannot bypass browser origin/CSRF on another route.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "/api/v1/identity/administration/invitations/query", "{}", "Bearer " + EventsToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, EventsPath + "/other", "{}", "Bearer " + EventsToken)).StatusCode);
        var browser = new HttpRequestMessage(HttpMethod.Post, "/api/v1/identity/administration/invitations/query")
        {
            Content = new StringContent("{\"purpose\":\"access-review\"}", Encoding.UTF8, "application/json")
        };
        browser.Headers.Add("Origin", "https://portal.unit-toko.co.id");
        browser.Headers.Add("X-CSRF-Token", csrf.GetProperty("requestToken").GetString());
        browser.Headers.Authorization = new("Bearer", EventsToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(browser)).StatusCode);

        using var openapi = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var operation = openapi.RootElement.GetProperty("paths").GetProperty(EventsPath).GetProperty("post");
        if (operation.TryGetProperty("parameters", out var parameters))
            Assert.DoesNotContain(parameters.EnumerateArray(), item => item.GetProperty("name").GetString() == "X-CSRF-Token");
        Assert.Contains(operation.GetProperty("security").EnumerateArray(), item => item.TryGetProperty("providerEvents", out _));
    }

    [Fact]
    public async Task Separate_log_transport_and_trusted_https_are_required()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        data.Clock.UtcNow = DateTimeOffset.UtcNow;
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var factory = IdentityRuntimeTests.Factory(Settings(keys, data.Database.DatabaseNamespace.DatabaseName))
            .WithWebHostBuilder(web => web.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            }));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://portal.unit-toko.co.id"), HandleCookies = false });
        var logs = JsonSerializer.Serialize(new[] { new { log_id = "90000000000000000000000000000000000000000000000000000001",
            data = new { type = "scp", date = data.Clock.UtcNow, user_id = "auth0|unknown-provider-user" } } });
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, LogsPath, logs, "Bearer " + EventsToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, LogsPath, logs, "Bearer " + LogsToken)).StatusCode);
        foreach (var origin in new[] { "http://portal.unit-toko.co.id", "https://other.unit-toko.co.id" })
        {
            using var wrongOrigin = factory.CreateClient(new() { BaseAddress = new(origin), HandleCookies = false });
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(wrongOrigin, EventsPath, Event(data.Clock.UtcNow), "Bearer " + EventsToken)).StatusCode);
        }
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("identitySecurityEvents").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public void Enabled_event_issuer_must_match_the_configured_identity_provider()
    {
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        var settings = Settings(keys);
        settings["Identity:Auth0SecurityEvents:Issuer"] = "https://other.unit-toko.co.id/";
        using var factory = IdentityRuntimeTests.Factory(settings);
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Disabled_transport_cannot_be_enabled_by_a_cookie_or_header()
    {
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var factory = IdentityRuntimeTests.Factory(IdentityRuntimeTests.Settings(keys));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://portal.unit-toko.co.id"), HandleCookies = false });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(client, EventsPath, "{}", "Bearer " + EventsToken)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(client, LogsPath, "[]", "Bearer " + LogsToken)).StatusCode);
    }

    [Fact]
    public async Task Provider_callback_does_not_read_or_refresh_a_supplied_browser_session()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        data.Clock.UtcNow = DateTimeOffset.UtcNow;
        var actor = await data.AddActorAsync(3101, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        const string issuer = "https://login.unit-toko.co.id/";
        await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
            Builders<BsonDocument>.Update.Set("issuer", issuer).Set("securityVersion", 1L));
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var factory = IdentityRuntimeTests.Factory(Settings(keys, data.Database.DatabaseNamespace.DatabaseName))
            .WithWebHostBuilder(web => web.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            }));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://portal.unit-toko.co.id"), HandleCookies = false });
        var sessions = factory.Services.GetRequiredService<MongoIdentitySessionStore>();
        var proof = new ProviderIdentityProof(issuer, actor.ProviderSubject, "synthetic@example.test", true, "con_synthetic",
            data.Clock.UtcNow, data.Clock.UtcNow, "otp", "unit-toko-otp-v1");
        var issued = await new MongoUnitOfWork(fixture.Client).ExecuteAsync((tx, ct) =>
            sessions.CreateAsync(tx, new(actor.UserId, 1L, actor.GrantVersion, issuer, actor.ProviderSubject), proof, "synthetic-private-payload", ct));
        var rows = data.Database.GetCollection<BsonDocument>("identitySessions");
        var before = await rows.Find(new BsonDocument("_id", issued.Session.Reference.Id)).SingleAsync();
        data.Clock.UtcNow += TimeSpan.FromSeconds(5);
        client.DefaultRequestHeaders.Add("Cookie", "__Host-ut_session=" + issued.OpaqueToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, EventsPath, Event(data.Clock.UtcNow), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, EventsPath, Event(data.Clock.UtcNow), "Bearer " + EventsToken)).StatusCode);
        Assert.Equal(before, await rows.Find(new BsonDocument("_id", issued.Session.Reference.Id)).SingleAsync());
    }
    [Fact]
    public async Task Machine_transport_limits_media_and_ambiguous_headers_without_browser_exemption_leaks()
    {
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var factory = IdentityRuntimeTests.Factory(Settings(keys));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://portal.unit-toko.co.id"), HandleCookies = false });
        using var ambiguous = new HttpRequestMessage(HttpMethod.Post, EventsPath)
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        };
        ambiguous.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + EventsToken, "Bearer " + LogsToken });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(ambiguous)).StatusCode);
        using var wrongMedia = new HttpRequestMessage(HttpMethod.Post, EventsPath)
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain")
        };
        wrongMedia.Headers.Authorization = new("Bearer", EventsToken);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.SendAsync(wrongMedia)).StatusCode);
        foreach (var content in new HttpContent[] { new StringContent(new string('x', 65_537)), new ChunkedContent(65_537) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, EventsPath) { Content = content };
            request.Headers.Authorization = new("Bearer", EventsToken);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        }
        using var wrongMethod = new HttpRequestMessage(HttpMethod.Delete, EventsPath);
        wrongMethod.Headers.Authorization = new("Bearer", EventsToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(wrongMethod)).StatusCode);
    }
    private static Dictionary<string, string?> Settings(IdentityRuntimeTests.RuntimeKeys keys, string? database = null)
    {
        var values = IdentityRuntimeTests.Settings(keys);
        if (database is not null) values["Mongo:Database"] = database;
        foreach (var pair in new Dictionary<string, string?>
        {
            ["EventsEnabled"] = "true", ["LogsEnabled"] = "true", ["Issuer"] = values["Identity:Auth0:Issuer"],
            ["EventSource"] = "urn:auth0:login.unit-toko.co.id", ["TenantId"] = "unit-toko-fixture",
            ["EventStreamId"] = "est_abcdefghijklmnopqrstuv", ["EventsBearerToken"] = EventsToken, ["LogsBearerToken"] = LogsToken
        }) values["Identity:Auth0SecurityEvents:" + pair.Key] = pair.Value;
        return values;
    }

    private static string Event(DateTimeOffset at) => JsonSerializer.Serialize(new
    {
        specversion = "1.0", type = "user.updated", source = "urn:auth0:login.unit-toko.co.id",
        id = "evt_abcdefghijklmnopqrstuv", time = at, a0tenant = "unit-toko-fixture", a0stream = "est_abcdefghijklmnopqrstuv",
        data = new { @object = new { user_id = "auth0|unknown-provider-user", blocked = true } }
    });
    private static Task<HttpResponseMessage> Send(HttpClient client, string path, string payload, string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return client.SendAsync(request);
    }
}
