using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnitToko.Api.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentHttpTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Buyer_saves_private_draft_and_streams_quarantined_original_with_strict_browser_boundary() {
        await using var files = await EvidenceAdmissionTestData.CreateAsync(fixture); await using var data = await PaymentDraftData.CreateAsync(fixture); var order = data.Invoice.Order;
        var policy = EvidenceAdmissionSchemaTests.Policy(100000, 10, 3000000); policy["_id"] = (await new IdAllocator(order.Database).NextAsync("evidenceStorePolicies")).Value;
        await data.Invoice.Rows("evidenceStorePolicies").InsertOneAsync(policy);
        var evidence = new PaymentEvidenceService(order.Database, new(order.Prices.Catalog.Client), new(order.Database), order.Prices.Catalog.Clock, data.Service, files.Registration);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Service);
            services.RemoveAll<PaymentEvidenceService>(); services.AddSingleton(evidence);
        }));
        const string origin = "https://portal.unit-toko.co.id"; using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + order.Prices.Catalog.Issued[order.Buyer.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        async Task<HttpResponseMessage> Send(string path, HttpContent content, string key = "http-payment", string version = "0", string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/payment-declarations" + path) { Content = content };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "different-actor" ? data.Invoice.Finance.UserId.ToString() : order.Buyer.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            request.Headers.Add("If-Match", "\"" + version + "\""); request.Headers.Add("Idempotency-Key", key); return await client.SendAsync(request);
        }
        var input = new { invoiceIds = new[] { data.Issued.Id.ToString() }, declaredAmount = "4000.00", transferDate = data.Input.TransferDate, payerName = "Pembayar fixture", bankReference = "SYNTHETIC", note = (string?)null };
        var body = new { declarationId = (string?)null, input, reason = "Catatan pembayaran uji lokal" };
        foreach (var (fault, status) in new[] { ("actor", HttpStatusCode.BadRequest), ("different-actor", HttpStatusCode.Conflict), ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden) })
            Assert.Equal(status, (await Send("", JsonContent.Create(body), fault: fault)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("", JsonContent.Create(new { body.declarationId, body.reason, input, verified = true }))).StatusCode);
        var saved = await Send("", JsonContent.Create(body)); Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var result = await saved.Content.ReadFromJsonAsync<JsonElement>(); var id = result.GetProperty("declarationId").GetString()!;
        Assert.Equal("1", result.GetProperty("revision").GetString());
        var recovery = await Send("/operations/query", JsonContent.Create(new { purpose = "operation-recovery", key = "http-payment" }));
        Assert.Equal("committed", (await recovery.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        var read = await Send("/" + id + "/query", JsonContent.Create(new { purpose = "payment-declarations" })); Assert.True(read.Headers.CacheControl!.NoStore);
        Assert.Equal("4000.00", (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("input").GetProperty("declaredAmount").GetString());
        var reserved = await Send("/" + id + "/evidence", JsonContent.Create(new { reason = "Bukti asli uji lokal" }), "http-reserve", "1");
        Assert.True(reserved.IsSuccessStatusCode, await reserved.Content.ReadAsStringAsync()); var reservation = await reserved.Content.ReadFromJsonAsync<JsonElement>();
        var reservationId = reservation.GetProperty("reservationId").GetString(); var uploadPath = "/" + id + "/evidence/" + reservationId + "/content";
        ByteArrayContent Bytes() { var content = new ByteArrayContent(new byte[80000]); content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); return content; }
        foreach (var (fault, status) in new[] { ("actor", HttpStatusCode.BadRequest), ("different-actor", HttpStatusCode.Conflict), ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden) })
            Assert.Equal(status, (await Send(uploadPath, Bytes(), "http-bytes", "1", fault)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Send(uploadPath, JsonContent.Create(new { bytes = "invalid" }), "http-bytes", "1")).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Send("/" + id + "/query", Bytes())).StatusCode);
        var uploaded = await Send(uploadPath, Bytes(), "http-bytes", "1"); Assert.True(uploaded.IsSuccessStatusCode, await uploaded.Content.ReadAsStringAsync());
        Assert.Equal("quarantined", (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        var resumed = await Send("/" + id + "/evidence/" + reservationId + "/recover", JsonContent.Create(new { purpose = "upload-recovery", reason = "Pulihkan hasil unggah" }), "http-bytes", "1");
        Assert.Equal("quarantined", (await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(1, await data.Invoice.Count(PaymentDraftBson.Heads)); Assert.Equal(1, await data.Invoice.Count("documentVersions")); Assert.Equal(0, await data.Invoice.Count("payments")); Assert.Equal(1, await data.Invoice.Count("journalEntries"));
    }
}
