using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnitToko.Api.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentDispatchHttpTests(MongoFixture fixture)
{
    [Fact]
    public async Task Dispatch_http_confirms_explicit_lots_scoped_courier_and_recovers_original_result_without_private_costs() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order;
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
            services.RemoveAll<ShipmentDispatchService>(); services.AddSingleton(data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + order.Prices.Catalog.Issued[data.Dispatcher.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var access = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        foreach (var action in new[] { "shipment.dispatch", "shipment.assign" })
            Assert.Contains(access.GetProperty("allowedActions").EnumerateArray(), value => value.GetString() == action);
        Assert.DoesNotContain(access.GetProperty("allowedActions").EnumerateArray(), value => value.GetString() == "shipment.prepare");
        async Task<HttpResponseMessage> Request(string path, object body, string? key = null, string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/fulfillment/dispatches" + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "different-actor" ? order.Buyer.UserId.ToString() : data.Dispatcher.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (key is not null) { request.Headers.Add("Idempotency-Key", key); request.Headers.Add("If-Match", fault == "version" ? "\"2\"" : "\"1\""); }
            return await client.SendAsync(request);
        }
        static async Task<JsonElement> Ok(HttpResponseMessage response) {
            var content = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, response.StatusCode + " " + content);
            return JsonSerializer.Deserialize<JsonElement>(content);
        }
        var query = new { purpose = "shipment-dispatch" }; var path = "/" + data.Source.SuborderId;
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("/query", query, fault: "actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("/query", query, fault: "csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Request("/query", query, fault: "different-actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("/query", query, fault: "origin")).StatusCode);
        var queue = await Ok(await Request("/query", query)); Assert.True(queue.GetProperty("items")[0].GetProperty("canDispatch").GetBoolean());
        var prepared = await Ok(await Request(path + "/query", query)); Assert.Equal("202", prepared.GetProperty("lots")[0].GetProperty("lotId").GetString());
        var couriers = await Ok(await Request(path + "/couriers/query", query));
        Assert.Equal(data.Courier.UserId.ToString(), couriers.GetProperty("items")[0].GetProperty("userId").GetString());
        var selection = new { preparationId = data.Prepared.PreparationId.ToString(), preparationRevision = "1", courierId = data.Courier.UserId.ToString(),
            lots = new[] { new { reservationId = data.Lots[0].ReservationId.ToString(), packages = 1 } } };
        var preview = await Ok(await Request(path + "/preview/query", new { query.purpose, selection }));
        foreach (var privateField in new[] { "price", "packageCost", "tax", "charges", "total", "admission", "verifiedEmail", "scope", "auditId", "createdBy" }) {
            Assert.DoesNotContain('"' + privateField + '"', preview.GetRawText()); Assert.DoesNotContain('"' + privateField + '"', couriers.GetRawText());
        }
        var command = new { selection, review = preview.GetProperty("review").GetString(), reason = "Kemasan dan lot diperiksa saat serah ke kurir" };
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(path, command, "wrong-revision", "version")).StatusCode);
        var result = await Ok(await Request(path, command, "dispatch-http"));
        Assert.Equal("1", result.GetProperty("revision").GetString()); Assert.Equal("1", result.GetProperty("number").GetString());
        Assert.Equal("100", result.GetProperty("numberEntity").GetString()); Assert.Equal("103", result.GetProperty("numberChannel").GetString());
        var replay = await Ok(await Request(path, command, "dispatch-http")); Assert.Equal(result.GetRawText(), replay.GetRawText());
        var recovered = await Ok(await Request("/operations/query", new { purpose = "operation-recovery", key = "dispatch-http" }));
        Assert.Equal(result.GetRawText(), recovered.GetProperty("result").GetRawText());
        var after = await Ok(await Request("/query", query)); Assert.False(after.GetProperty("items")[0].GetProperty("canDispatch").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(path, new { command.selection, command.review, command.reason, packageCost = "0.00" }, "forged-cost")).StatusCode);
    }
}
