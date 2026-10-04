using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Procurement;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PdfDocumentOcrApiTests(MongoFixture fixture)
{
    private const string Origin = "https://portal.unit-toko.co.id";
    [WindowsStorageFact]
    public async Task Supplier_PDF_format_page_aware_extraction_and_original_require_current_scoped_authority_through_actual_API() {
        await using var data = await SupplierDocumentTestData.CreateAsync(fixture);
        await using var scanner = await PdfEvidenceAdmissionTestRuntime.CreateAsync(data.Database, data.Unit, data.Clock, data.Files.Path, ProcurementInspectionTestData.StoreId);
        await PdfDocumentOcrProcessingTestData.ReadPolicyAsync(data.Database, ProcurementInspectionTestData.StoreId);
        var reservation = await data.Evidence.ReservePdfAsync(data.Actor, data.DraftId, 1, SupplierDocumentTestData.Meta("supplier-pdf-http"));
        var bytes = PdfDocumentOcrProcessingTestData.Pdf(ExtractionPurpose.SupplierInvoice); using var input = new MemoryStream(bytes);
        var uploaded = await data.Evidence.UploadPdfAsync(data.Actor, data.DraftId, reservation.ReservationId, reservation.Revision, input, SupplierDocumentTestData.Meta("supplier-pdf-http-upload"));
        var reference = uploaded.Evidence!.Reference; Assert.Equal("completed", await scanner.Runtime.RunOnceAsync());
        var runtime = PdfDocumentOcrRuntime.CreateSupplierInvoice(data.Database, data.Unit, new(data.Database), data.Clock,
            PdfDocumentOcrProcessingTestData.Options(data.Files.Path, ProcurementInspectionTestData.StoreId, PdfDocumentOcrProcessingTestData.SupplierConsumer));
        await PdfDocumentOcrProcessingTestData.PolicyAsync(data.Database, data.Clock, ProcurementInspectionTestData.StoreId, runtime);
        var preview = await data.Service.PreviewSubmitAsync(data.Actor, data.DraftId, 1, reservation.ReservationId, reference);
        await data.Evidence.SubmitAsync(data.Actor, data.DraftId, 1, reservation.ReservationId, reference, preview["binding"].AsString, SupplierDocumentTestData.Meta("supplier-pdf-http-submit"));
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"),
            SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var actorToken = await IssueAsync(data.Database, data.Unit, data.Clock, sessions, data.Actor);
        var readerToken = await IssueAsync(data.Database, data.Unit, data.Clock, sessions, data.EvidenceReader);
        var outsiderToken = await IssueAsync(data.Database, data.Unit, data.Clock, sessions, data.Outsider);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Release:Profile"] = "full";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<SupplierDocumentService>(); services.AddSingleton(data.Service); services.RemoveAll<SupplierDocumentEvidenceService>(); services.AddSingleton(data.Evidence);
        }));
        using var actorClient = Client(factory, actorToken); using var readerClient = Client(factory, readerToken); using var outsiderClient = Client(factory, outsiderToken);
        var actor = await Browser.CreateAsync(actorClient, data.Actor.UserId); var reader = await Browser.CreateAsync(readerClient, data.EvidenceReader.UserId);
        var outsider = await Browser.CreateAsync(outsiderClient, data.Outsider.UserId);
        var root = $"/api/v1/procurement/supplier-documents/drafts/{data.DraftId}"; var format = root + "/evidence-format/query"; var extraction = root + "/pdf-ocr/query";
        var original = root + $"/pdf-evidence/{reservation.ReservationId}/original/query";
        var beforeReads = await ReadsAsync(data.Database); var moneyBefore = await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database);
        foreach (var (path, purpose) in new[] { (format, "supplier-document-evidence-format-view"), (extraction, "supplier-document-pdf-ocr-view"), (original, "supplier-document-evidence-view") }) {
            await DeniedBoundariesAsync(actor, path, purpose, reference);
            using var denied = await outsider.SendAsync(path, purpose, reference); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var range = await actor.SendAsync(original, "supplier-document-evidence-view", reference, "range")) Assert.Equal(HttpStatusCode.BadRequest, range.StatusCode);
        using (var image = await reader.SendAsync(root + $"/evidence/{reservation.ReservationId}/original/query", "supplier-document-evidence-view", reference)) {
            Assert.Equal(HttpStatusCode.BadRequest, image.StatusCode); Assert.NotEqual("application/pdf", image.Content.Headers.ContentType?.MediaType);
        }
        foreach (var unsupported in new[] { $"/api/v1/billing/payment-declarations/{data.DraftId}/pdf-ocr/query",
            $"/api/v1/billing/payment-reviews/{data.DraftId}/pdf-ocr/query", $"/api/v1/billing/payment-reviews/{data.DraftId}/pdf-evidence/{reservation.ReservationId}/original/query" }) {
            using var response = await actor.SendAsync(unsupported, "payment-pdf-ocr-view", reference); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        Assert.Equal(beforeReads, await ReadsAsync(data.Database)); Assert.Equal(moneyBefore, await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database));
        await FormatAsync(actor, format, "supplier-document-evidence-format-view", data.DraftId, reference);
        await FormatAsync(reader, format, "supplier-document-evidence-format-view", data.DraftId, reference);
        using (var queued = await actor.SendAsync(extraction, "supplier-document-pdf-ocr-view", reference)) {
            Assert.True(queued.IsSuccessStatusCode, await queued.Content.ReadAsStringAsync());
            var value = await queued.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("queued", value.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, value.GetProperty("result").ValueKind);
        }
        Assert.Equal("completed", await runtime.RunOnceAsync());
        foreach (var browser in new[] { actor, reader }) {
            await ResultAsync(browser, extraction, "supplier-document-pdf-ocr-view", data.DraftId, reference, "SupplierInvoice", "total_amount");
            await OriginalAsync(browser, original, "supplier-document-evidence-view", reference, bytes);
        }
        Assert.Equal(moneyBefore, await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database));
        var beforeRevoked = await ReadsAsync(data.Database);
        await data.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.EvidenceReader.UserId), new BsonDocument("$set", new BsonDocument("status", "revoked")));
        foreach (var (path, purpose) in new[] { (format, "supplier-document-evidence-format-view"), (extraction, "supplier-document-pdf-ocr-view"), (original, "supplier-document-evidence-view") }) {
            using var revoked = await reader.SendAsync(path, purpose, reference); Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        }
        Assert.Equal(beforeRevoked, await ReadsAsync(data.Database)); Assert.Equal(moneyBefore, await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database));
        var pilotSettings = new Dictionary<string, string?>(settings) { ["Release:Profile"] = "release1" };
        using var pilot = IdentityRuntimeTests.Factory(pilotSettings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
        }));
        using var pilotClient = Client(pilot, actorToken); var pilotBrowser = await Browser.CreateAsync(pilotClient, data.Actor.UserId);
        foreach (var (path, purpose) in new[] { (format, "supplier-document-evidence-format-view"), (extraction, "supplier-document-pdf-ocr-view"), (original, "supplier-document-evidence-view") }) {
            using var hidden = await pilotBrowser.SendAsync(path, purpose, reference); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
        Assert.Equal(beforeRevoked, await ReadsAsync(data.Database)); Assert.Equal(moneyBefore, await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database));
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string token) {
        var client = factory.CreateClient(new() { BaseAddress = new(Origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + token); return client;
    }
    private static async Task<string> IssueAsync(IMongoDatabase database, MongoUnitOfWork unit, IClock clock, MongoIdentitySessionStore sessions, ActorContext actor) {
        await database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), new BsonDocument("$set", new BsonDocument("securityVersion", 1L)));
        var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "synthetic-pdf-http@example.invalid", true, "fixture-connection", clock.UtcNow, clock.UtcNow, "otp", "fixture-policy");
        var issue = await unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-pdf-http-no-provider-call", ct));
        return issue.OpaqueToken;
    }
    private static async Task<BsonDocument> ReadsAsync(IMongoDatabase database) {
        var result = new BsonDocument(); foreach (var name in new[] { "evidenceReadLeases", "evidenceReadAudits", PdfDocumentExtractionRecords.Reads, "authorizationReadEvents" })
            result[name] = new BsonArray(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    private static async Task DeniedBoundariesAsync(Browser browser, string path, string purpose, EvidenceRef reference) {
        foreach (var (fault, expected) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden), ("actor", HttpStatusCode.BadRequest),
            ("different-actor", HttpStatusCode.Conflict), ("revision", HttpStatusCode.BadRequest), ("stale", HttpStatusCode.Forbidden), ("purpose", HttpStatusCode.BadRequest) }) {
            using var response = await browser.SendAsync(path, purpose, reference, fault);
            Assert.True(expected == response.StatusCode, $"Denied boundary path={path}; fault={fault}; expected={(int)expected}; actual={(int)response.StatusCode}");
        }
    }
    private static async Task FormatAsync(Browser browser, string path, string purpose, long id, EvidenceRef reference) {
        using var response = await browser.SendAsync(path, purpose, reference); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); Private(response);
        var value = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(id.ToString(), value.GetProperty("sourceId").GetString());
        Assert.Equal("1", value.GetProperty("sourceRevision").GetString()); Assert.Equal("pdf", value.GetProperty("kind").GetString());
        Assert.Equal(reference.Sha256, value.GetProperty("evidence").GetProperty("sha256").GetString());
    }
    private static async Task ResultAsync(Browser browser, string path, string purpose, long id, EvidenceRef reference, string kind, string amountName) {
        using var response = await browser.SendAsync(path, purpose, reference); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); Private(response);
        var value = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("succeeded", value.GetProperty("state").GetString()); Assert.Equal(kind, value.GetProperty("sourceKind").GetString());
        Assert.Equal(id.ToString(), value.GetProperty("sourceId").GetString()); Assert.Equal("1", value.GetProperty("sourceRevision").GetString());
        Assert.Equal(reference.Sha256, value.GetProperty("evidence").GetProperty("sha256").GetString());
        var result = value.GetProperty("result"); Assert.Matches("^[a-f0-9]{64}$", result.GetProperty("resultHash").GetString()!); Assert.True(result.GetProperty("requiresHumanReview").GetBoolean());
        var pages = result.GetProperty("pages").EnumerateArray().ToArray(); Assert.Equal(2, pages.Length);
        Assert.Equal(new[] { 0, 90 }, pages.Select(page => page.GetProperty("raster").GetProperty("rotationDegrees").GetInt32()));
        Assert.All(pages, page => { Assert.NotEmpty(page.GetProperty("lines").EnumerateArray()); Assert.True(page.GetProperty("preprocessing").ValueKind == JsonValueKind.Object); });
        var amount = Assert.Single(result.GetProperty("fields").EnumerateArray(), field => field.GetProperty("field").GetString() == amountName);
        Assert.Equal(new[] { "1234567.89", "2345678.90" }, amount.GetProperty("observations").EnumerateArray().Select(item => item.GetProperty("candidate").GetProperty("value").GetString()));
        Assert.Contains("contradictory_pages", amount.GetProperty("reviewReasons").EnumerateArray().Select(item => item.GetString()));
        foreach (var secret in new[] { "stageToken", "leaseToken", "generation", "consumerId", "policyId", "storeId" }) Assert.False(value.GetRawText().Contains('"' + secret + '"', StringComparison.Ordinal));
    }
    private static async Task OriginalAsync(Browser browser, string path, string purpose, EvidenceRef reference, byte[] bytes) {
        using var response = await browser.SendAsync(path, purpose, reference); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); Private(response);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType); Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength); Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("default-src 'none'; sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy"))); Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.False(response.Headers.Contains("Accept-Ranges"));
    }
    private static void Private(HttpResponseMessage response) {
        Assert.True(response.Headers.CacheControl!.Private); Assert.True(response.Headers.CacheControl.NoStore); Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }
    private sealed record Browser(HttpClient Client, long ActorId, string Csrf) {
        internal static async Task<Browser> CreateAsync(HttpClient client, long actorId) => new(client, actorId,
            (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!);
        internal async Task<HttpResponseMessage> SendAsync(string path, string purpose, EvidenceRef reference, string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new {
                purpose = fault == "purpose" ? "wrong-source-purpose" : purpose,
                reference = new { id = reference.Id.ToString(), version = reference.Version.ToString(), sha256 = reference.Sha256 } }) };
            request.Headers.Add("Origin", fault == "origin" ? "https://foreign.invalid" : Origin);
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", Csrf);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", (fault == "different-actor" ? ActorId + 999 : ActorId).ToString());
            if (fault != "revision") request.Headers.Add("If-Match", fault == "stale" ? "\"2\"" : "\"1\"");
            if (fault == "range") request.Headers.Add("Range", "bytes=0-3");
            return await Client.SendAsync(request);
        }
    }
}
