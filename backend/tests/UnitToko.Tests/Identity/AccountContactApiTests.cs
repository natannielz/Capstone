using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

// The test authentication adapter selects an actor; actual authorization, CSRF,
// strict HTTP binding, transaction owners and Mongo persistence remain in use.
[Collection("Mongo")]
public sealed class AccountContactApiTests(MongoFixture fixture)
{
    private const string Origin = "https://portal.unit-toko.co.id";
    private const string Root = "/api/v1/identity/contact";
    private const string SaveBody = """{"purpose":"account-contact","name":"Kontak Tebet","phone":"+62 812 1234","position":"PIC Cabang","address":"Tebet, Jakarta","reason":"Memperbarui kontak cabang"}""";
    private const string QueryBody = """{"purpose":"account-contact"}""";
    private const string HistoryBody = """{"purpose":"account-contact","beforeHistoryId":null,"limit":20}""";
    private const string RecoveryBody = """{"purpose":"account-contact","key":"account.contact.update.original"}""";

    [Fact]
    public async Task Real_http_persists_own_contact_history_and_original_receipt_without_foreign_account_reads()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        await AccountContactTestSchema.ApplyAsync(data.Database, data.Clock);
        var owner = await data.AddActorAsync(41, new(GrantScopeKind.BuyerOwned, OrganizationId: 10, BranchId: 11), Permissions.OrderRead);
        var foreign = await data.AddActorAsync(42, new(GrantScopeKind.BuyerOwned, OrganizationId: 10, BranchId: 12), Permissions.OrderRead);
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var host = Factory(data, owner, keys);
        using var client = Client(host); var csrf = await TokenAsync(client);
        var before = await SendAsync(client, Root + "/query", QueryBody, owner.UserId, csrf);
        Assert.Equal("0", before.GetProperty("revision").GetString()); Assert.Equal(JsonValueKind.Null, before.GetProperty("name").ValueKind);
        var saved = await SendAsync(client, Root, SaveBody, owner.UserId, csrf);
        Assert.Equal(owner.UserId.ToString(CultureInfo.InvariantCulture), saved.GetProperty("userId").GetString());
        Assert.Equal("1", saved.GetProperty("revision").GetString());
        var replay = await SendAsync(client, Root, SaveBody, owner.UserId, csrf);
        Assert.Equal(saved.GetRawText(), replay.GetRawText());
        var receipt = await SendAsync(client, Root + "/operations/query", RecoveryBody, owner.UserId, csrf);
        Assert.Equal(saved.GetRawText(), receipt.GetProperty("result").GetRawText());
        var history = await SendAsync(client, Root + "/history/query", HistoryBody, owner.UserId, csrf);
        Assert.Equal(saved.GetProperty("historyId").GetString(), Assert.Single(history.GetProperty("items").EnumerateArray()).GetProperty("historyId").GetString());
        using var restarted = Factory(data, owner, keys); using var fresh = Client(restarted);
        var restored = await SendAsync(fresh, Root + "/query", QueryBody, owner.UserId, await TokenAsync(fresh));
        Assert.Equal("Kontak Tebet", restored.GetProperty("name").GetString()); Assert.Equal("1", restored.GetProperty("revision").GetString());
        using var otherHost = Factory(data, foreign, keys); using var other = Client(otherHost); var otherCsrf = await TokenAsync(other);
        var otherCurrent = await SendAsync(other, Root + "/query", QueryBody, foreign.UserId, otherCsrf);
        Assert.Equal("0", otherCurrent.GetProperty("revision").GetString());
        Assert.Empty((await SendAsync(other, Root + "/history/query", HistoryBody, foreign.UserId, otherCsrf)).GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, (await SendAsync(other, Root + "/operations/query", RecoveryBody, foreign.UserId, otherCsrf)).GetProperty("result").ValueKind);
        using var changed = Request(Root, SaveBody.Replace("Kontak Tebet", "Kontak lain", StringComparison.Ordinal), owner.UserId, csrf);
        await StatusAsync(client, changed, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Own_contact_wire_rejects_foreign_fields_and_requires_actor_origin_csrf_and_canonical_revision()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        await AccountContactTestSchema.ApplyAsync(data.Database, data.Clock);
        var actor = await data.AddActorAsync(41, new(GrantScopeKind.BuyerOwned, OrganizationId: 10, BranchId: 11), Permissions.OrderRead);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); using var host = Factory(data, actor, keys);
        using var client = Client(host); var csrf = await TokenAsync(client);
        foreach (var (route, json) in new[] { (Root, SaveBody), (Root + "/query", QueryBody),
            (Root + "/history/query", HistoryBody), (Root + "/operations/query", RecoveryBody) })
        {
            foreach (var missing in new[] { "Origin", "X-CSRF-Token", "X-Expected-Actor-Id" })
            {
                using var request = Request(route, json, actor.UserId, csrf); request.Headers.Remove(missing);
                await StatusAsync(client, request, missing == "X-Expected-Actor-Id" ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden);
            }
            foreach (var field in new[] { "userId", "verifiedEmail", "role", "privatePath" })
            {
                var body = JsonNode.Parse(json)!.AsObject(); body[field] = "forbidden";
                using var request = Request(route, body.ToJsonString(), actor.UserId, csrf);
                await StatusAsync(client, request, HttpStatusCode.BadRequest);
            }
            using var wrongActor = Request(route, json, actor.UserId + 1, csrf); await StatusAsync(client, wrongActor, HttpStatusCode.Conflict);
            using var wrongPurpose = Request(route, json.Replace("account-contact", "account-directory", StringComparison.Ordinal), actor.UserId, csrf);
            await StatusAsync(client, wrongPurpose, HttpStatusCode.BadRequest);
        }
        foreach (var version in new[] { "0", "\"00\"", "\"-1\"", "\"1.0\"", "\"257\"", "\"9223372036854775808\"" })
        {
            using var request = Request(Root, SaveBody, actor.UserId, csrf);
            request.Headers.Remove("If-Match"); request.Headers.TryAddWithoutValidation("If-Match", version);
            await StatusAsync(client, request, HttpStatusCode.BadRequest);
        }
        using var invalidText = Request(Root, SaveBody.Replace("Kontak Tebet", " Kontak Tebet ", StringComparison.Ordinal), actor.UserId, csrf);
        await StatusAsync(client, invalidText, HttpStatusCode.BadRequest);
        using var wrongOrigin = Request(Root, SaveBody, actor.UserId, csrf);
        wrongOrigin.Headers.Remove("Origin"); wrongOrigin.Headers.Add("Origin", "https://foreign.example");
        await StatusAsync(client, wrongOrigin, HttpStatusCode.Forbidden);
    }

    private static WebApplicationFactory<Program> Factory(StoreTestData data, ActorContext actor, IdentityRuntimeTests.RuntimeKeys keys)
    {
        var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Release:Profile"] = "full";
        return IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.AddAuthentication("AccessTest").AddScheme<AuthenticationSchemeOptions, AccessTestAuthenticationHandler>("AccessTest", _ => { });
            services.PostConfigure<AuthenticationOptions>(options => { options.DefaultAuthenticateScheme = "AccessTest"; options.DefaultChallengeScheme = "AccessTest"; });
            services.RemoveAll<IRequestActorResolver>(); services.AddSingleton<IRequestActorResolver>(new Resolver(actor));
        }));
    }
    private static HttpClient Client(WebApplicationFactory<Program> host) => host.CreateClient(new() { BaseAddress = new(Origin), AllowAutoRedirect = false });
    private static async Task<string> TokenAsync(HttpClient client) => (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
    private static HttpRequestMessage Request(string path, string json, long actor, string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Origin", Origin); request.Headers.Add("X-CSRF-Token", csrf);
        request.Headers.Add("X-Expected-Actor-Id", actor.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("Idempotency-Key", "account.contact.update.original"); request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        return request;
    }
    private static async Task<JsonElement> SendAsync(HttpClient client, string path, string json, long actor, string csrf)
    {
        using var request = Request(path, json, actor, csrf); using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }
    private static async Task StatusAsync(HttpClient client, HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await client.SendAsync(request); Assert.Equal(expected, response.StatusCode);
    }
    private sealed class Resolver(ActorContext actor) : IRequestActorResolver
    {
        public ValueTask<ActorContext?> ResolveAsync(HttpContext http, CancellationToken ct) => ValueTask.FromResult<ActorContext?>(actor);
    }
}
