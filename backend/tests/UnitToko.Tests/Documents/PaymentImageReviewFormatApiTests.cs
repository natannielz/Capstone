using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentImageReviewFormatApiTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Scoped_reviewer_reads_exact_primary_image_while_stale_foreign_wrong_reference_and_explicit_PDF_are_refused_before_original_access() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture);
        // An actual separately reserved/uploaded PDF remains unsubmitted. It cannot replace
        // the primary IMAGE or acquire a reviewer original-read through the image route.
        var pdfReservation = await data.Service.ReservePdfAsync(data.Actor, data.DeclarationId, 1, EvidenceAdmissionTestData.Meta("review-format-distinct-pdf"));
        using var pdfBytes = new MemoryStream(PdfEvidenceAdmissionTestRuntime.Pdf(pages: 1));
        var uploadedPdf = await data.Service.UploadPdfAsync(data.Actor, data.DeclarationId, pdfReservation.ReservationId, pdfReservation.Revision,
            pdfBytes, EvidenceAdmissionTestData.Meta("review-format-distinct-pdf-upload"));
        var pdfReference = uploadedPdf.Evidence!.Reference;
        await PaymentOcrProcessorTests.Submit(data); // Actual private image + pinned native decoder; named synthetic scanner verdict.
        var reviewer = await PaymentReviewTests.Reviewer(data);
        var foreign = await data.Catalog.ActorAsync(9953, CatalogAuthoringTestData.OtherOwner, Permissions.PaymentReviewRead, Permissions.InvoiceRead);
        Assert.NotEqual(data.Actor.UserId, reviewer.UserId);
        var reservation = await data.Rows("evidenceUploadReservations").Find(new BsonDocument("_id", data.Reservation.ReservationId)).SingleAsync();
        var distinctPdf = await data.Rows("evidenceUploadReservations").Find(new BsonDocument("_id", pdfReservation.ReservationId)).SingleAsync();
        Assert.Equal("PaymentImage", reservation["purpose"]); Assert.Equal("PaymentPdf", distinctPdf["purpose"]);
        var submission = await data.Rows("paymentSubmissions").Find(new BsonDocument()).SingleAsync();
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("_id", submission["pinId"])).SingleAsync();
        Assert.Equal("PaymentSubmission", pin["associationKind"]); Assert.Equal(submission["_id"], pin["associationId"]);
        Assert.Equal(data.Reservation.ReservationId, submission["reservationId"]); Assert.Equal(data.Reference.Sha256, pin["reference"]["sha256"]);
        Assert.False(await data.Rows("evidencePins").Find(new BsonDocument("reservationId", pdfReservation.ReservationId)).AnyAsync());

        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Catalog.Sessions);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Payment.Service);
            services.RemoveAll<PaymentEvidenceService>(); services.AddSingleton(data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        using var foreignClient = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Catalog.Issued[reviewer.UserId].OpaqueToken);
        foreignClient.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Catalog.Issued[foreign.UserId].OpaqueToken);
        var current = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Contains("payment.review.read", current.GetProperty("allowedActions").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain("payment.verify", current.GetProperty("allowedActions").EnumerateArray().Select(value => value.GetString()));
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var foreignCsrf = (await foreignClient.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var acquisitions = 0;
        data.Service.ReadObserver = new(() => { acquisitions++; return Task.CompletedTask; });
        var financial = await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database);
        var originalVersions = await SnapshotAsync("documentVersions");
        var history = await SnapshotAsync("paymentSubmissions", "evidencePins", "evidencePinAudits",
            "evidenceValidationResults", "evidenceValidationAudits", "evidenceLifecycleAudits", "outboxEvents", "idempotencyRecords",
            "documentExtractionResults", "documentExtractionAudits");
        var beforeDenied = await SnapshotAsync("evidenceReadLeases", "evidenceReadAudits", "auditEvents", "authorizationReadEvents");
        foreach (var (fault, expected) in new[] { ("stale", HttpStatusCode.Conflict), ("hash", HttpStatusCode.Conflict),
            ("foreign", HttpStatusCode.Forbidden), ("pdf", HttpStatusCode.BadRequest) }) {
            using var denied = await SendAsync(fault);
            Assert.True(expected == denied.StatusCode, $"Reviewer original fault={fault}; expected={(int)expected}; actual={(int)denied.StatusCode}");
            Assert.NotEqual("image/png", denied.Content.Headers.ContentType?.MediaType);
        }
        Assert.Equal(0, acquisitions); SameRows(beforeDenied, await SnapshotAsync("evidenceReadLeases", "evidenceReadAudits", "auditEvents", "authorizationReadEvents"));
        SameRows(originalVersions, await SnapshotAsync("documentVersions"));
        using var response = await SendAsync();
        Assert.True(response.IsSuccessStatusCode, "Authorized exact primary image returned status " + (int)response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType); Assert.Equal(data.Bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(response.Headers.CacheControl!.Private); Assert.True(response.Headers.CacheControl.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(data.Bytes, await response.Content.ReadAsByteArrayAsync()); Assert.Equal(1, acquisitions);
        var lease = await data.Rows("evidenceReadLeases").Find(new BsonDocument("actor.userId", reviewer.UserId)).SingleAsync();
        Assert.Equal("released", lease["state"]); Assert.Equal(data.Reference.Sha256, lease["reference"]["sha256"]);
        Assert.True(await data.Rows("evidenceReadAudits").Find(new BsonDocument("leaseId", lease["_id"])).AnyAsync());
        SameRows(financial, await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database));
        SameRows(history, await SnapshotAsync("paymentSubmissions", "evidencePins", "evidencePinAudits",
            "evidenceValidationResults", "evidenceValidationAudits", "evidenceLifecycleAudits", "outboxEvents", "idempotencyRecords",
            "documentExtractionResults", "documentExtractionAudits"));
        var actualVersions = (await SnapshotAsync("documentVersions"))["documentVersions"].AsBsonArray;
        Assert.Equal(originalVersions["documentVersions"].AsBsonArray.Count, actualVersions.Count);
        foreach (var original in originalVersions["documentVersions"].AsBsonArray) {
            var expected = original.DeepClone().AsBsonDocument;
            if (expected["_id"] == pin["versionRowId"]) {
                // Acquisition and initial byte authorization each perform the real current
                // eligibility fence. Content, proof, pin, and lifecycle remain unchanged.
                expected["revision"] = expected["revision"].AsInt64 + 2;
                expected["fence"] = expected["fence"].AsInt64 + 2;
                expected["updatedAt"] = data.Catalog.Clock.UtcNow.UtcDateTime;
            }
            var actual = Assert.Single(actualVersions, row => row["_id"] == expected["_id"]);
            Assert.True(expected.Equals(actual), "Original version changed beyond the exact authorized read-fence revision, fence, and time");
        }

        async Task<HttpResponseMessage> SendAsync(string? fault = null) {
            var pdf = fault == "pdf"; var reference = pdf ? pdfReference : data.Reference;
            var selectedClient = fault == "foreign" ? foreignClient : client;
            var path = $"/api/v1/billing/payment-reviews/{data.DeclarationId}/evidence/{(pdf ? pdfReservation.ReservationId : data.Reservation.ReservationId)}/original/query";
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new {
                purpose = "payment-evidence-view", reference = new { id = reference.Id.ToString(), version = reference.Version.ToString(),
                    sha256 = fault == "hash" ? new string('f', 64) : reference.Sha256 } }) };
            request.Headers.Add("Origin", origin); request.Headers.Add("X-CSRF-Token", fault == "foreign" ? foreignCsrf : csrf);
            request.Headers.Add("X-Expected-Actor-Id", (fault == "foreign" ? foreign.UserId : reviewer.UserId).ToString());
            request.Headers.Add("If-Match", fault == "stale" ? "\"2\"" : "\"1\""); return await selectedClient.SendAsync(request);
        }
        async Task<BsonDocument> SnapshotAsync(params string[] names) {
            var result = new BsonDocument(); foreach (var name in names)
                result[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
            return result;
        }
        static void SameRows(BsonDocument expected, BsonDocument actual) {
            Assert.Equal(expected.Names, actual.Names);
            foreach (var name in expected.Names) Assert.True(expected[name].Equals(actual[name]), "Stored collection changed: " + name);
        }
    }
}
