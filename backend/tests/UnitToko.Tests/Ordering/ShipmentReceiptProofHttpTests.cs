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
public sealed class ShipmentReceiptProofHttpTests(MongoFixture fixture)
{
    [Fact]
    public async Task Issuance_requires_current_actor_origin_csrf_and_strict_input_and_download_is_private_attachment() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var receipt = await ShipmentReceiptProofTests.Accept(data); var order = data.Order;
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + order.Prices.Catalog.Issued[order.Buyer.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        async Task<HttpResponseMessage> Request(string path, object body, string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/fulfillment/receipt-proofs" + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "different-actor" ? data.Courier.UserId.ToString() : order.Buyer.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            request.Headers.Add("Idempotency-Key", "http-proof"); return await client.SendAsync(request);
        }
        var path = "/" + receipt.ShipmentId; var body = new { receiptId = receipt.Id.ToString(), asRecipient = true, reason = "Salinan penerimaan" };
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(path, body, "actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Request(path, body, "different-actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(path, body, "origin")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(path, body, "csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(path, new { body.receiptId, body.asRecipient, body.reason, html = "unsafe" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(path, new { body.receiptId, body.reason })).StatusCode);
        var issued = await Request(path, body); var text = await issued.Content.ReadAsStringAsync(); Assert.True(issued.IsSuccessStatusCode, text);
        var document = JsonSerializer.Deserialize<JsonElement>(text); Assert.Equal(receipt.Id.ToString(), document.GetProperty("receiptId").GetString());
        Assert.Equal("1", document.GetProperty("version").GetString()); Assert.True(document.GetProperty("issuedAsRecipient").GetBoolean());
        var file = await Request(path + "/" + document.GetProperty("id").GetString() + "/download", new { purpose = "receipt-proof", asRecipient = true });
        Assert.Equal(HttpStatusCode.OK, file.StatusCode); Assert.Equal("attachment", file.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(file.Headers.CacheControl!.NoStore); Assert.Contains("sandbox", file.Headers.GetValues("Content-Security-Policy").Single());
        var bytes = await file.Content.ReadAsByteArrayAsync(); Assert.Equal(document.GetProperty("byteLength").GetString(), bytes.Length.ToString());
        Assert.Equal(document.GetProperty("contentHash").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(path + "/query", new { purpose = "receipt-proof", asRecipient = false })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/api/v1/fulfillment/receipt-proofs" + path)).StatusCode);
    }
}
