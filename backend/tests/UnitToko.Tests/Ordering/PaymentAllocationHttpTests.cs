using System.Net;
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
using UnitToko.Tests.Documents;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentAllocationHttpTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Allocation_routes_enforce_private_request_metadata_and_return_actual_balances_and_original_result() {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture); var preview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, data.Input(0, 1000m));
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Source.Read.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Source.Read.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Source.Read.Catalog.Sessions);
            services.RemoveAll<PaymentAllocationService>(); services.AddSingleton(data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Source.Read.Catalog.Issued[data.Actor.UserId].OpaqueToken);
        var access = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current"); Assert.Contains("payment.allocate", access.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var input = new { expectedReceiptVersion = "0", lines = new[] { new { invoiceId = data.InvoiceId.ToString(), expectedVersion = "0", amount = "1000.00" } } };
        async Task<HttpResponseMessage> Send(string kind, string? fault = null) {
            var suffix = kind switch { "query" => "/query", "preview" => "/preview", "recover" => "/operations/query", _ => "" };
            object body = kind switch { "query" => new { purpose = "payment-allocation", invoiceIds = new[] { data.InvoiceId.ToString() } }, "preview" => input,
                "recover" => new { purpose = "operation-recovery", key = "http-allocation" }, _ => new { binding = preview.Binding, input, reason = "Synthetic reviewed HTTP allocation" } };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/billing/payment-verifications/{data.VerificationId}/allocations" + suffix) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", data.Actor.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "key") request.Headers.Add("Idempotency-Key", "http-allocation");
            if (fault != "revision") request.Headers.Add("If-Match", fault == "stale" ? "\"1\"" : "\"0\""); return await client.SendAsync(request);
        }
        foreach (var kind in new[] { "query", "preview", "save", "recover" }) foreach (var (fault, status) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden), ("actor", HttpStatusCode.BadRequest) }) {
            using var response = await Send(kind, fault); Assert.Equal(status, response.StatusCode);
        }
        foreach (var fault in new[] { "key", "revision", "stale" }) { using var response = await Send("save", fault); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); }
        using var shown = await Send("preview"); Assert.True(shown.IsSuccessStatusCode, await shown.Content.ReadAsStringAsync());
        Assert.Equal("3000.00", (await shown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("unapplied").GetString()); Assert.True(shown.Headers.CacheControl!.NoStore);
        using var saved = await Send("save"); Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync()); var result = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1000.00", result.GetProperty("amount").GetString());
        using var recovery = await Send("recover"); var recovered = await recovery.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("committed", recovered.GetProperty("status").GetString()); Assert.Equal(result.GetRawText(), recovered.GetProperty("result").GetRawText());
        using var balances = await Send("query"); var current = await balances.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("3000.00", current.GetProperty("available").GetString()); Assert.Equal("1000.00", current.GetProperty("invoices")[0].GetProperty("paid").GetString());
        Assert.False(current.TryGetProperty("policy", out _)); Assert.False(current.TryGetProperty("accountNumber", out _)); Assert.True(balances.Headers.CacheControl!.NoStore);
    }

    [WindowsStorageFact]
    public async Task The_verifier_without_payment_allocate_is_refused_and_nothing_is_allocated() {
        // F-007 maker/checker: the person who verified the payment (payment.verify, no payment.allocate) cannot allocate it.
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture); var preview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, data.Input(0, 1000m));
        var verifier = data.Source.Reviewer;
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Source.Read.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Source.Read.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Source.Read.Catalog.Sessions);
            services.RemoveAll<PaymentAllocationService>(); services.AddSingleton(data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Source.Read.Catalog.Issued[verifier.UserId].OpaqueToken);
        var actions = (await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current")).GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("payment.verify", actions); Assert.DoesNotContain("payment.allocate", actions);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var input = new { expectedReceiptVersion = "0", lines = new[] { new { invoiceId = data.InvoiceId.ToString(), expectedVersion = "0", amount = "1000.00" } } };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/billing/payment-verifications/{data.VerificationId}/allocations") {
            Content = JsonContent.Create(new { binding = preview.Binding, input, reason = "Synthetic allocation by the verifier" }) };
        request.Headers.Add("Origin", origin); request.Headers.Add("X-Expected-Actor-Id", verifier.UserId.ToString()); request.Headers.Add("X-CSRF-Token", csrf);
        request.Headers.Add("Idempotency-Key", "http-allocation-verifier"); request.Headers.Add("If-Match", "\"0\"");
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("\"permission_denied\"", await response.Content.ReadAsStringAsync()); // the missing grant, not a person-independence refusal
        Assert.Equal(0L, await data.Rows(PaymentAllocationBson.Events).CountDocumentsAsync(new MongoDB.Bson.BsonDocument()));
    }
}
