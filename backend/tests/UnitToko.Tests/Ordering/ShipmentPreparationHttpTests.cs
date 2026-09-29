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
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentPreparationHttpTests(MongoFixture fixture)
{
    [Fact]
    public async Task Protected_preparation_routes_keep_exact_packages_and_hide_commercial_and_other_store_data() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var ready = await ShipmentPreparationTests.Ready(data);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Prices.Catalog.Sessions);
            services.RemoveAll<OrderSubmissionService>(); services.AddSingleton(data.Service);
            services.RemoveAll<ShipmentPreparationService>(); services.AddSingleton(ShipmentPreparationTests.Service(data));
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Prices.Catalog.Issued[ready.Picker.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var access = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Contains(access.GetProperty("allowedActions").EnumerateArray(), action => action.GetString() == "shipment.prepare");
        Assert.DoesNotContain(access.GetProperty("allowedActions").EnumerateArray(), action => action.GetString() == "order.accept");
        async Task<HttpResponseMessage> Request(string path, object body, string? key = null, string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/fulfillment/preparations" + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "different-actor" ? data.Buyer.UserId.ToString() : ready.Picker.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (key is not null) { request.Headers.Add("Idempotency-Key", key); request.Headers.Add("If-Match", "\"0\""); }
            return await client.SendAsync(request);
        }
        static async Task<JsonElement> Ok(HttpResponseMessage response) {
            var content = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, response.StatusCode + " " + content);
            return JsonSerializer.Deserialize<JsonElement>(content);
        }
        var query = new { purpose = "shipment-preparation" };
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("/sources/query", query, fault: "actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("/sources/query", query, fault: "csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Request("/sources/query", query, fault: "different-actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("/sources/query", query, fault: "origin")).StatusCode);
        var source = (await Ok(await Request("/sources/query", query))).GetProperty("items")[0];
        Assert.Equal(ready.Source.SuborderId.ToString(), source.GetProperty("suborderId").GetString());
        Assert.Equal(12, source.GetProperty("lines")[0].GetProperty("definition").GetProperty("baseUnitsPerPackage").GetInt32());
        foreach (var privateField in new[] { "price", "tax", "charges", "total", "scope", "auditId", "createdBy" }) Assert.DoesNotContain('"' + privateField + '"', source.GetRawText());
        var path = "/" + ready.Source.SuborderId;
        Assert.Equal(JsonValueKind.Null, (await Ok(await Request(path + "/query", query))).GetProperty("preparation").ValueKind);
        var input = new { review = ready.Source.Review, lines = new[] { new { skuId = ready.Source.Lines[0].SkuId.ToString(), packages = 1 } }, reason = "Kemasan sebagian diperiksa" };
        var saved = await Ok(await Request(path, input, "prepare-http"));
        Assert.Equal("1", saved.GetProperty("revision").GetString());
        var read = (await Ok(await Request(path + "/query", query))).GetProperty("preparation");
        Assert.Equal(1, read.GetProperty("lines")[0].GetProperty("packages").GetInt32()); Assert.True(read.GetProperty("currentSourceMatches").GetBoolean());
        var recovered = await Ok(await Request("/operations/query", new { purpose = "operation-recovery", key = "prepare-http" }));
        Assert.Equal(saved.GetRawText(), recovered.GetProperty("result").GetRawText());
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(path, new { input.review, input.lines, input.reason, total = "0.00" }, "forged-total")).StatusCode);
    }
}
