using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class IdentityAdministrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Search_filters_scope_before_count_and_page_and_audits_without_personal_query_text()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var inside = await data.AddActor(2201, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var outside = await data.AddActor(2202, new(GrantScopeKind.BuyerOwned, 10, 12), Permissions.OrderRead);
        await data.Admit(data.Proof() with { Subject = inside.ProviderSubject, Email = "one@example.test", DisplayName = "Shared Search" }, null);
        await data.Admit(data.Proof() with { Subject = outside.ProviderSubject, Email = "two@example.test", DisplayName = "Shared Search" }, null);

        var page = await data.Service.SearchProfilesAsync(data.Checker, "shared", 0, 1,
            new AuthorizationReadMetadata("access-review", "directory-search"));
        Assert.Equal(1, page.Total);
        Assert.Equal(inside.UserId, Assert.Single(page.Items).UserId);
        var hidden = await data.Service.SearchProfilesAsync(data.Checker, "two@example.test", 0, 20,
            new AuthorizationReadMetadata("access-review", "directory-hidden"));
        Assert.Equal(0, hidden.Total);
        Assert.Empty(hidden.Items);
        var audit = (await data.Rows("identityReadEvents").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()).ToJson();
        Assert.Contains("identity.directory.read", audit);
        Assert.DoesNotContain("two@example.test", audit);
    }

    [Fact]
    public async Task Http_directory_requires_origin_csrf_and_reviewed_actor_then_hides_outside_person()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var inside = await data.AddActor(2301, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var outside = await data.AddActor(2302, new(GrantScopeKind.BuyerOwned, 10, 12), Permissions.OrderRead);
        await data.Admit(data.Proof() with { Subject = inside.ProviderSubject, Email = "inside@example.test", DisplayName = "Inside" }, null);
        await data.Admit(data.Proof() with { Subject = outside.ProviderSubject, Email = "outside@example.test", DisplayName = "Outside" }, null);
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Store.Database.DatabaseNamespace.DatabaseName;
        var resolver = new SyntheticResolver(data.Checker);
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services =>
        {
            services.RemoveAll<UnitToko.Platform.IClock>();
            services.AddSingleton<UnitToko.Platform.IClock>(data.Store.Clock);
            services.RemoveAll<IdentityOnboardingService>();
            services.AddSingleton(data.Service);
            services.AddAuthentication("AccessTest").AddScheme<AuthenticationSchemeOptions, AccessTestAuthenticationHandler>("AccessTest", _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = "AccessTest";
                options.DefaultAuthenticateScheme = "AccessTest";
                options.DefaultChallengeScheme = "AccessTest";
            });
            services.RemoveAll<IRequestActorResolver>();
            services.AddSingleton<IRequestActorResolver>(resolver);
        }));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://portal.unit-toko.co.id"), AllowAutoRedirect = false });
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf");
        var token = csrf.GetProperty("requestToken").GetString()!;
        const string path = "/api/v1/identity/administration/directory/search";
        async Task<HttpResponseMessage> Search(string? origin, string? suppliedToken, string? actor, string term,
            string requestPath = path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, requestPath)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { purpose = "access-review", term, offset = 0, limit = 20 }), Encoding.UTF8, "application/json")
            };
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (suppliedToken is not null) request.Headers.Add("X-CSRF-Token", suppliedToken);
            if (actor is not null) request.Headers.Add("X-Expected-Actor-Id", actor);
            return await client.SendAsync(request);
        }
        var before = await data.Rows("identityReadEvents").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        Assert.Equal(HttpStatusCode.Forbidden, (await Search(null, token, data.Checker.UserId.ToString(), "inside")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Search("https://portal.unit-toko.co.id", null, data.Checker.UserId.ToString(), "inside")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Search("https://portal.unit-toko.co.id", token, null, "inside")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Search("https://portal.unit-toko.co.id", token, null, "inside",
            "/API/v1/IDENTITY/administration/directory/search")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Search("https://portal.unit-toko.co.id", token, "99", "inside")).StatusCode);
        Assert.Equal(before, await data.Rows("identityReadEvents").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var response = await Search("https://portal.unit-toko.co.id", token, data.Checker.UserId.ToString(), "outside@example.test");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("0", body.RootElement.GetProperty("total").GetString());
        Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());

        resolver.Actor = data.Maker;
        var beforeInvites = await data.Rows("accountInvitations").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        foreach (var templateId in new[] { "buyer", "finance" })
        foreach (var field in new[] { "financePacks", "additionalPermissions", "delegablePermissions" })
        foreach (var arrayEntry in new string?[] { null, " " })
        {
            var payload = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                email = "candidate@example.test", scope = new { kind = "buyingBranch", organizationId = "10", branchId = "11" },
                expiresAt = data.Store.Clock.UtcNow.AddDays(1), reason = "Review candidate",
                role = new { templateId, financePacks = Array.Empty<string>(), additionalPermissions = Array.Empty<string>(),
                    delegablePermissions = Array.Empty<string>() }
            }))!;
            payload["role"]![field] = JsonSerializer.SerializeToNode(new[] { arrayEntry });
            using var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/v1/identity/administration/invitations")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            invalid.Headers.Add("Origin", "https://portal.unit-toko.co.id");
            invalid.Headers.Add("X-CSRF-Token", token);
            invalid.Headers.Add("X-Expected-Actor-Id", data.Maker.UserId.ToString());
            invalid.Headers.Add("Idempotency-Key", "invalid-role-array");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(invalid)).StatusCode);
        }
        Assert.Equal(beforeInvites, await data.Rows("accountInvitations").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        resolver.Actor = data.Checker;

        using var schema = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        foreach (var operationPath in new[] { "/api/v1/identity/administration/directory/search",
            "/api/v1/identity/administration/invitations" })
        {
            var parameters = schema.RootElement.GetProperty("paths").GetProperty(operationPath)
                .GetProperty("post").GetProperty("parameters").EnumerateArray();
            var expectedActor = Assert.Single(parameters, parameter =>
                parameter.GetProperty("name").GetString() == "X-Expected-Actor-Id");
            Assert.True(expectedActor.GetProperty("required").GetBoolean());
        }

        var pending = await data.Service.ProposeAsync(data.Maker, data.Request(),
            new("http-review-propose", "Reviewed account scope", "identity-http"));
        using var queueRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/identity/administration/invitations/query")
        {
            Content = new StringContent("{\"purpose\":\"access-review\",\"offset\":0,\"limit\":20}", Encoding.UTF8, "application/json")
        };
        queueRequest.Headers.Add("Origin", "https://portal.unit-toko.co.id");
        queueRequest.Headers.Add("X-CSRF-Token", token);
        queueRequest.Headers.Add("X-Expected-Actor-Id", data.Checker.UserId.ToString());
        using var queue = JsonDocument.Parse(await (await client.SendAsync(queueRequest)).Content.ReadAsStringAsync());
        var review = Assert.Single(queue.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(pending.Id.ToString(), review.GetProperty("id").GetString());
        Assert.Equal("buyer", review.GetProperty("role").GetProperty("templateId").GetString());
        Assert.Contains("approve", review.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(data.Maker.UserId.ToString(), review.GetProperty("requestedBy").GetString());
        Assert.DoesNotContain("secret", review.GetRawText(), StringComparison.OrdinalIgnoreCase);

        async Task<HttpResponseMessage> Approve(string reason, string key, string version)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/identity/administration/invitations/{pending.Id}/approve")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { reason }), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Origin", "https://portal.unit-toko.co.id");
            request.Headers.Add("X-CSRF-Token", token);
            request.Headers.Add("X-Expected-Actor-Id", data.Checker.UserId.ToString());
            request.Headers.Add("Idempotency-Key", key);
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
            return await client.SendAsync(request);
        }
        var approved = await Approve("Independent review", "http-approve", "1");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var original = await approved.Content.ReadAsStringAsync();
        Assert.Contains("approved", original);
        Assert.DoesNotContain("secret", original, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await (await Approve("Independent review", "http-approve", "1")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await Approve("Changed review", "http-approve", "1")).StatusCode);
        Assert.Equal("blocked", (await data.Rows("identityProviderIntents")
            .Find(new BsonDocument("invitationId", pending.Id)).SingleAsync())["status"].AsString);
    }

    private sealed class SyntheticResolver(UnitToko.Modules.Identity.Contracts.ActorContext actor) : IRequestActorResolver
    {
        public UnitToko.Modules.Identity.Contracts.ActorContext Actor { get; set; } = actor;
        public ValueTask<UnitToko.Modules.Identity.Contracts.ActorContext?> ResolveAsync(Microsoft.AspNetCore.Http.HttpContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<UnitToko.Modules.Identity.Contracts.ActorContext?>(Actor);
    }
}
