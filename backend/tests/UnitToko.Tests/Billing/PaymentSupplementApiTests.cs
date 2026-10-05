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
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Billing;

// Concrete real-Mongo/issued-session HTTP tests. Draft only, uncompiled and unexecuted.
// No attached original/OCR result is fabricated; those facts require the coordinated native producer.
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentSupplementApiTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Full_profile_additive_create_reserve_recovery_requires_actor_origin_CSRF_exact_owner_and_preserves_primary_finances()
    {
        await using var source = await PaymentSupplementEvidenceTestData.CreateAsync(fixture); var data = source.Data;
        var before = await source.FinancialAsync(); using var keys = new IdentityRuntimeTests.RuntimeKeys();
        var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Release:Profile"] = "full";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Catalog.Sessions);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Payment.Service);
            services.RemoveAll<PaymentEvidenceService>(); services.AddSingleton(data.Service);
            services.RemoveAll<PaymentSupplementEvidenceService>(); services.AddSingleton(source.Facade);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() {BaseAddress = new(origin), AllowAutoRedirect = false});
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Catalog.Issued[data.Actor.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var route = $"/api/v1/billing/payment-declarations/{source.Primary.DeclarationId}/supplements";
        var parentRevision = source.Primary.DeclarationRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var image = new {id = source.Primary.PrimaryEvidence.Id.ToString(), version = "1", sha256 = source.Primary.PrimaryEvidence.Sha256};
        var create = new {submissionId = source.Primary.SubmissionId.ToString(), primaryEvidence = image, reason = "Lampiran PDF tambahan; gambar utama tetap wajib"};
        var createKey = $"ps.c.{data.Actor.UserId}.{source.Primary.DeclarationId}.{source.Primary.SubmissionId}.{Guid.NewGuid():D}";
        async Task<HttpResponseMessage> Post(string path, object body, string revision, string? key = null, string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) {Content = JsonContent.Create(body)};
            request.Headers.Add("Origin", fault == "origin" ? "https://foreign.invalid" : origin);
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", data.Actor.UserId.ToString());
            if (fault != "revision") request.Headers.Add("If-Match", '"' + revision + '"');
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(request);
        }
        foreach (var (fault, expected) in new[] {("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden),
            ("actor", HttpStatusCode.BadRequest), ("revision", HttpStatusCode.BadRequest)}) {
            using var denied = await Post(route, create, parentRevision, createKey, fault); Assert.Equal(expected, denied.StatusCode);
        }
        using var stale = await Post(route, create, (source.Primary.DeclarationRevision + 1).ToString(), createKey); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var unknown = await Post(route, new {create.submissionId, create.primaryEvidence, create.reason, policyApproval = "forbidden"}, parentRevision, createKey);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var created = await Post(route, create, parentRevision, createKey); Assert.True(created.IsSuccessStatusCode);
        var saved = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result"); var supplementId = saved.GetProperty("supplementId").GetString()!;
        Assert.Equal("draft", saved.GetProperty("state").GetString()); Assert.Equal("1", saved.GetProperty("supplementRevision").GetString());
        Assert.Equal(image.sha256, saved.GetProperty("primaryEvidence").GetProperty("sha256").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("pdfEvidence").ValueKind);
        using var replay = await Post(route, create, parentRevision, createKey);
        Assert.Equal(saved.GetRawText(), (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetRawText());
        using var lookup = await Post(route + "/operations/query", new {purpose = "operation-recovery", key = createKey}, parentRevision);
        var receipt = await lookup.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("committed", receipt.GetProperty("status").GetString());
        Assert.Equal(saved.GetRawText(), receipt.GetProperty("result").GetRawText());
        using var changed = await Post(route, createWithReason("Berbeda"), parentRevision, createKey); Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        object createWithReason(string reason) => new {create.submissionId, create.primaryEvidence, reason};

        var pdfRoute = route + $"/{supplementId}/pdf-evidence";
        var reserveKey = $"ps.r.{data.Actor.UserId}.{source.Primary.DeclarationId}.{source.Primary.SubmissionId}.{supplementId}.{Guid.NewGuid():D}";
        using var wrongOwn = await Post(pdfRoute, new {reason = "Tambahan"}, "2", reserveKey); Assert.Equal(HttpStatusCode.Conflict, wrongOwn.StatusCode);
        using var reserve = await Post(pdfRoute, new {reason = "Tambahan"}, "1", reserveKey); Assert.True(reserve.IsSuccessStatusCode);
        var reservation = await reserve.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("1", reservation.GetProperty("revision").GetString());
        using var reserveLookup = await Post(pdfRoute + "/operations/query", new {purpose = "operation-recovery", key = reserveKey}, "1");
        var reserveReceipt = await reserveLookup.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("committed", reserveReceipt.GetProperty("status").GetString());
        Assert.Equal(reservation.GetRawText(), reserveReceipt.GetProperty("reservation").GetRawText());
        using var omittedBefore = await Post(pdfRoute + "/query", new {purpose = "payment-supplement-pdf-status"}, "1"); Assert.Equal(HttpStatusCode.BadRequest, omittedBefore.StatusCode);
        using var list = await Post(pdfRoute + "/query", new {purpose = "payment-supplement-pdf-status", beforeId = (string?)null}, "1"); Assert.True(list.IsSuccessStatusCode);
        Assert.Equal(reservation.GetProperty("reservationId").GetString(), (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0].GetProperty("reservationId").GetString());
        using var mistakenPrimary = await Post(route + $"/{supplementId}/attach", new {reservationId = reservation.GetProperty("reservationId").GetString(), evidence = image, reason = "Foto tidak boleh menjadi lampiran PDF"}, "1", "wrong-primary-as-supplement");
        Assert.False(mistakenPrimary.IsSuccessStatusCode); Assert.NotEqual(HttpStatusCode.InternalServerError, mistakenPrimary.StatusCode);
        var reviewerRoute = $"/api/v1/billing/payment-reviews/{source.Primary.DeclarationId}/supplements";
        using var reviewerWrite = await Post(reviewerRoute, create, parentRevision, createKey); Assert.Equal(HttpStatusCode.NotFound, reviewerWrite.StatusCode);
        using var reviewerReserve = await Post(reviewerRoute + $"/{supplementId}/pdf-evidence", new {reason = "Forbidden"}, "1", reserveKey); Assert.Equal(HttpStatusCode.NotFound, reviewerReserve.StatusCode);

        var foreign = await data.Catalog.ActorAsync(95117, new GrantScope(GrantScopeKind.BuyerOwned, 20, 21), Permissions.PaymentEvidence, Permissions.InvoiceRead);
        using var foreignClient = factory.CreateClient(new() {BaseAddress = new(origin), AllowAutoRedirect = false});
        foreignClient.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Catalog.Issued[foreign.UserId].OpaqueToken);
        var foreignCsrf = (await foreignClient.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        using var foreignRead = new HttpRequestMessage(HttpMethod.Post, route + $"/{supplementId}/query") {Content = JsonContent.Create(new {purpose = "payment-supplement-detail"})};
        foreignRead.Headers.Add("Origin", origin); foreignRead.Headers.Add("X-CSRF-Token", foreignCsrf); foreignRead.Headers.Add("X-Expected-Actor-Id", foreign.UserId.ToString()); foreignRead.Headers.Add("If-Match", '"' + parentRevision + '"');
        using var deniedForeign = await foreignClient.SendAsync(foreignRead); Assert.Equal(HttpStatusCode.Forbidden, deniedForeign.StatusCode);
        using var foreignLookup = new HttpRequestMessage(HttpMethod.Post, route + "/operations/query") {Content = JsonContent.Create(new {purpose = "operation-recovery", key = createKey})};
        foreignLookup.Headers.Add("Origin", origin); foreignLookup.Headers.Add("X-CSRF-Token", foreignCsrf); foreignLookup.Headers.Add("X-Expected-Actor-Id", foreign.UserId.ToString()); foreignLookup.Headers.Add("If-Match", '"' + parentRevision + '"');
        using var deniedLookup = await foreignClient.SendAsync(foreignLookup); Assert.Equal(HttpStatusCode.Forbidden, deniedLookup.StatusCode);
        Assert.Equal(before, await source.FinancialAsync());
        Assert.Equal(0L, await data.Rows(PaymentSupplementBson.Attachments).CountDocumentsAsync(new BsonDocument()));
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", PaymentSupplementBson.JobKind)).AnyAsync());
    }
    [WindowsStorageFact]
    public async Task Release1_leaves_every_supplement_route_absent_including_reviewer_reads()
    {
        await using var source = await PaymentSupplementEvidenceTestData.CreateAsync(fixture); var data = source.Data; using var keys = new IdentityRuntimeTests.RuntimeKeys();
        var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Release:Profile"] = "release1";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Catalog.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Catalog.Sessions);
        }));
        const string origin = "https://portal.unit-toko.co.id"; using var client = factory.CreateClient(new() {BaseAddress = new(origin), AllowAutoRedirect = false});
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Catalog.Issued[data.Actor.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        foreach (var parent in new[] {"payment-declarations", "payment-reviews"}) foreach (var suffix in new[] {"", "/query", "/operations/query", "/1/query", "/1/attach",
            "/1/attachment/operations/query", "/1/pdf-evidence", "/1/pdf-evidence/query", "/1/pdf-evidence/operations/query", "/1/pdf-evidence/1/query",
            "/1/pdf-evidence/1/content", "/1/pdf-evidence/1/recover", "/1/pdf-evidence/1/original/query", "/1/pdf-ocr/query"}) {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/billing/{parent}/1/supplements{suffix}") {Content = JsonContent.Create(new {purpose = "operation-recovery"})};
            request.Headers.Add("Origin", origin); request.Headers.Add("X-CSRF-Token", csrf); request.Headers.Add("X-Expected-Actor-Id", data.Actor.UserId.ToString());
            request.Headers.Add("If-Match", "\"1\""); request.Headers.Add("Idempotency-Key", "pilot-hidden-supplement");
            using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
