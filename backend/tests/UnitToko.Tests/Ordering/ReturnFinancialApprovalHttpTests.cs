using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Identity;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ReturnFinancialApprovalHttpTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Independent_financial_routes_enforce_real_session_origin_csrf_lossless_versions_and_exact_private_recovery() {
        await using var data = await ReturnFinancialApprovalTests.Data.CreateAsync(fixture); var preview = await data.PreviewAsync();
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        var catalog = data.Source.Payment.Invoice.Order.Prices.Catalog;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(catalog.Sessions);
            services.RemoveAll<ReturnFinancialApprovalService>(); services.AddSingleton(data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + catalog.Issued[data.Actor.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var release = data.Input.Releases[0]; var input = new { expectedInvoiceVersion = "1", releases = new[] { new { originalAllocationId = release.OriginalAllocationId.ToString(), expectedReceiptVersion = "1", amount = "1000.00" } } };
        const string key = "fa.synthetic.original";
        async Task<HttpResponseMessage> Send(string kind, string? fault = null) {
            var suffix = kind switch { "query" => "/query", "preview" => "/preview", "recover" => "/operations/query", _ => "" };
            object body = kind switch {
                "query" => new { purpose = "return-financial-approval", action = data.Source.Action, beforeAllocationId = (string?)null },
                "preview" => new { action = data.Source.Action, input },
                "recover" => new { purpose = "operation-recovery", action = data.Source.Action, requestKey = fault == "unknown" ? "fa.synthetic.unknown" : key },
                _ => new { action = data.Source.Action, binding = preview.Binding, input, reason = fault == "changed" ? "Changed original request" : "Synthetic independent financial approval; no transfer" } };
            var json = JsonSerializer.Serialize(body);
            if (fault == "extra") json = json[..^1] + ",\"paid\":true}";
            if (fault == "numeric-money") json = json.Replace("\"amount\":\"1000.00\"", "\"amount\":1000");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/billing/cases/{data.Source.CaseId}/returns/{data.Source.ReturnId}/financial-approval" + suffix) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", data.Actor.UserId.ToString());
            request.Headers.Add("Idempotency-Key", key); request.Headers.Add("If-Match", fault == "version" ? "\"2\"" : "\"1\"");
            return await client.SendAsync(request);
        }
        foreach (var route in new[] { "query", "preview", "save", "recover" }) foreach (var (fault, status) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden), ("actor", HttpStatusCode.BadRequest), ("extra", HttpStatusCode.BadRequest) }) {
            using var denied = await Send(route, fault); Assert.Equal(status, denied.StatusCode);
        }
        foreach (var fault in new[] { "version", "numeric-money" }) { using var denied = await Send("save", fault); Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode); }
        using (var response = await Send("query")) {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); var view = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("1000.02", view.GetProperty("request").GetProperty("credit").GetString()); Assert.Equal("1000.00", view.GetProperty("allocations")[0].GetProperty("available").GetString());
            Assert.Equal("1", view.GetProperty("invoice").GetProperty("version").GetString()); Assert.True(response.Headers.CacheControl!.NoStore);
            foreach (var secret in new[] { "people", "participants", "actorId", "originalAccounts", "snapshotHash", "binding", "synthetic-approved" }) Assert.DoesNotContain(secret, view.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        using (var response = await Send("preview")) { Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); var value = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(preview.Binding, value.GetProperty("binding").GetString()); Assert.Equal("1000.00", value.GetProperty("reservedRefund").GetString()); }
        Assert.Equal(0, await data.Rows(A.Events).CountDocumentsAsync(new BsonDocument()));
        using var saved = await Send("save"); Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync()); var result = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("refund_reserved", result.GetProperty("state").GetString()); Assert.Equal(A.Operation, result.GetProperty("operation").GetString()); Assert.True(saved.Headers.CacheControl!.NoStore);
        using (var repeated = await Send("save")) { Assert.True(repeated.IsSuccessStatusCode, await repeated.Content.ReadAsStringAsync()); Assert.Equal(result.GetRawText(), (await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetRawText()); }
        using (var changed = await Send("save", "changed")) Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using (var recovered = await Send("recover")) { Assert.True(recovered.IsSuccessStatusCode, await recovered.Content.ReadAsStringAsync()); var value = await recovered.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("committed", value.GetProperty("status").GetString()); Assert.Equal(result.GetRawText(), value.GetProperty("result").GetRawText()); }
        using (var unknown = await Send("recover", "unknown")) { Assert.True(unknown.IsSuccessStatusCode, await unknown.Content.ReadAsStringAsync()); Assert.Equal("not_confirmed", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()); }
        Assert.Equal(1, await data.Rows(A.Events).CountDocumentsAsync(new BsonDocument()));
    }
}
