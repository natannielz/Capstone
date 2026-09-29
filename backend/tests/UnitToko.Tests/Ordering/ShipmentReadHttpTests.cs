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
public sealed class ShipmentReadHttpTests(MongoFixture fixture)
{
    [Fact]
    public async Task Courier_http_shows_only_dispatched_packages_and_requires_current_account_origin_and_csrf() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order;
        var saved = await data.DispatchAsync(await data.PreviewAsync(), "shipment-read-http");
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + order.Prices.Catalog.Issued[data.Courier.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var access = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Equal(new[] { "shipment.read" }, access.GetProperty("allowedActions").EnumerateArray().Select(value => value.GetString()));
        async Task<HttpResponseMessage> Request(string path, object body, string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/fulfillment/shipments" + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "different-actor" ? order.Buyer.UserId.ToString() : data.Courier.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            return await client.SendAsync(request);
        }
        var query = new { purpose = "shipment-read" }; var path = "/" + saved["shipmentId"].AsInt64;
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("/query", query, "actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Request("/query", query, "different-actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(path + "/query", query, "csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(path + "/query", query, "origin")).StatusCode);
        foreach (var target in new[] { "/query", path + "/query" }) {
            var response = await Request(target, query); var content = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, response.StatusCode + " " + content); Assert.True(response.Headers.CacheControl?.NoStore);
            var body = JsonSerializer.Deserialize<JsonElement>(content); var shipment = target == "/query" ? body.GetProperty("items")[0] : body;
            Assert.Equal(saved["shipmentId"].AsInt64.ToString(), shipment.GetProperty("id").GetString());
            Assert.Equal(1, shipment.GetProperty("lines")[0].GetProperty("packages").GetInt32());
            Assert.Equal("12", shipment.GetProperty("lines")[0].GetProperty("baseUnits").GetString());
            foreach (var privateField in new[] { "price", "packageCost", "total", "admission", "requestKey", "scope", "stockEffects", "auditId", "createdBy" }) Assert.DoesNotContain('"' + privateField + '"', content);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("/query", new { query.purpose, limit = 26 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(path + "/query", new { query.purpose, includeCosts = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("/99999/query", query)).StatusCode);
    }
}
