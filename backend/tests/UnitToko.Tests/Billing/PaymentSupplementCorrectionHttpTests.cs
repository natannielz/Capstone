using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
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

namespace UnitToko.Tests.Billing;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentSupplementCorrectionHttpTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Actual_HTTP_retains_image_binding_and_explicit_PDF_provenance_with_private_transport_and_exact_recovery()
    {
        await using var data = await PaymentSupplementCorrectionTestData.CreateAsync(fixture);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Data.Database.DatabaseNamespace.DatabaseName; settings["Release:Profile"] = "full";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Data.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Data.Catalog.Sessions);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Data.Payment.Service);
            services.RemoveAll<PaymentEvidenceService>(); services.AddSingleton(data.Data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Data.Catalog.Issued[data.Reviewer.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var binding = new { declarationRevision = "1", submissionId = data.Review.Binding.SubmissionId.ToString(), extractionId = data.Review.Binding.ExtractionId.ToString(),
            evidence = new { id = data.Data.Reference.Id.ToString(), version = "1", sha256 = data.Data.Reference.Sha256 } };
        object Observation(int page = 1) => new { field = "amount", supplementId = data.Pdf.Evidence.Supplement.SupplementId.ToString(), supplementRevision = "2",
            candidate = new { resultId = data.PdfResult["_id"].AsInt64.ToString(), resultHash = data.PdfResult["resultHash"].AsString,
                evidence = new { id = data.Pdf.Reference.Id.ToString(), version = "1", sha256 = data.Pdf.Reference.Sha256 }, pageIndex = page, field = "amount" }, originalReviewed = true };
        var fields = data.Fields.Select(field => new { field = field.Field, value = field.Value }).ToArray();
        var route = $"/api/v1/billing/payment-reviews/{data.Data.DeclarationId}/corrections";
        async Task<HttpResponseMessage> Send(string kind = "save", string? fault = null, int page = 1)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route + (kind == "query" ? "/query" : kind == "recover" ? "/operations/query" : ""));
            var reason = "Koreksi manual dengan rujukan PDF terpisah";
            request.Content = kind == "query" ? JsonContent.Create(new { purpose = "payment-ocr-view", binding }) : kind == "recover"
                ? JsonContent.Create(new { purpose = "operation-recovery", key = "http-supplement-reference" })
                : fault == "absent" ? JsonContent.Create(new { binding,
                    fields = fields.Select(field => field.field == "amount" ? new { field = field.field, value = (string?)"5000.00" } : field).ToArray(),
                    reason = "Koreksi foto tanpa anotasi tambahan" })
                : fault == "null" ? JsonContent.Create(new { binding, fields, reason, supplementaryObservations = (object?)null })
                : fault == "empty" ? JsonContent.Create(new { binding, fields, reason, supplementaryObservations = Array.Empty<object>() })
                : fault == "presence" ? JsonContent.Create(new { binding, fields, reason, supplementaryObservationsPresent = true })
                : JsonContent.Create(new { binding, fields, reason, supplementaryObservations = new[] { Observation(page) } });
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", data.Reviewer.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "key") request.Headers.Add("Idempotency-Key", fault == "absent" ? "http-image-only-reference" : "http-supplement-reference");
            if (fault != "revision") request.Headers.Add("If-Match", fault == "absent" ? "\"1\"" : "\"0\"");
            return await client.SendAsync(request);
        }
        foreach (var (fault, expected) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden),
            ("actor", HttpStatusCode.BadRequest), ("key", HttpStatusCode.BadRequest), ("revision", HttpStatusCode.BadRequest),
            ("null", HttpStatusCode.BadRequest), ("empty", HttpStatusCode.BadRequest), ("presence", HttpStatusCode.BadRequest) }) {
            using var denied = await Send(fault: fault); Assert.Equal(expected, denied.StatusCode);
        }
        Assert.False(await data.Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument()).AnyAsync());
        using var savedReply = await Send(); Assert.Equal(HttpStatusCode.OK, savedReply.StatusCode); Assert.True(savedReply.Headers.CacheControl!.NoStore);
        var saved = await savedReply.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("1", saved.GetProperty("reviewRevision").GetString());
        using var repeat = await Send(); Assert.Equal(HttpStatusCode.OK, repeat.StatusCode); Assert.Equal(saved.GetRawText(), (await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetRawText());
        using var changed = await Send(page: 0); Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("idempotency_key_reused", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var recovery = await Send("recover"); Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
        var receipt = await recovery.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("committed", receipt.GetProperty("status").GetString());
        Assert.Equal(saved.GetRawText(), receipt.GetProperty("result").GetRawText());
        using var query = await Send("query"); Assert.Equal(HttpStatusCode.OK, query.StatusCode); Assert.True(query.Headers.CacheControl!.NoStore);
        var view = await query.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("1", view.GetProperty("reviewRevision").GetString());
        var persisted = view.GetProperty("recent")[0].GetProperty("supplementaryObservations")[0];
        Assert.Equal(data.PdfResult["resultHash"].AsString, persisted.GetProperty("candidate").GetProperty("resultHash").GetString());
        Assert.Equal(1, persisted.GetProperty("candidate").GetProperty("pageIndex").GetInt32()); Assert.True(persisted.GetProperty("originalReviewed").GetBoolean());
        Assert.Equal(data.Data.Reference.Sha256, view.GetProperty("binding").GetProperty("evidence").GetProperty("sha256").GetString());
        Assert.Equal(1L, await data.Rows(PaymentCorrectionBson.Versions).CountDocumentsAsync(new BsonDocument()));
        using var legacy = await Send(fault: "absent"); Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Equal("2", (await legacy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reviewRevision").GetString());
        var oldVersion = await data.Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument("revision", 2L)).SingleAsync();
        Assert.Equal(1, oldVersion["schemaVersion"].AsInt32); Assert.False(oldVersion.Contains("supplementaryObservations"));
        var oldFields = PaymentCorrectionBson.Fields(oldVersion["fields"].AsBsonArray);
        var oldBody = PaymentCorrectionBson.Body(data.Data.DeclarationId, 1, data.Review.Binding, oldFields);
        Assert.Equal(UnitToko.Platform.Authorization.AuthorizationStore.CommandHash(oldBody, "Koreksi foto tanpa anotasi tambahan"), oldVersion["bodyHash"]);
        Assert.Equal(2L, await data.Rows(PaymentCorrectionBson.Versions).CountDocumentsAsync(new BsonDocument())); await data.UnchangedAsync();
    }
}
