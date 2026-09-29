using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnitToko.Api.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class InvoiceHttpTests(MongoFixture fixture)
{
    [Fact]
    public async Task Strict_invoice_contract_refuses_browser_amounts_wrong_actor_missing_csrf_and_downloads_private_bytes() {
        await using var data = await InvoiceData.CreateAsync(fixture); var order = data.Order;
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
            services.RemoveAll<InvoiceService>(); services.AddSingleton(data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id"; using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + order.Prices.Catalog.Issued[data.Finance.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        async Task<HttpResponseMessage> Request(string path, object body, string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/invoices" + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "different-actor" ? order.Buyer.UserId.ToString() : data.Finance.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            request.Headers.Add("If-Match", "\"0\""); request.Headers.Add("Idempotency-Key", "http-invoice"); return await client.SendAsync(request);
        }
        var receipts = new[] { new { shipmentId = data.Receipt.ShipmentId.ToString(), receiptId = data.Receipt.Id.ToString() } };
        var reviewed = await Request("/preview/query", new { purpose = "invoices", receipts }); Assert.True(reviewed.IsSuccessStatusCode, await reviewed.Content.ReadAsStringAsync());
        var review = (await reviewed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("review").GetString()!; var body = new { receipts, review, reason = "Pemeriksaan tagihan cabang" };
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("", body, "actor")).StatusCode); Assert.Equal(HttpStatusCode.Conflict, (await Request("", body, "different-actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("", body, "origin")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await Request("", body, "csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("", new { receipts, review, body.reason, total = "1.00" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("", new { receipts, body.reason })).StatusCode);
        var response = await Request("", body); var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, text);
        var invoice = JsonSerializer.Deserialize<JsonElement>(text); Assert.Equal(JsonValueKind.String, invoice.GetProperty("detail").GetProperty("total").ValueKind);
        Assert.Equal("6", invoice.GetProperty("detail").GetProperty("lines")[0].GetProperty("acceptedBaseUnits").GetString());
        Assert.DoesNotContain("orderHash", text); Assert.DoesNotContain("creditDecision", text);
        var file = await Request("/" + invoice.GetProperty("id").GetString() + "/download", new { purpose = "invoices" }); Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("attachment", file.Content.Headers.ContentDisposition!.DispositionType); Assert.True(file.Headers.CacheControl!.NoStore); Assert.Contains("sandbox", file.Headers.GetValues("Content-Security-Policy").Single());
        var bytes = await file.Content.ReadAsByteArrayAsync(); Assert.Equal(invoice.GetProperty("documentLength").GetInt32(), bytes.Length);
        Assert.Equal(invoice.GetProperty("documentHash").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }
}
