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
public sealed class PaymentVerificationHttpTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public Task Private_verification_routes_require_origin_CSRF_actor_key_version_and_return_one_exact_recoverable_decision() => Run(false);
    [WindowsStorageFact]
    public Task Private_manual_review_routes_require_origin_CSRF_actor_key_revision_and_recover_the_exact_observation() => Run(true);
    private async Task Run(bool manual) {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture, manual ? "ocr_unavailable" : null);
        if (manual) await PaymentManualReviewTests.Policy(data);
        var view = manual ? null : await data.PreviewAsync();
        var manualView = manual ? await data.Service.PreviewManualReviewAsync(data.Reviewer, data.Read.DeclarationId) : null;
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Read.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Read.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Read.Catalog.Sessions);
            services.RemoveAll<PaymentVerificationService>(); services.AddSingleton(data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Read.Catalog.Issued[data.Reviewer.UserId].OpaqueToken);
        var access = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current"); Assert.Contains("payment.verify", access.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var input = view is null ? null : PaymentVerificationTestData.Input(view);
        async Task<HttpResponseMessage> Send(string kind, string? fault = null) {
            var suffix = kind switch { "query" => "/query", "preview" => "/preview", "recover" => "/operations/query", _ => "" };
            object body = manual ? kind switch { "query" => new { purpose = "payment-review" }, "recover" => new { purpose = "operation-recovery", key = "http-verify" },
                _ => (object)new { binding = manualView!.Binding, imageReviewed = true, fields = PaymentManualReviewTests.Fields(), reason = "Synthetic authorized original review during OCR outage" } } :
                kind switch { "query" => new { purpose = "payment-review" }, "preview" => new { receiptId = data.ReceiptId.ToString() },
                "recover" => new { purpose = "operation-recovery", key = "http-verify" }, _ => new { receiptId = data.ReceiptId.ToString(), binding = view!.Binding,
                    payerVerificationReference = input!.PayerVerificationReference, imageReviewed = true, bankReviewed = true, payerVerified = true,
                    resolutions = input.Resolutions.Select(item => new { field = item.Field, reason = item.Reason }), reason = "Synthetic independent HTTP verification" } };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/billing/payment-reviews/{data.Read.DeclarationId}/" + (manual ? "manual-review" : "verification") + suffix) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", data.Reviewer.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "key") request.Headers.Add("Idempotency-Key", "http-verify");
            if (fault != "revision") request.Headers.Add("If-Match", manual && kind == "query" ? "\"1\"" : "\"0\""); return await client.SendAsync(request);
        }
        foreach (var kind in manual ? new[] { "query", "save", "recover" } : new[] { "query", "preview", "save", "recover" }) foreach (var (fault, status) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden), ("actor", HttpStatusCode.BadRequest) }) {
            using var response = await Send(kind, fault); Assert.Equal(status, response.StatusCode);
        }
        foreach (var fault in new[] { "key", "revision" }) { using var response = await Send("save", fault); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); }
        using var preview = await Send(manual ? "query" : "preview"); Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
        var shown = await preview.Content.ReadFromJsonAsync<JsonElement>(); Assert.True(preview.Headers.CacheControl!.NoStore);
        if (manual) { Assert.Equal("0", shown.GetProperty("reviewRevision").GetString()); Assert.Equal("ocr_unavailable", shown.GetProperty("failureCode").GetString()); }
        else Assert.Equal("4000.00", shown.GetProperty("amount").GetString());
        using var saved = await Send("save"); Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync()); Assert.True(saved.Headers.CacheControl!.NoStore);
        var result = await saved.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(data.Read.DeclarationId.ToString(), result.GetProperty("declarationId").GetString());
        using var recovery = await Send("recover"); var recovered = await recovery.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("committed", recovered.GetProperty("status").GetString()); Assert.Equal(result.GetRawText(), recovered.GetProperty("result").GetRawText());
        using var query = await Send("query"); var detail = await query.Content.ReadFromJsonAsync<JsonElement>(); Assert.True(query.Headers.CacheControl!.NoStore);
        if (manual) { Assert.Equal("1", detail.GetProperty("reviewRevision").GetString()); Assert.False(detail.TryGetProperty("people", out _)); }
        else { Assert.Equal("verified", detail.GetProperty("state").GetString()); Assert.False(detail.GetProperty("verification").TryGetProperty("people", out _)); }
    }
}
