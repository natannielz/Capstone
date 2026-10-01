using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class AfterSalesCaseHttpTests(MongoFixture fixture)
{
    [Fact]
    public async Task Current_browser_authority_strict_base_units_private_projection_and_original_key_recovery_are_enforced() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var order = data.Invoice.Order; var buyer = order.Buyer;
        await order.Prices.Catalog.Base.AddGrantAsync(buyer, 99810, CatalogAuthoringTestData.Audience, [Permissions.CaseRead, Permissions.CaseRequest]);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
            services.RemoveAll<InvoiceService>(); services.AddSingleton(data.Invoice.Service);
            services.RemoveAll<AfterSalesCaseService>(); services.AddSingleton(new AfterSalesCaseService(order.Database, new(order.Prices.Catalog.Client), new(order.Database), order.Prices.Catalog.Clock, data.Invoice.Service));
        }));
        const string origin = "https://portal.unit-toko.co.id"; using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + order.Prices.Catalog.Issued[buyer.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var access = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Contains(access.GetProperty("allowedActions").EnumerateArray(), value => value.GetString() == Permissions.CaseRead);
        Assert.Contains(access.GetProperty("allowedActions").EnumerateArray(), value => value.GetString() == Permissions.CaseRequest);
        async Task<HttpResponseMessage> Request(string path, object body, string version = "1", string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/fulfillment/cases" + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "different-actor" ? data.Invoice.Finance.UserId.ToString() : buyer.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            request.Headers.Add("If-Match", "\"" + version + "\""); request.Headers.Add("Idempotency-Key", "case-http-original"); return await client.SendAsync(request);
        }
        var sourceResponse = await Request("/sales/" + data.Issued.Id + "/query", new { purpose = "after-sales-case" });
        Assert.True(sourceResponse.IsSuccessStatusCode, await sourceResponse.Content.ReadAsStringAsync()); Assert.True(sourceResponse.Headers.CacheControl!.NoStore);
        var source = await sourceResponse.Content.ReadFromJsonAsync<JsonElement>(); var line = source.GetProperty("lines")[0];
        Assert.Equal(JsonValueKind.String, line.GetProperty("acceptedBaseUnits").ValueKind); Assert.Equal(data.Issued.Detail.Lines[0].PackageLabel, line.GetProperty("packageLabel").GetString());
        var invoiceId = source.GetProperty("invoiceId").GetString()!; var binding = source.GetProperty("binding").GetString()!;
        var receiptId = line.GetProperty("receiptId").GetString()!; var skuId = line.GetProperty("skuId").GetString()!;
        var lines = new[] { new { receiptId, skuId, reportedBaseUnits = "1" } }; var body = new { invoiceId, binding, issue = "damaged", lines, reason = "Kemasan diterima dengan barang rusak" };
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("", body, "0", "actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Request("", body, "0", "different-actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("", body, "0", "origin")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("", body, "0", "csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("", body, "1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("", new { invoiceId, binding, body.issue, lines, body.reason, refundAmount = "1" }, "0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("", new { invoiceId, binding, body.issue, lines = new[] { new { receiptId, skuId, reportedBaseUnits = 1 } }, body.reason }, "0")).StatusCode);
        Assert.Equal(0, await data.Invoice.Count("afterSalesCases"));
        var committed = await Request("", body, "0"); Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync()); var result = await committed.Content.ReadFromJsonAsync<JsonElement>();
        var recovered = await Request("/operations/query", new { purpose = "operation-recovery", key = "case-http-original" }); Assert.True(recovered.IsSuccessStatusCode);
        var recovery = await recovered.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("committed", recovery.GetProperty("status").GetString()); Assert.Equal(result.GetRawText(), recovery.GetProperty("result").GetRawText());
        var shown = await Request("/" + result.GetProperty("caseId").GetString() + "/query", new { purpose = "after-sales-case" }); Assert.True(shown.IsSuccessStatusCode);
        var text = await shown.Content.ReadAsStringAsync(); var view = JsonSerializer.Deserialize<JsonElement>(text); Assert.Equal("requested", view.GetProperty("state").GetString()); Assert.Equal(body.reason, view.GetProperty("reportedProblem").GetString());
        foreach (var secret in new[] { "invoiceHash", "snapshotHash", "requestKey", "requestedBy", "grantVersion", "journal", "price", "tax", "bank", "scopes" }) Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        Assert.True((await Request("/query", new { purpose = "after-sales-case" })).IsSuccessStatusCode);
        Assert.Equal(1, await data.Invoice.Count("afterSalesCases"));
    }
}
