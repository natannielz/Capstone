using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnitToko.Api.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentDocumentHttpTests(MongoFixture fixture)
{
    [Fact]
    public async Task Download_is_private_no_store_actor_bound_and_only_returns_fixed_bytes() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order;
        var shipmentId = (await data.DispatchAsync(await data.PreviewAsync(), "document-http-dispatch"))["shipmentId"].AsInt64;
        var doc = await ShipmentDocumentTests.Service(data).IssueAsync(data.Picker, shipmentId, 1, CatalogPriceTestData.Meta("document-http-issue"));
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + order.Prices.Catalog.Issued[data.Courier.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        async Task<HttpResponseMessage> Request(string path, object body, string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/fulfillment/shipment-documents" + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "different-actor" ? order.Buyer.UserId.ToString() : data.Courier.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            request.Headers.Add("Idempotency-Key", "http-no-authority"); request.Headers.Add("If-Match", "\"1\"");
            return await client.SendAsync(request);
        }
        var purpose = new { purpose = "shipment-document" }; var download = $"/{shipmentId}/{doc.Id}/download";
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(download, purpose, "actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Request(download, purpose, "different-actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(download, purpose, "csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(download, purpose, "origin")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request($"/{shipmentId}", new { reason = "Courier cannot issue" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(download, new { purpose.purpose, publicUrl = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request($"/{shipmentId}/99999/download", purpose)).StatusCode);
        var response = await Request(download, purpose); var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, response.StatusCode + " " + content); Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("default-src 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(doc.ContentHash, UnitToko.Platform.Documents.IssuedDocumentStore.Hash(content));
        Assert.Equal(doc.ByteLength, (await response.Content.ReadAsByteArrayAsync()).LongLength);
        Assert.Equal(content, await (await Request(download, purpose)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/api/v1/fulfillment/shipment-documents" + download)).StatusCode);
    }
}
