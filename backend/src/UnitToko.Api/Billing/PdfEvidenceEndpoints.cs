using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Procurement;

namespace UnitToko.Api.Billing;

// Only mapped by the full profile. The branch-order pilot never acquires financial PDF routes.
public static class PdfEvidenceEndpoints
{
    public static void MapPdfEvidenceEndpoints(this WebApplication app) {
        Map(app, "/api/v1/billing/payment-declarations/{id}/pdf-evidence", false);
        Map(app, "/api/v1/procurement/supplier-documents/drafts/{id}/pdf-evidence", true);
    }
    private static void Map(WebApplication app, string route, bool supplier) {
        var prefix = supplier ? "SupplierDocumentPdf" : "PaymentPdf";
        var purpose = supplier ? "supplier-document-pdf-evidence" : "payment-pdf-evidence";
        var readPurpose = supplier ? "supplier-document-evidence-view" : "payment-evidence-view";
        var group = app.MapGroup(route).RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var code in new[] { 400, 401, 403, 409, 413, 415, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(code, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (string id, InvoiceQueryDto request, HttpContext http, PaymentEvidenceService payment,
            SupplierDocumentEvidenceService documents, CancellationToken ct) => {
            Purpose(request.Purpose, purpose); var actor = http.Features.Get<ResolvedActor>()!.Actor; var ownerId = LongId.Parse(id).Value;
            var rows = supplier ? await documents.ListPdfsAsync(actor, ownerId, request.BeforeId?.Value, ct) : await payment.ListPdfsAsync(actor, ownerId, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new PaymentEvidencePageDto([.. rows.Select(Status)]));
        }).WithName("List" + prefix + "Evidence");
        group.MapPost("", async (string id, PaymentEvidenceReserveDto request, HttpContext http, PaymentEvidenceService payment,
            SupplierDocumentEvidenceService documents, [FromHeader(Name = "Idempotency-Key")] string key,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            var actor = http.Features.Get<ResolvedActor>()!.Actor; var ownerId = LongId.Parse(id).Value; var revision = AccessRequestMetadata.ExpectedVersion(http.Request);
            var metadata = new UnitToko.Platform.Authorization.AuthorizationCommandMetadata(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier);
            var row = supplier ? await documents.ReservePdfAsync(actor, ownerId, revision, metadata, ct) : await payment.ReservePdfAsync(actor, ownerId, revision, metadata, ct);
            return TypedResults.Ok(Reservation(row));
        }).WithName("Reserve" + prefix + "Evidence");
        group.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, PaymentEvidenceService payment,
            SupplierDocumentEvidenceService documents, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); var actor = http.Features.Get<ResolvedActor>()!.Actor; var ownerId = LongId.Parse(id).Value;
            var row = supplier ? await documents.RecoverPdfReservationAsync(actor, ownerId, request.Key, ct) : await payment.RecoverPdfReservationAsync(actor, ownerId, request.Key, ct);
            return TypedResults.Ok(new PaymentUploadRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Reservation(row)));
        }).WithName("Recover" + prefix + "EvidenceReservation");
        group.MapPost("/{reservationId}/query", async (string id, string reservationId, OrderReadDto request, HttpContext http,
            PaymentEvidenceService payment, SupplierDocumentEvidenceService documents, CancellationToken ct) => {
            Purpose(request.Purpose, purpose); var actor = http.Features.Get<ResolvedActor>()!.Actor; var ownerId = LongId.Parse(id).Value; var reserved = LongId.Parse(reservationId).Value;
            var row = supplier ? await documents.PdfStatusAsync(actor, ownerId, reserved, ct) : await payment.PdfStatusAsync(actor, ownerId, reserved, ct);
            return TypedResults.Ok(Status(row));
        }).WithName("Read" + prefix + "EvidenceStatus");
        group.MapPost("/{reservationId}/content", async (string id, string reservationId, HttpContext http, PaymentEvidenceService payment,
            SupplierDocumentEvidenceService documents, [FromHeader(Name = "Idempotency-Key")] string key,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (!string.Equals(http.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) || http.Request.Headers.ContainsKey("Content-Encoding"))
                throw new BadHttpRequestException("Use unencoded original bytes", StatusCodes.Status415UnsupportedMediaType);
            var actor = http.Features.Get<ResolvedActor>()!.Actor; var ownerId = LongId.Parse(id).Value; var reserved = LongId.Parse(reservationId).Value;
            var metadata = new UnitToko.Platform.Authorization.AuthorizationCommandMetadata(AccessRequestMetadata.IdempotencyKey(http.Request), "Upload original PDF evidence for security validation only", http.TraceIdentifier);
            using var source = new PaymentEvidenceBodyStream(http.Request.Body); var revision = AccessRequestMetadata.ExpectedVersion(http.Request);
            var result = supplier ? await documents.UploadPdfAsync(actor, ownerId, reserved, revision, source, metadata, ct) : await payment.UploadPdfAsync(actor, ownerId, reserved, revision, source, metadata, ct);
            if (source.Exceeded) throw new BadHttpRequestException("Upload too large", StatusCodes.Status413PayloadTooLarge);
            return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : Reference(result.Evidence.Reference)));
        }).WithMetadata(PaymentEvidenceBodyStream.Metadata).Accepts<byte[]>("application/octet-stream").WithName("Upload" + prefix + "Evidence");
        group.MapPost("/{reservationId}/recover", async (string id, string reservationId, PaymentEvidenceRecoverDto request, HttpContext http,
            PaymentEvidenceService payment, SupplierDocumentEvidenceService documents, [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            Purpose(request.Purpose, "upload-recovery"); var actor = http.Features.Get<ResolvedActor>()!.Actor; var ownerId = LongId.Parse(id).Value; var reserved = LongId.Parse(reservationId).Value;
            var metadata = new UnitToko.Platform.Authorization.AuthorizationCommandMetadata(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier);
            var result = supplier ? await documents.RecoverPdfUploadAsync(actor, ownerId, reserved, metadata, ct) : await payment.RecoverPdfUploadAsync(actor, ownerId, reserved, metadata, ct);
            return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : Reference(result.Evidence.Reference)));
        }).WithName("Recover" + prefix + "EvidenceUpload");
        group.MapPost("/{reservationId}/original/query", async (string id, string reservationId, PaymentEvidenceReadDto request, HttpContext http,
            PaymentEvidenceService payment, SupplierDocumentEvidenceService documents, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, readPurpose); if (request.Reference is null || http.Request.Headers.ContainsKey("Range")) throw PaymentDraftEndpoints.Invalid();
            var actor = http.Features.Get<ResolvedActor>()!.Actor; var ownerId = LongId.Parse(id).Value; var reserved = LongId.Parse(reservationId).Value;
            var reference = new EvidenceRef(request.Reference.Id.Value, LongId.Parse(request.Reference.Version).Value, request.Reference.Sha256);
            var metadata = new UnitToko.Platform.Authorization.AuthorizationReadMetadata(request.Purpose, http.TraceIdentifier); var revision = AccessRequestMetadata.ExpectedVersion(http.Request);
            var file = supplier ? await documents.OpenPdfReadAsync(actor, ownerId, reserved, revision, reference, metadata, ct) : await payment.OpenPdfReadAsync(actor, ownerId, reserved, revision, reference, metadata, ct);
            http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache";
            http.Response.Headers.XContentTypeOptions = "nosniff"; http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Response.Headers["Referrer-Policy"] = "no-referrer"; http.Response.ContentLength = file.Length;
            return Results.Stream(file.Content, file.Mime, $"bukti-{reference.Id}-{reference.Version}.pdf", enableRangeProcessing: false);
        }).Produces<byte[]>(200, "application/pdf").WithName("Read" + prefix + "EvidenceOriginal");
    }
    private static void Purpose(string actual, string expected) { if (actual != expected) throw PaymentDraftEndpoints.Invalid(); }
    private static PaymentUploadReservationDto Reservation(ReservationResult row) => new(new(row.ReservationId), OrderDraftInputDto.Number(row.Revision), row.ExpiresAt);
    private static PaymentEvidenceReferenceDto Reference(EvidenceRef value) => new(new(value.Id), OrderDraftInputDto.Number(value.Version), value.Sha256);
    private static PaymentEvidenceStatusDto Status(SafeStatus value) => new(new(value.ReservationId), value.State, OrderDraftInputDto.Number(value.Revision), value.ExpiresAt,
        value.Reference is null ? null : Reference(value.Reference), value.ScanStatus, value.DecoderStatus, value.SafeFailure);
}
