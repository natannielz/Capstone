using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;

namespace UnitToko.Api.Billing;

// Additive full-profile-only routes. The primary image endpoints/DTOs are unchanged.
public static class PaymentSupplementEndpoints
{
    public static void MapPaymentSupplementEndpoints(this WebApplication app) {
        MapRead(app, "/api/v1/billing/payment-declarations/{id}/supplements", false);
        MapRead(app, "/api/v1/billing/payment-reviews/{id}/supplements", true);
        var group = Group(app, "/api/v1/billing/payment-declarations/{id}/supplements");
        group.MapPost("", async (string id, PaymentSupplementCreateDto request, HttpContext http, PaymentSupplementEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            Private(http); var primary = new PaymentSupplementPrimary(Owner(id), ParentRevision(http), request.SubmissionId.Value, PaymentSupplementDtos.Reference(request.PrimaryEvidence)).Validate();
            var row = await service.Domain.CreateAsync(Actor(http), primary, Command(http, request.Reason), ct);
            var result = PaymentSupplementDtos.View(row); http.Response.Headers.ETag = '"' + result.SupplementRevision + '"';
            return TypedResults.Ok(new PaymentSupplementResultDto(PaymentSupplementDtos.CreateOperation, result));
        }).WithName("CreatePaymentSupplement");
        group.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, PaymentSupplementEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); Private(http);
            var receipt = await service.RecoverCreateAsync(Actor(http), Owner(id), ParentRevision(http), request.Key, ct);
            return TypedResults.Ok(Recovery(receipt));
        }).WithName("RecoverPaymentSupplementCreation");
        group.MapPost("/{supplementId}/attach", async (string id, string supplementId, PaymentSupplementAttachDto request, HttpContext http,
            PaymentSupplementEvidenceService service, [FromHeader(Name = "If-Match")] string expected, [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            Private(http); var row = await service.AttachAsync(Actor(http), Owner(id), Owner(supplementId), OwnRevision(http, 1),
                request.ReservationId.Value, PaymentSupplementDtos.Reference(request.Evidence), Command(http, request.Reason), ct);
            var result = PaymentSupplementDtos.View(row); http.Response.Headers.ETag = '"' + result.SupplementRevision + '"';
            return TypedResults.Ok(new PaymentSupplementResultDto(PaymentSupplementDtos.AttachOperation, result));
        }).WithName("AttachPaymentSupplementPdf");
        group.MapPost("/{supplementId}/attachment/operations/query", async (string id, string supplementId, OrderDraftRecoveryDto request, HttpContext http,
            PaymentSupplementEvidenceService service, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); OwnRecoveryRevision(http); Private(http);
            // Original command revision1 must remain recoverable after it commits revision2.
            var receipt = await service.RecoverAttachAsync(Actor(http), Owner(id), Owner(supplementId), request.Key, ct);
            return TypedResults.Ok(Recovery(receipt));
        }).WithName("RecoverPaymentSupplementAttachment");
        MapAdmission(group);
    }
    private static void MapRead(WebApplication app, string route, bool review) {
        var group = Group(app, route); var prefix = review ? "PaymentReviewSupplement" : "PaymentSupplement";
        group.MapPost("/query", async (string id, InvoiceQueryDto request, HttpContext http, PaymentSupplementEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, review ? "payment-review-supplements-list" : "payment-supplements-list"); Private(http);
            var page = await service.Domain.ListAsync(Actor(http), Owner(id), ParentRevision(http), request.BeforeId?.Value, Read(http, request.Purpose), review, ct);
            return TypedResults.Ok(new PaymentSupplementPageDto([.. page.Items.Select(PaymentSupplementDtos.View)], page.NextSupplementId is { } next ? new LongId(next) : null, page.ObservedAt, page.Readiness));
        }).WithName("List" + prefix + "s");
        group.MapPost("/{supplementId}/query", async (string id, string supplementId, OrderReadDto request, HttpContext http, PaymentSupplementEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, review ? "payment-review-supplement-detail" : "payment-supplement-detail"); Private(http);
            var view = await service.Domain.DetailAsync(Actor(http), Owner(id), ParentRevision(http), Owner(supplementId), Read(http, request.Purpose), review, ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(view.SupplementRevision); return TypedResults.Ok(PaymentSupplementDtos.View(view));
        }).WithName("Read" + prefix);
        group.MapPost("/{supplementId}/pdf-evidence/{reservationId}/original/query", async (string id, string supplementId, string reservationId,
            PaymentEvidenceReadDto request, HttpContext http, PaymentSupplementEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, review ? "payment-review-supplement-pdf-view" : "payment-supplement-pdf-view");
            if (http.Request.Headers.ContainsKey("Range")) throw PaymentSupplementRules.Invalid();
            var reference = PaymentSupplementDtos.Reference(request.Reference); var own = OwnRevision(http, 2); var actor = Actor(http);
            var file = review ? await service.OpenReviewPdfReadAsync(actor, Owner(id), Owner(supplementId), Owner(reservationId), own, reference, Read(http, request.Purpose), ct)
                : await service.OpenPdfReadAsync(actor, Owner(id), Owner(supplementId), Owner(reservationId), own, reference, Read(http, request.Purpose), ct);
            if (file.Mime != "application/pdf" || file.Length is <= 0 or > PaymentEvidenceBodyStream.MaximumBytes) {
                await file.Content.DisposeAsync(); throw PaymentSupplementRules.Invalid();
            }
            Private(http); http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Response.Headers["Referrer-Policy"] = "no-referrer"; http.Response.ContentLength = file.Length;
            return Results.Stream(file.Content, "application/pdf", $"lampiran-pdf-{supplementId}-{reference.Id}-{reference.Version}.pdf", enableRangeProcessing: false);
        }).Produces<byte[]>(200, "application/pdf").WithName("Read" + prefix + "PdfOriginal");
        group.MapPost("/{supplementId}/pdf-ocr/query", async (string id, string supplementId, PaymentEvidenceReadDto request, HttpContext http,
            PaymentSupplementEvidenceService service, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, review ? "payment-review-supplement-pdf-ocr-view" : "payment-supplement-pdf-ocr-view");
            var reference = PaymentSupplementDtos.Reference(request.Reference); var own = OwnRevision(http, 2);
            var view = review ? await service.ReadReviewPdfExtractionAsync(Actor(http), Owner(id), Owner(supplementId), own, reference, Read(http, request.Purpose), ct)
                : await service.ReadPdfExtractionAsync(Actor(http), Owner(id), Owner(supplementId), own, reference, Read(http, request.Purpose), ct);
            Private(http); return TypedResults.Ok(PaymentSupplementDtos.Ocr(view));
        }).WithName("Read" + prefix + "PdfOcr");
    }
    private static void MapAdmission(RouteGroupBuilder group) {
        group.MapPost("/{supplementId}/pdf-evidence", async (string id, string supplementId, PaymentEvidenceReserveDto request, HttpContext http,
            PaymentSupplementEvidenceService service, [FromHeader(Name = "If-Match")] string expected, [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            Private(http); return TypedResults.Ok(Reservation(await service.ReservePdfAsync(Actor(http), Owner(id), Owner(supplementId), OwnRevision(http, 1), Command(http, request.Reason), ct)));
        }).WithName("ReservePaymentSupplementPdfEvidence");
        group.MapPost("/{supplementId}/pdf-evidence/query", async (string id, string supplementId, InvoiceQueryDto request, HttpContext http,
            PaymentSupplementEvidenceService service, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, "payment-supplement-pdf-status"); Private(http);
            var rows = await service.ListPdfsAsync(Actor(http), Owner(id), Owner(supplementId), OwnRevision(http, 1), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new PaymentEvidencePageDto([.. rows.Select(Status)]));
        }).WithName("ListPaymentSupplementPdfEvidence");
        group.MapPost("/{supplementId}/pdf-evidence/{reservationId}/query", async (string id, string supplementId, string reservationId,
            OrderReadDto request, HttpContext http, PaymentSupplementEvidenceService service, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, "payment-supplement-pdf-status"); Private(http);
            return TypedResults.Ok(Status(await service.PdfStatusAsync(Actor(http), Owner(id), Owner(supplementId), OwnRevision(http, 1), Owner(reservationId), ct)));
        }).WithName("ReadPaymentSupplementPdfEvidenceStatus");
        group.MapPost("/{supplementId}/pdf-evidence/operations/query", async (string id, string supplementId, OrderDraftRecoveryDto request, HttpContext http,
            PaymentSupplementEvidenceService service, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); Private(http);
            var row = await service.RecoverPdfReservationAsync(Actor(http), Owner(id), Owner(supplementId), OwnRevision(http, 1), request.Key, ct);
            return TypedResults.Ok(new PaymentUploadRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Reservation(row)));
        }).WithName("RecoverPaymentSupplementPdfReservation");
        group.MapPost("/{supplementId}/pdf-evidence/{reservationId}/content", async (string id, string supplementId, string reservationId,
            HttpContext http, PaymentSupplementEvidenceService service, [FromHeader(Name = "If-Match")] string expected,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            if (!string.Equals(http.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) || http.Request.Headers.ContainsKey("Content-Encoding"))
                throw new BadHttpRequestException("Use unencoded original bytes", StatusCodes.Status415UnsupportedMediaType);
            using var source = new PaymentEvidenceBodyStream(http.Request.Body);
            var result = await service.UploadPdfAsync(Actor(http), Owner(id), Owner(supplementId), Owner(reservationId), OwnRevision(http, 1), source,
                Command(http, "Unggah lampiran PDF tambahan untuk pemeriksaan keamanan; foto utama tetap wajib"), ct);
            if (source.Exceeded) throw new BadHttpRequestException("Upload too large", StatusCodes.Status413PayloadTooLarge);
            Private(http); return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : PaymentSupplementDtos.Reference(result.Evidence.Reference)));
        }).WithMetadata(PaymentEvidenceBodyStream.Metadata).Accepts<byte[]>("application/octet-stream").WithName("UploadPaymentSupplementPdfEvidence");
        group.MapPost("/{supplementId}/pdf-evidence/{reservationId}/recover", async (string id, string supplementId, string reservationId,
            PaymentEvidenceRecoverDto request, HttpContext http, PaymentSupplementEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            Purpose(request.Purpose, "upload-recovery"); OwnRevision(http, 1); Private(http);
            var result = await service.RecoverPdfUploadAsync(Actor(http), Owner(id), Owner(supplementId), Owner(reservationId), Command(http, request.Reason), ct);
            return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : PaymentSupplementDtos.Reference(result.Evidence.Reference)));
        }).WithName("RecoverPaymentSupplementPdfUpload");
    }
    private static PaymentUploadReservationDto Reservation(ReservationResult row) => new(new(row.ReservationId), OrderDraftInputDto.Number(row.Revision), row.ExpiresAt);
    private static PaymentEvidenceStatusDto Status(SafeStatus value) => new(new(value.ReservationId), value.State, OrderDraftInputDto.Number(value.Revision), value.ExpiresAt,
        value.Reference is null ? null : PaymentSupplementDtos.Reference(value.Reference), value.ScanStatus, value.DecoderStatus, value.SafeFailure);
    private static RouteGroupBuilder Group(WebApplication app, string route) {
        var group = app.MapGroup(route).RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var code in new[] {400, 401, 403, 409, 413, 415, 503}) group.WithMetadata(new ProducesResponseTypeMetadata(code, typeof(ApiProblem), ["application/problem+json"]));
        return group;
    }
    private static UnitToko.Modules.Identity.Contracts.ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()!.Actor;
    private static long Owner(string value) {var id = LongId.Parse(value).Value; PaymentSupplementRules.Id(id); return id;}
    private static long ParentRevision(HttpContext http) {var revision = AccessRequestMetadata.ExpectedVersion(http.Request); PaymentSupplementRules.Id(revision); return revision;}
    private static long OwnRevision(HttpContext http, long expected) {var revision = AccessRequestMetadata.ExpectedVersion(http.Request);
        if (revision != expected) throw new PersistenceConflictException("payment_supplement_changed", "Muat lampiran terbaru sebelum melanjutkan"); return revision;}
    private static void OwnRecoveryRevision(HttpContext http) {if (AccessRequestMetadata.ExpectedVersion(http.Request) is not (1 or 2)) throw PaymentSupplementRules.Invalid();}
    private static AuthorizationCommandMetadata Command(HttpContext http, string reason) => new(AccessRequestMetadata.IdempotencyKey(http.Request), reason, http.TraceIdentifier);
    private static AuthorizationReadMetadata Read(HttpContext http, string purpose) => new(purpose, http.TraceIdentifier);
    private static void Purpose(string actual, string expected) {if (actual != expected) throw PaymentSupplementRules.Invalid();}
    private static PaymentSupplementRecoveryDto Recovery(PaymentSupplementOperationRecovery receipt) {
        if (receipt.Status is not ("not_confirmed" or "committed") || (receipt.Status == "committed") != (receipt.Result is not null)) throw PaymentSupplementRules.Invalid();
        return new(receipt.Status, receipt.Result is null ? null : PaymentSupplementDtos.View(receipt.Result));
    }
    private static void Private(HttpContext http) {http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; http.Response.Headers.XContentTypeOptions = "nosniff";}
}
