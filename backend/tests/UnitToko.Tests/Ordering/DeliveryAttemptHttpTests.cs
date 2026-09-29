using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnitToko.Api.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class DeliveryAttemptHttpTests(MongoFixture fixture)
{
    [Fact]
    public async Task Courier_route_requires_csrf_actor_origin_explicit_version_and_recovers_exact_original_result() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await ShipmentReceiptTests.Ready(data); var order = data.Order;
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + order.Prices.Catalog.Issued[data.Courier.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var account = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Contains("delivery.attempt", account.GetProperty("allowedActions").EnumerateArray().Select(action => action.GetString()));
        async Task<HttpResponseMessage> Request(string path, object body, string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/fulfillment/delivery-attempts" + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "different-actor" ? order.Buyer.UserId.ToString() : data.Courier.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "key") request.Headers.Add("Idempotency-Key", "http-failed-visit");
            if (fault != "version") request.Headers.Add("If-Match", fault == "bad-version" ? "\"00\"" : "\"0\"");
            return await client.SendAsync(request);
        }
        var path = $"/{source.Shipment}"; var body = new { outcome = "recipient_unavailable", occurredAt = order.Prices.Catalog.Clock.UtcNow, reason = "Penerima belum hadir" };
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(path, body, "actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Request(path, body, "different-actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(path, body, "csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(path, body, "origin")).StatusCode);
        foreach (var fault in new[] { "version", "key", "bad-version" }) Assert.Equal(HttpStatusCode.BadRequest, (await Request(path, body, fault)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(path, new { body.outcome, body.occurredAt, body.reason, courierId = data.Courier.UserId.ToString() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(path, new { body.outcome, body.reason })).StatusCode);
        var response = await Request(path, body); var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, response.StatusCode + " " + text); Assert.True(response.Headers.CacheControl?.NoStore);
        var attempt = JsonDocument.Parse(text).RootElement; Assert.Equal("1", attempt.GetProperty("sequence").GetString()); Assert.Equal(source.Shipment.ToString(), attempt.GetProperty("shipmentId").GetString());
        Assert.Equal(text, await (await Request(path, body)).Content.ReadAsStringAsync());
        var recovered = await Request("/operations/query", new { purpose = "operation-recovery", key = "http-failed-visit" }); Assert.True(recovered.IsSuccessStatusCode);
        var receipt = await recovered.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("committed", receipt.GetProperty("status").GetString()); Assert.Equal(attempt.GetProperty("id").GetString(), receipt.GetProperty("attempt").GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/api/v1/fulfillment/delivery-attempts" + path)).StatusCode);
    }
}
