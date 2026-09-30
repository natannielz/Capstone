using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using UnitToko.Api.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentCorrectionHttpTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Correction_query_write_and_recovery_enforce_transport_and_return_lossless_private_versions() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); var actor = await PaymentCorrectionTests.Corrector(data); var view = await PaymentCorrectionTests.Prepare(data, actor);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Catalog.Sessions);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Payment.Service);
            services.RemoveAll<PaymentEvidenceService>(); services.AddSingleton(data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Catalog.Issued[actor.UserId].OpaqueToken);
        var access = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current"); Assert.Contains("payment.review.correct", access.GetProperty("allowedActions").EnumerateArray().Select(value => value.GetString()));
        using (var absentProfile = await client.GetAsync("/api/v1/identity/session")) {
            Assert.Equal(HttpStatusCode.Forbidden, absentProfile.StatusCode);
            Assert.Equal("account_profile_unavailable", (await absentProfile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
            { "displayName", "Correction reviewer fixture" }, { "verifiedEmail", "correction-reviewer@example.invalid" },
            { "verifiedAt", data.Catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Catalog.Clock.UtcNow.UtcDateTime } });
        var identity = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/session"); Assert.Contains("payment.review.correct", identity.GetProperty("stepUpOperations").EnumerateArray().Select(value => value.GetString()));
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var binding = new { declarationRevision = "1", submissionId = view.Binding.SubmissionId.ToString(), extractionId = view.Binding.ExtractionId.ToString(), evidence = new { id = data.Reference.Id.ToString(), version = "1", sha256 = data.Reference.Sha256 } };
        async Task<HttpResponseMessage> Send(string kind, string? fault = null) {
            var suffix = kind == "query" ? "/query" : kind == "recover" ? "/operations/query" : "";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/billing/payment-reviews/{data.DeclarationId}/corrections" + suffix) {
                Content = kind == "query" ? JsonContent.Create(new { purpose = "payment-ocr-view", binding }) : kind == "recover" ? JsonContent.Create(new { purpose = "operation-recovery", key = "http-correction" }) :
                    JsonContent.Create(new { binding, fields = PaymentCorrectionTests.Changed(view).Select(field => new { field = field.Field, value = field.Value }), reason = "Synthetic HTTP correction" }) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", actor.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "key") request.Headers.Add("Idempotency-Key", "http-correction");
            if (fault != "revision") request.Headers.Add("If-Match", "\"0\"");
            return await client.SendAsync(request);
        }
        foreach (var kind in new[] { "query", "save", "recover" }) foreach (var (fault, status) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden), ("actor", HttpStatusCode.BadRequest) }) {
            using var denied = await Send(kind, fault); Assert.Equal(status, denied.StatusCode);
        }
        foreach (var fault in new[] { "key", "revision" }) { using var denied = await Send("save", fault); Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode); }
        using var before = await Send("query"); Assert.True(before.IsSuccessStatusCode, await before.Content.ReadAsStringAsync()); Assert.Equal("\"0\"", before.Headers.ETag!.Tag);
        using var save = await Send("save"); Assert.True(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync()); Assert.True(save.Headers.CacheControl!.NoStore);
        var saved = await save.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("1", saved.GetProperty("reviewRevision").GetString()); Assert.Equal(data.DeclarationId.ToString(), saved.GetProperty("declarationId").GetString());
        using var recover = await Send("recover"); var receipt = await recover.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("committed", receipt.GetProperty("status").GetString()); Assert.Equal(saved.GetRawText(), receipt.GetProperty("result").GetRawText());
        using var after = await Send("query"); Assert.Equal("\"1\"", after.Headers.ETag!.Tag); Assert.True(after.Headers.CacheControl!.NoStore);
        var current = await after.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(actor.UserId.ToString(), current.GetProperty("recent")[0].GetProperty("actorId").GetString());
        Assert.Equal("100.00", current.GetProperty("originalFields")[0].GetProperty("value").GetString()); Assert.Equal("250.00", current.GetProperty("fields")[0].GetProperty("value").GetString());
    }
}
