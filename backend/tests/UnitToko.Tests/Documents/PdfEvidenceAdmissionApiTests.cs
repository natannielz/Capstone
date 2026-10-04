using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Documents;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Identity;
using Data = UnitToko.Tests.Documents.PdfEvidenceAdmissionTests.PaymentPdfData;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PdfEvidenceAdmissionApiTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Full_profile_actual_PDF_routes_keep_private_draft_original_and_refuse_PDF_as_primary_payment_evidence() {
        await using var data = await Data.CreateAsync(fixture); using var keys = new IdentityRuntimeTests.RuntimeKeys();
        var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Release:Profile"] = "full";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Catalog.Sessions);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Payment.Service);
            services.RemoveAll<PaymentEvidenceService>(); services.AddSingleton(data.Evidence);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Catalog.Issued[data.Actor.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var route = $"/api/v1/billing/payment-declarations/{data.DeclarationId}/pdf-evidence";
        async Task<HttpResponseMessage> Send(string path, object body, string? key = null, string? fault = null, byte[]? content = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content is null ? JsonContent.Create(body) : new ByteArrayContent(content) };
            if (content is not null) request.Content.Headers.ContentType = new(fault == "mime" ? "application/pdf" : "application/octet-stream");
            request.Headers.Add("Origin", fault == "origin" ? "https://foreign.invalid" : origin);
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", data.Actor.UserId.ToString());
            if (fault != "revision") request.Headers.Add("If-Match", fault == "stale" ? "\"2\"" : "\"1\"");
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            if (fault == "range") request.Headers.Add("Range", "bytes=0-3");
            return await client.SendAsync(request);
        }
        var reserveBody = new { reason = "Bukti PDF uji lokal; belum menjadi pengajuan pembayaran" };
        foreach (var (fault, expected) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden), ("actor", HttpStatusCode.BadRequest), ("revision", HttpStatusCode.BadRequest) }) {
            using var denied = await Send(route, reserveBody, "pdf-api-reserve", fault); Assert.Equal(expected, denied.StatusCode);
        }
        using var reserved = await Send(route, reserveBody, "pdf-api-reserve"); Assert.True(reserved.IsSuccessStatusCode, await reserved.Content.ReadAsStringAsync());
        var reservation = await reserved.Content.ReadFromJsonAsync<JsonElement>(); var id = reservation.GetProperty("reservationId").GetString()!;
        using var replay = await Send(route, reserveBody, "pdf-api-reserve"); Assert.Equal(reservation.GetRawText(), (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetRawText());
        using var recovery = await Send(route + "/operations/query", new { purpose = "operation-recovery", key = "pdf-api-reserve" });
        var receipt = await recovery.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("committed", receipt.GetProperty("status").GetString()); Assert.Equal(reservation.GetRawText(), receipt.GetProperty("reservation").GetRawText());
        var bytes = PdfEvidenceAdmissionTestRuntime.Pdf();
        using var badMime = await Send(route + $"/{id}/content", new { }, "pdf-api-upload", "mime", bytes); Assert.Equal(HttpStatusCode.UnsupportedMediaType, badMime.StatusCode);
        using var uploaded = await Send(route + $"/{id}/content", new { }, "pdf-api-upload", content: bytes); Assert.True(uploaded.IsSuccessStatusCode, await uploaded.Content.ReadAsStringAsync());
        var result = await uploaded.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("quarantined", result.GetProperty("state").GetString()); var referenceDto = result.GetProperty("reference");
        var reference = new EvidenceRef(long.Parse(referenceDto.GetProperty("id").GetString()!), 1, referenceDto.GetProperty("sha256").GetString()!);
        var readBody = new { purpose = "payment-evidence-view", reference = new { id = reference.Id.ToString(), version = "1", sha256 = reference.Sha256 } };
        using var pending = await Send(route + $"/{id}/original/query", readBody); Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        await using var runtime = await data.RuntimeAsync(); Assert.Equal("completed", await runtime.Runtime.RunOnceAsync());
        foreach (var (fault, expected) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden), ("revision", HttpStatusCode.BadRequest), ("stale", HttpStatusCode.Conflict), ("range", HttpStatusCode.BadRequest) }) {
            using var denied = await Send(route + $"/{id}/original/query", readBody, fault: fault); Assert.Equal(expected, denied.StatusCode); Assert.NotEqual("application/pdf", denied.Content.Headers.ContentType?.MediaType);
        }
        using var original = await Send(route + $"/{id}/original/query", readBody); Assert.True(original.IsSuccessStatusCode, await original.Content.ReadAsStringAsync());
        Assert.Equal("application/pdf", original.Content.Headers.ContentType?.MediaType); Assert.Equal(bytes, await original.Content.ReadAsByteArrayAsync());
        Assert.True(original.Headers.CacheControl!.Private); Assert.True(original.Headers.CacheControl.NoStore);
        Assert.Equal("attachment", original.Content.Headers.ContentDisposition!.DispositionType);
        Assert.EndsWith(".pdf", original.Content.Headers.ContentDisposition.FileNameStar ?? original.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        using var omittedPageBound = await Send(route + "/query", new { purpose = "payment-pdf-evidence" });
        Assert.Equal(HttpStatusCode.BadRequest, omittedPageBound.StatusCode);
        using var list = await Send(route + "/query", new { purpose = "payment-pdf-evidence", beforeId = (string?)null });
        Assert.True(list.IsSuccessStatusCode, await list.Content.ReadAsStringAsync());
        Assert.Equal(id, (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0].GetProperty("reservationId").GetString());
        using var imageRoute = await Send($"/api/v1/billing/payment-declarations/{data.DeclarationId}/evidence/{id}/original/query", readBody);
        Assert.Equal(HttpStatusCode.BadRequest, imageRoute.StatusCode); Assert.NotEqual("application/pdf", imageRoute.Content.Headers.ContentType?.MediaType);
        var financialBefore = await data.FinanceAsync();
        var submitRoute = $"/api/v1/billing/payment-declarations/{data.DeclarationId}/submit";
        var submitBody = new { reservationId = id, evidence = readBody.reference, reason = "PDF tidak boleh menggantikan bukti gambar wajib untuk pembayaran" };
        data.Evidence.ReadObserver = new(() => throw new InvalidOperationException("HTTP primary-PDF refusal must precede original opening"));
        using var submission = await Send(submitRoute, submitBody, "pdf-api-submit");
        Assert.Equal(HttpStatusCode.BadRequest, submission.StatusCode);
        Assert.Equal("payment_image_evidence_required", (await submission.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var submittedReplay = await Send(submitRoute, submitBody, "pdf-api-submit");
        Assert.Equal(HttpStatusCode.BadRequest, submittedReplay.StatusCode);
        Assert.Equal("payment_image_evidence_required", (await submittedReplay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var submissionRecovery = await Send($"/api/v1/billing/payment-declarations/{data.DeclarationId}/submission/operations/query", new { purpose = "operation-recovery", key = "pdf-api-submit" });
        Assert.True(submissionRecovery.IsSuccessStatusCode, await submissionRecovery.Content.ReadAsStringAsync());
        var recoveredSubmission = await submissionRecovery.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("not_confirmed", recoveredSubmission.GetProperty("status").GetString());
        Assert.False(await data.Rows("paymentSubmissions").Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "PaymentSubmission")).AnyAsync());
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", PaymentSubmissionBson.Operation)).AnyAsync());
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", PdfDocumentExtractionContract.PaymentJobKind)).AnyAsync());
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", "payment.ocr.requested")).AnyAsync());
        Assert.Equal("draft", (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).State);
        Assert.Equal(financialBefore, await data.FinanceAsync()); Assert.False(await data.Rows("payments").Find(new BsonDocument()).AnyAsync());
        // The same genuine declaration can proceed using its mandatory eligible image;
        // the admitted draft PDF never becomes that association or an OCR source.
        data.Evidence.ReadObserver = null;
        var imageReservation = await data.Evidence.ReserveAsync(data.Actor, data.DeclarationId, 1, EvidenceAdmissionTestData.Meta("pdf-api-required-image"));
        using var imageBytes = new MemoryStream(ImageDecoderTests.Png(13, 7));
        var imageUpload = await data.Evidence.UploadAsync(data.Actor, data.DeclarationId, imageReservation.ReservationId, 1, imageBytes, EvidenceAdmissionTestData.Meta("pdf-api-image-upload"));
        var imageReference = imageUpload.Evidence!.Reference;
        var imageValidator = new EvidenceValidationProcessor(data.Database, new(data.Catalog.Client), new(data.Database), data.Clock, data.Files.Registration,
            EvidenceValidationTests.Consumer, new string('c', 64), EvidenceValidationTests.ClaimPolicy, new(1024, 100, 100, 10000),
            new EvidenceValidationTests.FixtureScanner("clean", signaturesAt: data.Clock.UtcNow.AddHours(-1)), new WindowsImageDecoder(ImageDecoderTests.Options()));
        await EvidenceValidationTests.Policy(data.Database, data.Clock, data.Files.StoreId, imageValidator.PipelineId);
        Assert.Equal("completed", await imageValidator.RunOnceAsync());
        var imageDto = new { id = imageReference.Id.ToString(), version = "1", sha256 = imageReference.Sha256 };
        using var imageSubmitted = await Send(submitRoute, new { reservationId = imageReservation.ReservationId.ToString(), evidence = imageDto, reason = "Pengajuan memakai gambar asli yang telah divalidasi" }, "pdf-api-real-image-submit");
        Assert.True(imageSubmitted.IsSuccessStatusCode, await imageSubmitted.Content.ReadAsStringAsync());
        Assert.Equal("submitted", (await imageSubmitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        using var imageFormat = await Send($"/api/v1/billing/payment-declarations/{data.DeclarationId}/evidence-format/query",
            new { purpose = "payment-evidence-format-view", reference = imageDto });
        Assert.True(imageFormat.IsSuccessStatusCode, await imageFormat.Content.ReadAsStringAsync());
        Assert.Equal("image", (await imageFormat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kind").GetString());
        var storedImage = await data.Rows("paymentSubmissions").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(imageReference.Sha256, storedImage["evidence"]["sha256"]); Assert.NotEqual(reference.Sha256, storedImage["evidence"]["sha256"]);
        Assert.Equal(1, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument("associationKind", "PaymentSubmission")));
        Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "payment.ocr.requested")));
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", PdfDocumentExtractionContract.PaymentJobKind)).AnyAsync());
        foreach (var unsupported in new[] {
            $"/api/v1/billing/payment-declarations/{data.DeclarationId}/pdf-ocr/query",
            $"/api/v1/billing/payment-reviews/{data.DeclarationId}/pdf-ocr/query",
            $"/api/v1/billing/payment-reviews/{data.DeclarationId}/pdf-evidence/{id}/original/query" }) {
            using var absent = await Send(unsupported, new { purpose = "payment-pdf-ocr-view", reference = imageDto });
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        }
        Assert.Equal(financialBefore, await data.FinanceAsync());
        var pilotSettings = new Dictionary<string, string?>(settings) { ["Release:Profile"] = "release1" };
        using var pilot = IdentityRuntimeTests.Factory(pilotSettings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Catalog.Sessions);
        }));
        using var pilotClient = pilot.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        pilotClient.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Catalog.Issued[data.Actor.UserId].OpaqueToken);
        var pilotCsrf = (await pilotClient.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        foreach (var hidden in new[] { route, "/api/v1/procurement/supplier-documents/drafts/1/pdf-evidence" }) {
            using var request = new HttpRequestMessage(HttpMethod.Post, hidden) { Content = JsonContent.Create(reserveBody) };
            request.Headers.Add("Origin", origin); request.Headers.Add("X-CSRF-Token", pilotCsrf); request.Headers.Add("X-Expected-Actor-Id", data.Actor.UserId.ToString());
            request.Headers.Add("If-Match", "\"1\""); request.Headers.Add("Idempotency-Key", "pilot-cannot-admit-pdf");
            using var notMapped = await pilotClient.SendAsync(request); Assert.Equal(HttpStatusCode.NotFound, notMapped.StatusCode);
        }
    }
}
