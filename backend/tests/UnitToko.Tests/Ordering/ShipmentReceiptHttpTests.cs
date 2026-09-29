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
public sealed class ShipmentReceiptHttpTests(MongoFixture fixture)
{
    [Fact]
    public async Task Receipt_requires_intended_account_csrf_origin_explicit_consent_and_strict_quantities() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var source = await ShipmentReceiptTests.Ready(data); var order = data.Order;
        var challenge = await ShipmentReceiptTests.Service(data).IssueChallengeAsync(data.Courier, source.Shipment, source.Document, order.Buyer.UserId, CatalogPriceTestData.Meta("http-qr"));
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + order.Prices.Catalog.Issued[order.Buyer.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var account = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Contains("receipt.accept", account.GetProperty("allowedActions").EnumerateArray().Select(action => action.GetString()));
        Assert.DoesNotContain("delivery.evidence", account.GetProperty("allowedActions").EnumerateArray().Select(action => action.GetString()));
        async Task<HttpResponseMessage> Request(string path, object body, string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/fulfillment/receipts" + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "different-actor" ? data.Courier.UserId.ToString() : order.Buyer.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            request.Headers.Add("Idempotency-Key", "http-confirm"); return await client.SendAsync(request);
        }
        var path = $"/challenges/{challenge.Id}/query"; var preview = new { token = challenge.Token };
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(path, preview, "actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Request(path, preview, "different-actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(path, preview, "csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(path, preview, "origin")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(path, new { preview.token, recipientId = order.Buyer.UserId.ToString() })).StatusCode);
        var response = await Request(path, preview); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(source.Document.ToString(), document.GetProperty("documentId").GetString());
        var body = new { token = challenge.Token, consentVersion = "delivery-receipt-v1", consent = false, reason = "Hitungan sesuai barang", lines = new[] {
            new { skuId = data.Source.Lines[0].SkuId.ToString(), accepted = new { packages = 1, looseUnits = 0 }, rejected = new { packages = 0, looseUnits = 0 }, missing = new { packages = 0, looseUnits = 0 }, exceptionReason = (string?)null }
        } };
        Assert.Equal(HttpStatusCode.BadRequest, (await Request($"/challenges/{challenge.Id}/confirm", body)).StatusCode);
        var accepted = await Request($"/challenges/{challenge.Id}/confirm", new { body.token, body.consentVersion, consent = true, body.reason, body.lines });
        var acceptedText = await accepted.Content.ReadAsStringAsync(); Assert.True(accepted.IsSuccessStatusCode, accepted.StatusCode + " " + acceptedText);
        Assert.DoesNotContain(challenge.Token!, acceptedText); Assert.True(accepted.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/api/v1/fulfillment/receipts" + path)).StatusCode);
    }
}
