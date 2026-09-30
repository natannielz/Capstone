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
public sealed class PaymentReviewHttpTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Finance_read_routes_require_origin_csrf_actor_revision_and_return_private_bound_content() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await PaymentOcrProcessorTests.Submit(data); var reviewer = await PaymentReviewTests.Reviewer(data);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Catalog.Sessions);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Payment.Service);
            services.RemoveAll<PaymentEvidenceService>(); services.AddSingleton(data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Catalog.Issued[reviewer.UserId].OpaqueToken);
        var current = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Contains("payment.review.read", current.GetProperty("allowedActions").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain("payment.verify", current.GetProperty("allowedActions").EnumerateArray().Select(value => value.GetString()));
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        async Task<HttpResponseMessage> Send(string kind, string? fault = null) {
            var path = kind switch { "list" => "/query", "queue" => "/queue/query", "detail" => $"/{data.DeclarationId}/query", "ocr" => $"/{data.DeclarationId}/ocr/query", "duplicates" => $"/{data.DeclarationId}/duplicates/query", _ => $"/{data.DeclarationId}/evidence/{data.Reservation.ReservationId}/original/query" };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/payment-reviews" + path) {
                Content = kind is "list" or "queue" ? JsonContent.Create(new { purpose = "payment-review", beforeId = (string?)null }) : kind == "detail" ? JsonContent.Create(new { purpose = "payment-review" }) :
                    kind == "duplicates" ? JsonContent.Create(new { purpose = "payment-review", beforeId = (string?)null, reference = new { id = data.Reference.Id.ToString(), version = "1", sha256 = fault == "hash" ? new string('f',64) : data.Reference.Sha256 } }) : JsonContent.Create(new { purpose = kind == "ocr" ? "payment-ocr-view" : "payment-evidence-view", reference = new { id = data.Reference.Id.ToString(), version = "1", sha256 = fault == "hash" ? new string('f',64) : data.Reference.Sha256 } }) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "different-actor" ? data.Actor.UserId.ToString() : reviewer.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "revision") request.Headers.Add("If-Match", fault == "stale" ? "\"2\"" : "\"1\"");
            if (fault == "range") request.Headers.Add("Range", "bytes=0-10");
            return await client.SendAsync(request);
        }
        foreach (var kind in new[] { "list", "queue", "detail", "ocr", "original", "duplicates" }) {
            foreach (var (fault, status) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden), ("actor", HttpStatusCode.BadRequest), ("different-actor", HttpStatusCode.Conflict) }) {
                using var denied = await Send(kind, fault); Assert.Equal(status, denied.StatusCode);
            }
            using var response = await Send(kind); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); Assert.True(response.Headers.CacheControl!.NoStore); Assert.True(response.Headers.CacheControl.Private);
            if (kind == "original") { Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType); Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType); Assert.Equal(data.Bytes, await response.Content.ReadAsByteArrayAsync()); }
            else if (kind == "ocr") { var value = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("queued", value.GetProperty("state").GetString()); Assert.Equal(data.DeclarationId.ToString(), value.GetProperty("declarationId").GetString()); }
            else if (kind == "duplicates") { var value = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Empty(value.GetProperty("items").EnumerateArray()); Assert.Equal(data.DeclarationId.ToString(), value.GetProperty("declarationId").GetString()); Assert.False(value.GetProperty("hasMore").GetBoolean()); }
            else if (kind == "detail") { var value = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("submitted", value.GetProperty("state").GetString()); Assert.False(value.TryGetProperty("verified", out _)); }
            else if (kind == "queue") { var value = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(data.Catalog.Clock.UtcNow, value.GetProperty("observedAt").GetDateTimeOffset());
                var row = Assert.Single(value.GetProperty("items").EnumerateArray()); Assert.Equal("pending", row.GetProperty("state").GetString()); Assert.Equal("queued", row.GetProperty("ocrState").GetString()); Assert.False(row.TryGetProperty("reason", out _)); Assert.False(row.TryGetProperty("paid", out _)); }
        }
        foreach (var kind in new[] { "ocr", "original", "duplicates" })
            foreach (var (fault, status) in new[] { ("revision", HttpStatusCode.BadRequest), ("stale", HttpStatusCode.Conflict), ("hash", HttpStatusCode.Conflict) }) {
                using var denied = await Send(kind, fault); Assert.Equal(status, denied.StatusCode);
            }
        using var range = await Send("original", "range"); Assert.Equal(HttpStatusCode.BadRequest, range.StatusCode);
    }
}
