using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnitToko.Api.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class ReceivingAccountHttpTests(MongoFixture fixture)
{
    [Fact]
    public async Task Private_lossless_routes_require_current_actor_origin_csrf_and_preconditions() {
        await using var data = await ReceivingAccountTestData.CreateAsync(fixture);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Catalog.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Catalog.Sessions);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        var actor = data.Maker;
        async Task<string> SignIn() {
            client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Catalog.Issued[actor.UserId].OpaqueToken);
            return (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        }
        var csrf = await SignIn();
        var access = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current"); Assert.Contains("receiving.account.approve", access.GetProperty("allowedActions").EnumerateArray().Select(value => value.GetString()));
        async Task<HttpResponseMessage> Send(string path, object body, string key, string version, string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/receiving-accounts/" + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", actor.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "key") request.Headers.Add("Idempotency-Key", key);
            if (fault != "version") request.Headers.Add("If-Match", "\"" + version + "\"");
            return await client.SendAsync(request);
        }
        object propose = new { accountId = (string?)null, policyId = "810", policyRevision = "1", details = ReceivingAccountTestData.Details, reason = "Synthetic HTTP proposal" };
        foreach (var fault in new[] { "origin", "actor", "csrf", "key", "version" }) {
            using var denied = await Send("proposals", propose, "http-propose", "0", fault);
            Assert.Equal(fault is "origin" or "csrf" ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest, denied.StatusCode);
        }
        using var proposed = await Send("proposals", propose, "http-propose", "0"); Assert.True(proposed.IsSuccessStatusCode, await proposed.Content.ReadAsStringAsync()); Assert.True(proposed.Headers.CacheControl!.NoStore);
        var p = await proposed.Content.ReadFromJsonAsync<JsonElement>(); var id = p.GetProperty("proposalId").GetString()!;
        actor = data.Checker; csrf = await SignIn();
        using var observation = await Send($"proposals/{id}/observations", new { details = ReceivingAccountTestData.Details, sourceReference = "synthetic-bank-record", attested = true, reason = "Synthetic checked bank channel" }, "http-observe", "1");
        Assert.True(observation.IsSuccessStatusCode, await observation.Content.ReadAsStringAsync()); var o = await observation.Content.ReadFromJsonAsync<JsonElement>();
        actor = data.Approver; csrf = await SignIn();
        using var decision = await Send($"proposals/{id}/decisions", new { decision = "approve", observationId = o.GetProperty("recordId").GetString(), reason = "Synthetic independent decision" }, "http-approve", "1");
        Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync()); var d = await decision.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("1", d.GetProperty("accountVersion").GetString());
        using var read = await Send($"proposals/{id}/query", new { purpose = "receiving-account-review" }, "unused", "1"); Assert.True(read.IsSuccessStatusCode, await read.Content.ReadAsStringAsync()); Assert.True(read.Headers.CacheControl!.NoStore);
        var view = await read.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("0000123456789", view.GetProperty("currentVersion").GetProperty("details").GetProperty("accountNumber").GetString()); Assert.DoesNotContain("personId", view.GetRawText());
        using var recovered = await Send("operations/query", new { purpose = "operation-recovery", operation = "billing.receiving-account.approve", key = "http-approve" }, "unused", "1");
        Assert.True(recovered.IsSuccessStatusCode, await recovered.Content.ReadAsStringAsync()); Assert.Equal(d.GetRawText(), (await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetRawText());
    }
}
