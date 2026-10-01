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
using UnitToko.Tests.Identity;
using E = UnitToko.Platform.Billing.ReturnEntitlementBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ReturnEntitlementHttpTests(MongoFixture fixture)
{
    [Fact]
    public async Task Financial_request_routes_enforce_actual_actor_csrf_origin_versions_and_private_original_key_recovery() {
        await using var data = await ReturnEntitlementTests.Data.CreateAsync(fixture); var preview = await data.PreviewAsync(); var before = await data.BusinessAsync();
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        var catalog = data.Payment.Invoice.Order.Prices.Catalog;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(catalog.Sessions);
            services.RemoveAll<ReturnEntitlementService>(); services.AddSingleton(data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + catalog.Issued[data.Actor.UserId].OpaqueToken);
        var access = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current"); Assert.Contains("credit.request", access.GetProperty("allowedActions").EnumerateArray().Select(value => value.GetString()));
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var line = data.Input.Lines[0]; var input = new { lines = new[] { new { receiptId = line.ReceiptId.ToString(), skuId = line.SkuId.ToString(), tax = "0.00", charges = "0.00" } } };
        const string key = "re.synthetic.financial-original";
        async Task<HttpResponseMessage> Send(string kind, string? fault = null) {
            var suffix = kind switch { "query" => "/query", "preview" => "/preview", "recover" => "/operations/query", _ => "" };
            var action = fault == "action" ? "refund" : data.Action;
            object body = kind switch {
                "query" => new { purpose = fault == "purpose" ? "other" : "return-entitlement", action },
                "preview" => new { action, input }, "recover" => new { purpose = "operation-recovery", action, requestKey = fault == "unknown" ? "re.synthetic.unknown" : key },
                _ => new { action, expectedVersion = "0", binding = preview.Binding, input, reason = fault == "changed-body" ? "Changed financial request" : "Synthetic immutable inspected financial request" } };
            var json = JsonSerializer.Serialize(body);
            if (fault == "extra") json = json[..^1] + ",\"approved\":true}";
            if (fault == "numeric-id") json = json.Replace("\"receiptId\":\"" + line.ReceiptId + "\"", "\"receiptId\":" + line.ReceiptId);
            if (fault == "numeric-money") json = json.Replace("\"tax\":\"0.00\"", "\"tax\":0");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/billing/cases/{data.CaseId}/returns/{data.ReturnId}/entitlement" + suffix) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", data.Actor.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "key") request.Headers.Add("Idempotency-Key", key);
            if (fault != "version") request.Headers.Add("If-Match", fault == "stale" ? "\"1\"" : "\"0\"");
            return await client.SendAsync(request);
        }
        foreach (var kind in new[] { "query", "preview", "save", "recover" }) foreach (var (fault, status) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden), ("actor", HttpStatusCode.BadRequest), ("extra", HttpStatusCode.BadRequest) }) {
            using var response = await Send(kind, fault); Assert.Equal(status, response.StatusCode);
        }
        foreach (var fault in new[] { "key", "version", "stale", "numeric-id", "numeric-money" }) { using var response = await Send("save", fault); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); }
        using (var invalid = await Send("query", "purpose")) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var mismatch = await Send("preview", "action")) Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
        using (var shown = await Send("query")) {
            Assert.True(shown.IsSuccessStatusCode, await shown.Content.ReadAsStringAsync()); var view = await shown.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("0", view.GetProperty("version").GetString()); Assert.Equal(JsonValueKind.Null, view.GetProperty("request").ValueKind); Assert.Equal("1", view.GetProperty("lines")[0].GetProperty("inspectedBaseUnits").GetString());
            Assert.True(shown.Headers.CacheControl!.NoStore);
        }
        using (var shown = await Send("preview")) {
            Assert.True(shown.IsSuccessStatusCode, await shown.Content.ReadAsStringAsync()); var value = await shown.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("1000.02", value.GetProperty("net").GetString()); Assert.Equal("1000.02", value.GetProperty("credit").GetString()); Assert.Equal(preview.Binding, value.GetProperty("binding").GetString());
            Assert.True(shown.Headers.CacheControl!.NoStore);
        }
        Assert.Equal(0, await data.Rows(E.Requests).CountDocumentsAsync(new BsonDocument()));
        using var saved = await Send("save"); Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync()); var result = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("financial_review_requested", result.GetProperty("state").GetString()); Assert.Equal(E.Operation, result.GetProperty("operation").GetString()); Assert.Equal("1000.02", result.GetProperty("net").GetString()); Assert.True(saved.Headers.CacheControl!.NoStore);
        using (var repeat = await Send("save")) { Assert.True(repeat.IsSuccessStatusCode, await repeat.Content.ReadAsStringAsync()); Assert.Equal(result.GetRawText(), (await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetRawText()); }
        using (var changed = await Send("save", "changed-body")) Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using (var recovered = await Send("recover")) {
            Assert.True(recovered.IsSuccessStatusCode, await recovered.Content.ReadAsStringAsync()); var value = await recovered.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("committed", value.GetProperty("status").GetString()); Assert.Equal(result.GetRawText(), value.GetProperty("result").GetRawText()); Assert.True(recovered.Headers.CacheControl!.NoStore);
        }
        using (var unknown = await Send("recover", "unknown")) { var value = await unknown.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("not_confirmed", value.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, value.GetProperty("result").ValueKind); }
        using (var shown = await Send("query")) {
            Assert.True(shown.IsSuccessStatusCode, await shown.Content.ReadAsStringAsync()); var value = await shown.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("1", value.GetProperty("version").GetString()); Assert.Equal("1", value.GetProperty("lines")[0].GetProperty("previouslyAttributedBaseUnits").GetString());
            var text = value.GetRawText(); foreach (var secret in new[] { "people", "participants", "actorId", "originalAccounts", "ObservedLotLabel", "PRIVAT", "snapshotHash", "synthetic-return-entitlement" }) Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(1, await data.Rows(E.Requests).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows(E.Pools).CountDocumentsAsync(new BsonDocument())); await data.AssertBusinessAsync(before);
    }
}
